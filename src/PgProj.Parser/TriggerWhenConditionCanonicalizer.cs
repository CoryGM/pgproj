using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PgProj.Parser;

/// <summary>
/// Turns the parsed <c>WHEN</c> condition of a trigger into a form that is equal for every spelling PostgreSQL treats as the same
/// condition (G-40). The project side is text the author wrote; the live side is <c>pg_get_triggerdef</c>, which deparses the stored
/// expression: fully parenthesized, <c>!=</c> as <c>&lt;&gt;</c>, <c>LIKE</c> as <c>~~</c>, <c>BETWEEN</c> as two comparisons,
/// <c>IN (...)</c> as <c>= ANY (ARRAY[...])</c>, and a <c>::text</c> added to a string constant or a <c>varchar</c> column. Comparing
/// the text would report every one of those as a change, so both sides are compared as pg_query's syntax tree (which has no
/// parentheses, whitespace, or keyword case) with the rewrites above undone.
/// </summary>
/// <remarks>
/// The folds are deliberately conservative, because a fold that equates two conditions that differ hides a real change and
/// <c>publish</c> would report success and leave the trigger stale. A cast is dropped only where PostgreSQL adds it to a constant (a string
/// constant to <c>text</c>/<c>varchar</c>, a negative integer printed <c>'-1'::integer</c>, an integer against a numeric column, the bare
/// constants of an <c>IN</c> list on a <c>varchar</c> column), or from
/// a <c>NEW.col</c> / <c>OLD.col</c> reference whose column the caller says is <c>varchar</c> (where PostgreSQL adds it itself), never from
/// any other operand. Nested <c>AND</c>/<c>OR</c> of the same kind are flattened, which is associativity. What is not folded (a typed
/// literal such as <c>'2020-01-01'::timestamp</c> that PostgreSQL renders with an explicit cast, a numeric constant against a
/// <c>numeric</c> column) can still read as a change, which recreates the trigger in the publish's transaction but never leaves a real
/// difference undetected.
/// </remarks>
public static class TriggerWhenConditionCanonicalizer
{
    private static readonly HashSet<string> SearchPathSchemas = new(StringComparer.OrdinalIgnoreCase) { "pg_catalog", "public" };

    /// <summary>
    /// Returns the canonical form of the condition, or null when there is none or the JSON cannot be read.
    /// <paramref name="columnBaseType"/> answers the base type name of a column of the trigger's table (or null when unknown).
    /// </summary>
    public static string? Canonicalize(string? whenConditionAstJson, Func<string, string?>? columnBaseType = null)
    {
        if (string.IsNullOrWhiteSpace(whenConditionAstJson))
        {
            return null;
        }

        try
        {
            var root = JsonNode.Parse(whenConditionAstJson, documentOptions: PgQueryJson.DocumentOptions);
            if (root is null)
            {
                return null;
            }

            var builder = new StringBuilder();
            Write(builder, Transform(root, columnBaseType));
            return builder.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A key that only says where in the text a node was written, which two spellings of one expression do not share: <c>location</c>,
    /// the statement bounds, and the list bounds PostgreSQL 18 adds to an <c>IN</c> list (<c>rexpr_list_start</c>/<c>rexpr_list_end</c>) and an
    /// array constructor (<c>list_start</c>/<c>list_end</c>).
    /// </summary>
    private static bool IsPositionKey(string key)
        => key is "location" or "stmt_len" or "stmt_location" or "list_start" or "list_end" or "rexpr_list_start" or "rexpr_list_end";

    private static JsonNode? Transform(JsonNode? node, Func<string, string?>? columnBaseType)
    {
        switch (node)
        {
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(Transform(item, columnBaseType));
                }

                return items;
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (IsPositionKey(key))
                    {
                        continue;
                    }

                    result[key] = Transform(value, columnBaseType);
                }

                return Rewrite(result, columnBaseType);
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>Applies the rewrites to a node whose children are already canonical.</summary>
    private static JsonNode Rewrite(JsonObject node, Func<string, string?>? columnBaseType)
    {
        if (node.Count != 1)
        {
            return node;
        }

        var (kind, value) = node.First();
        if (value is not JsonObject body)
        {
            return node;
        }

        return kind switch
        {
            "A_Expr" => RewriteExpression(node, body),
            "TypeCast" => RewriteCast(node, body, columnBaseType),
            "BoolExpr" => FlattenBoolExpression(node, body),
            "FuncCall" => StripSearchPathSchema(node, body, "funcname"),
            _ => node,
        };
    }

    private static JsonNode RewriteExpression(JsonObject node, JsonObject body)
    {
        var kind = body["kind"]?.GetValue<string>();
        switch (kind)
        {
            case "AEXPR_LIKE" or "AEXPR_ILIKE":
                // The operator (~~, !~~, ~~*, !~~*) is what PostgreSQL stores; only the syntax that produced it differs.
                body["kind"] = "AEXPR_OP";
                return node;
            case "AEXPR_BETWEEN" or "AEXPR_NOT_BETWEEN" when TryGetPair(body["rexpr"], out var low, out var high) && body["lexpr"] is { } subject:
                var negated = kind == "AEXPR_NOT_BETWEEN";
                return new JsonObject
                {
                    ["BoolExpr"] = new JsonObject
                    {
                        ["boolop"] = negated ? "OR_EXPR" : "AND_EXPR",
                        ["args"] = new JsonArray(
                            Comparison(negated ? "<" : ">=", subject.DeepClone(), low.DeepClone()),
                            Comparison(negated ? ">" : "<=", subject.DeepClone(), high.DeepClone())),
                    },
                };
            case "AEXPR_IN":
                var inOperator = body["name"] is JsonArray inName ? OperatorName(inName) : null;
                if (body["rexpr"]?["List"]?["items"] is JsonArray inItems && (inOperator == "=" || inOperator == "<>"))
                {
                    body["kind"] = inOperator == "=" ? "AEXPR_OP_ANY" : "AEXPR_OP_ALL";
                    body["rexpr"] = new JsonObject { ["A_ArrayExpr"] = new JsonObject { ["elements"] = inItems.DeepClone() } };
                }

                return node;
            default:
                return node;
        }
    }

    private static JsonNode RewriteCast(JsonObject node, JsonObject body, Func<string, string?>? columnBaseType)
    {
        var typeName = body["typeName"] as JsonObject ?? new JsonObject();
        StripSearchPathSchema(node, typeName, "names");

        var names = (typeName["names"] as JsonArray)?.Select(StringValue).ToArray() ?? [];
        var isArray = typeName["arrayBounds"] is not null;
        var hasModifiers = typeName["typmods"] is not null;
        if (body["arg"] is not JsonObject argument || names.Length != 1 || hasModifiers)
        {
            return node;
        }

        var target = names[0];

        // PostgreSQL types a string constant by what it is compared with: 'x'::text, 'x'::character varying.
        if (!isArray && target is "text" or "varchar" && argument["A_Const"]?["sval"] is not null)
        {
            return argument.DeepClone();
        }

        // A text search configuration or dictionary named by a string constant is printed 'english'::regconfig; the author wrote 'english', which the
        // function's regconfig parameter makes the same value (G-186).
        if (!isArray && target is "regconfig" or "regdictionary" && argument["A_Const"]?["sval"] is not null)
        {
            return argument.DeepClone();
        }

        // PostgreSQL prints a negative integer constant quoted and cast ('-1'::integer), where the author wrote -1.
        if (!isArray && target is "int2" or "int4" or "int8"
            && argument["A_Const"]?["sval"]?["sval"]?.GetValue<string>() is { } digits
            && long.TryParse(digits, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && number.ToString(System.Globalization.CultureInfo.InvariantCulture) == digits)
        {
            return new JsonObject { ["A_Const"] = new JsonObject { ["ival"] = number == 0 ? new JsonObject() : new JsonObject { ["ival"] = number } } };
        }

        // An integer constant compared with a numeric column is printed (0)::numeric; 0 and 0::numeric compare the same.
        if (!isArray && target == "numeric" && argument["A_Const"]?["ival"] is not null)
        {
            return argument.DeepClone();
        }

        // A varchar column in IN (...) is printed as (ARRAY['a'::character varying, ...])::text[]; the author wrote the bare constants.
        if (isArray && target == "text" && argument["A_ArrayExpr"]?["elements"] is JsonArray elements
            && elements.All(static element => element?["A_Const"]?["sval"] is not null))
        {
            return argument.DeepClone();
        }

        // varchar has no equality operator of its own, so PostgreSQL compares (new.col)::text; the author wrote new.col.
        if (!isArray && target == "text"
            && argument["ColumnRef"]?["fields"] is JsonArray fields
            && ColumnOf(fields) is { } column
            && columnBaseType?.Invoke(column) is "varchar")
        {
            return argument.DeepClone();
        }

        return node;
    }

    /// <summary>The column named by <c>NEW.col</c>/<c>OLD.col</c> (a trigger) or a bare <c>col</c> (a generated column's expression), else null.</summary>
    private static string? ColumnOf(JsonArray fields)
        => fields.Count switch
        {
            1 => StringValue(fields[0]),
            2 when StringValue(fields[0]) is "new" or "old" => StringValue(fields[1]),
            _ => null,
        };

    private static JsonNode FlattenBoolExpression(JsonObject node, JsonObject body)
    {
        var operation = body["boolop"]?.GetValue<string>();
        if (operation is not ("AND_EXPR" or "OR_EXPR") || body["args"] is not JsonArray arguments)
        {
            return node;
        }

        var flattened = new JsonArray();
        foreach (var argument in arguments)
        {
            if (argument?["BoolExpr"] is JsonObject nested
                && nested["boolop"]?.GetValue<string>() == operation
                && nested["args"] is JsonArray nestedArguments)
            {
                foreach (var inner in nestedArguments)
                {
                    flattened.Add(inner?.DeepClone());
                }
            }
            else
            {
                flattened.Add(argument?.DeepClone());
            }
        }

        body["args"] = flattened;
        return node;
    }

    /// <summary>PostgreSQL prints a name on the search path without its schema (<c>pg_catalog.lower(x)</c> is <c>lower(x)</c>).</summary>
    private static JsonNode StripSearchPathSchema(JsonObject node, JsonObject body, string namesProperty)
    {
        if (body[namesProperty] is JsonArray { Count: >= 2 } names && StringValue(names[0]) is { } schema && SearchPathSchemas.Contains(schema))
        {
            names.RemoveAt(0);
        }

        return node;
    }

    private static bool TryGetPair(JsonNode? list, out JsonNode low, out JsonNode high)
    {
        low = high = null!;
        if (list?["List"]?["items"] is JsonArray { Count: 2 } items && items[0] is { } first && items[1] is { } second)
        {
            low = first;
            high = second;
            return true;
        }

        return false;
    }

    private static JsonObject Comparison(string op, JsonNode left, JsonNode right)
        => new()
        {
            ["A_Expr"] = new JsonObject
            {
                ["kind"] = "AEXPR_OP",
                ["name"] = new JsonArray(new JsonObject { ["String"] = new JsonObject { ["sval"] = op } }),
                ["lexpr"] = left,
                ["rexpr"] = right,
            },
        };

    private static string? OperatorName(JsonArray name) => name.Count == 1 ? StringValue(name[0]) : null;

    private static string? StringValue(JsonNode? node) => node?["String"]?["sval"]?.GetValue<string>();

    /// <summary>Writes a deterministic form: object properties in ordinal order, arrays in their order.</summary>
    private static void Write(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in obj.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(key).Append(':');
                    Write(builder, value);
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    Write(builder, array[index]);
                }

                builder.Append(']');
                break;
            case null:
                builder.Append("null");
                break;
            default:
                builder.Append(node.ToJsonString());
                break;
        }
    }
}
