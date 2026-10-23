using System.Text.Json;

namespace PgProj.Semantics;

/// <summary>
/// Reads what a <c>CHECK</c> constraint, an index predicate, or an index key expression refers to from pg_query's tree for it (G-72).
/// The shallow text parser in <see cref="ExpressionMappingPass"/> splits only on symbolic operators, so <c>a &gt; 0 AND a &lt; 9</c>,
/// <c>BETWEEN</c>, <c>IN</c>, <c>IS NULL</c>, and <c>CASE</c> were read as one identifier or as calls to functions named <c>NOT</c> or
/// <c>IN</c>, and every such expression failed the build. In the tree a column is a <c>ColumnRef</c>, a function is a <c>FuncCall</c>, and
/// nothing else is either, so the words of the SQL grammar cannot be mistaken for names.
/// </summary>
internal static class ExpressionAstReader
{
    /// <summary>Column references (each as its name parts), function names, and cast target types found in the tree.</summary>
    internal sealed record References(
        IReadOnlyList<IReadOnlyList<string>> Columns,
        IReadOnlyList<string> Functions,
        IReadOnlyList<string> Types)
    {
        /// <summary>Operator names as written (<c>===</c> or <c>schema.===</c>), from each operator expression (G-138).</summary>
        public IReadOnlyList<string> Operators { get; init; } = [];
    }

    public static References Collect(string? astJson)
    {
        var columns = new List<IReadOnlyList<string>>();
        var functions = new List<string>();
        var types = new List<string>();
        var operators = new List<string>();

        if (TryParse(astJson, out var document))
        {
            using (document)
            {
                CollectFrom(document.RootElement, columns, functions, types, operators);
            }
        }

        return new References(columns, functions, types) { Operators = operators };
    }

    /// <summary>
    /// The expression as an <see cref="ExpressionNode"/> tree whose identifiers are exactly the columns and whose function calls are exactly
    /// the functions, or null when the tree cannot be read. Structure that carries no reference (operators, <c>CASE</c>, tests) is kept
    /// as grouping nodes without text.
    /// </summary>
    public static ExpressionNode? ToExpressionNode(string? astJson)
    {
        if (!TryParse(astJson, out var document))
        {
            return null;
        }

        using (document)
        {
            var nodes = new List<ExpressionNode>();
            Build(document.RootElement, nodes);
            return nodes.Count switch
            {
                0 => null,
                1 => nodes[0],
                _ => ExpressionNode.Simple(string.Empty, nodes),
            };
        }
    }

    private static bool TryParse(string? astJson, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(astJson))
        {
            return false;
        }

        try
        {
            document = PgProj.Parser.PgQueryJson.Parse(astJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void CollectFrom(JsonElement element, List<IReadOnlyList<string>> columns, List<string> functions, List<string> types, List<string> operators)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectFrom(item, columns, functions, types, operators);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "ColumnRef":
                            if (ReadNames(property.Value, "fields") is { Count: > 0 } parts)
                            {
                                columns.Add(parts);
                            }

                            break;
                        case "FuncCall":
                            if (ReadNames(property.Value, "funcname") is { Count: > 0 } name)
                            {
                                functions.Add(string.Join('.', name));
                            }

                            CollectFrom(property.Value, columns, functions, types, operators);
                            break;
                        case "TypeCast":
                            if (property.Value.TryGetProperty("typeName", out var typeName)
                                && ReadNames(typeName, "names") is { Count: > 0 } typeParts)
                            {
                                types.Add(string.Join('.', typeParts.Where(static part => !string.Equals(part, "pg_catalog", StringComparison.OrdinalIgnoreCase))));
                            }

                            CollectFrom(property.Value, columns, functions, types, operators);
                            break;
                        case "A_Expr":
                            if (ReadNames(property.Value, "name") is { Count: > 0 } operatorName)
                            {
                                operators.Add(string.Join('.', operatorName));
                            }

                            CollectFrom(property.Value, columns, functions, types, operators);
                            break;
                        case "SubLink":
                            // A subquery has its own scope; PostgreSQL does not allow one in a CHECK or an index anyway.
                            break;
                        default:
                            CollectFrom(property.Value, columns, functions, types, operators);
                            break;
                    }
                }

                break;
        }
    }

    private static void Build(JsonElement element, List<ExpressionNode> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Build(item, into);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Length == 0 || !char.IsUpper(property.Name[0]))
                    {
                        // A field of a node (lexpr, args, ...): its value holds the nodes.
                        Build(property.Value, into);
                        continue;
                    }

                    switch (property.Name)
                    {
                        case "ColumnRef":
                            if (ReadNames(property.Value, "fields") is { Count: > 0 } parts)
                            {
                                into.Add(ExpressionNode.Identifier(string.Join('.', parts)));
                            }

                            break;
                        case "FuncCall":
                            var arguments = new List<ExpressionNode>();
                            if (property.Value.TryGetProperty("args", out var args))
                            {
                                Build(args, arguments);
                            }

                            if (ReadNames(property.Value, "funcname") is { Count: > 0 } name)
                            {
                                into.Add(ExpressionNode.FunctionCall(string.Join('.', name), arguments));
                            }

                            break;
                        case "A_Const":
                            into.Add(ExpressionNode.Literal(ConstantText(property.Value)));
                            break;
                        case "TypeCast":
                            if (property.Value.TryGetProperty("arg", out var argument))
                            {
                                Build(argument, into);
                            }

                            break;
                        case "SubLink":
                        case "String":
                            break;
                        default:
                            var children = new List<ExpressionNode>();
                            Build(property.Value, children);
                            if (children.Count > 0)
                            {
                                into.Add(ExpressionNode.Simple(string.Empty, children));
                            }

                            break;
                    }
                }

                break;
        }
    }

    private static List<string>? ReadNames(JsonElement node, string field)
    {
        if (!node.TryGetProperty(field, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetProperty("String", out var text) && text.TryGetProperty("sval", out var value) && value.GetString() is { } part)
            {
                names.Add(part);
            }
            else
            {
                // A_Star (t.*) or anything else that is not a name: not a reference to one column.
                return null;
            }
        }

        return names;
    }

    private static string ConstantText(JsonElement constant)
    {
        if (constant.TryGetProperty("sval", out var text) && text.TryGetProperty("sval", out var value))
        {
            return $"'{value.GetString()}'";
        }

        if (constant.TryGetProperty("ival", out var integer))
        {
            return integer.TryGetProperty("ival", out var number) ? number.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
        }

        if (constant.TryGetProperty("fval", out var real) && real.TryGetProperty("fval", out var realText))
        {
            return realText.GetString() ?? "0";
        }

        if (constant.TryGetProperty("boolval", out var boolean))
        {
            return boolean.TryGetProperty("boolval", out var flag) && flag.GetBoolean() ? "true" : "false";
        }

        return "NULL";
    }
}
