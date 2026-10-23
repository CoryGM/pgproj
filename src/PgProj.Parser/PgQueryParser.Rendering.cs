using System.Globalization;
using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    /// <summary>Internal rather than private so <see cref="TypeStatementReader"/> can use it as a fallback for a domain's DEFAULT/CHECK text (G-77).</summary>
    internal static string? TryRenderSqlFragment(JsonElement element)
    {
        var rendered = RenderSqlFragment(element).Trim();
        return string.IsNullOrWhiteSpace(rendered) ? null : rendered;
    }

    private static string RenderSqlFragment(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.ToString();
            case JsonValueKind.Array:
                return string.Join(", ", element.EnumerateArray().Select(RenderSqlFragment).Where(static value => !string.IsNullOrWhiteSpace(value)));
            case JsonValueKind.Object:
                return RenderSqlObject(element);
            default:
                return string.Empty;
        }
    }

    private static string RenderSqlObject(JsonElement element)
    {
        if (TryGetDirectProperty(element, "A_Expr", out var aExpr))
        {
            return RenderAExpr(aExpr);
        }

        if (TryGetDirectProperty(element, "BoolExpr", out var boolExpr))
        {
            return RenderBoolExpr(boolExpr);
        }

        if (TryGetDirectProperty(element, "ColumnRef", out var columnRef))
        {
            return RenderQuotedName(columnRef, "fields", SqlIdentifiers.QuoteName) ?? string.Empty;
        }

        if (TryGetDirectProperty(element, "A_Const", out var aConst))
        {
            return RenderAConst(aConst);
        }

        // pg_query omits a zero from its JSON, so the integer 0 arrives as {"Integer": {}} and used to render as the
        // text "{}" (MINVALUE 0 became MINVALUE {}) (G-57).
        if (TryGetDirectProperty(element, "Integer", out var integerNode))
        {
            return TryGetDirectProperty(integerNode, "ival", out var integerValue) && integerValue.ValueKind == JsonValueKind.Number
                ? integerValue.ToString()
                : "0";
        }

        if (TryGetDirectProperty(element, "TypeCast", out var typeCast))
        {
            return RenderTypeCast(typeCast);
        }

        if (TryGetDirectProperty(element, "FuncCall", out var funcCall))
        {
            return RenderFuncCall(funcCall);
        }

        if (TryGetDirectProperty(element, "NullTest", out var nullTest))
        {
            return RenderNullTest(nullTest);
        }

        if (TryGetDirectProperty(element, "SubLink", out _))
        {
            return element.GetRawText();
        }

        if (TryGetQualifiedName(element, "names") is { Length: > 0 } qualifiedName)
        {
            return qualifiedName;
        }

        if (TryGetQualifiedName(element, "fields") is { Length: > 0 } fieldName)
        {
            return fieldName;
        }

        if (TryGetStringPropertyRecursive(element, "sval") is { Length: > 0 } stringValue)
        {
            return stringValue;
        }

        if (TryGetStringPropertyRecursive(element, "str") is { Length: > 0 } strValue)
        {
            return strValue;
        }

        if (TryGetStringPropertyRecursive(element, "name") is { Length: > 0 } nameValue)
        {
            return nameValue;
        }

        var nestedValues = element.EnumerateObject()
            .Select(property => RenderSqlFragment(property.Value))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return nestedValues.Length == 0
            ? element.GetRawText()
            : string.Join(' ', nestedValues);
    }

    private static string RenderAExpr(JsonElement aExpr)
    {
        var opName = TryGetQualifiedNameFromDirectProperty(aExpr, "name") ?? string.Empty;
        var kind = TryGetDirectStringProperty(aExpr, "kind") ?? string.Empty;
        var hasLeft = TryGetDirectProperty(aExpr, "lexpr", out var lexpr);
        var hasRight = TryGetDirectProperty(aExpr, "rexpr", out var rexpr);

        if ((string.Equals(kind, "AEXPR_BETWEEN", StringComparison.Ordinal)
                || string.Equals(kind, "AEXPR_NOT_BETWEEN", StringComparison.Ordinal))
            && hasLeft && hasRight && rexpr.ValueKind == JsonValueKind.Array)
        {
            var bounds = rexpr.EnumerateArray().Select(RenderSqlFragment).ToArray();
            if (bounds.Length == 2)
            {
                var notText = kind.Contains("NOT", StringComparison.Ordinal) ? "NOT " : string.Empty;
                return $"{RenderSqlFragment(lexpr)} {notText}BETWEEN {bounds[0]} AND {bounds[1]}";
            }
        }

        if (hasLeft && hasRight)
        {
            return $"{RenderSqlFragment(lexpr)} {opName} {RenderSqlFragment(rexpr)}";
        }

        if (hasRight)
        {
            return $"{opName} {RenderSqlFragment(rexpr)}";
        }

        if (hasLeft)
        {
            return $"{RenderSqlFragment(lexpr)} {opName}";
        }

        return opName;
    }

    private static string RenderBoolExpr(JsonElement boolExpr)
    {
        var boolOp = TryGetDirectStringProperty(boolExpr, "boolop") ?? string.Empty;
        if (!TryGetDirectProperty(boolExpr, "args", out var args) || args.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var rendered = args.EnumerateArray()
            .Select(RenderSqlFragment)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return boolOp switch
        {
            "AND_EXPR" => string.Join(" AND ", rendered),
            "OR_EXPR" => string.Join(" OR ", rendered),
            "NOT_EXPR" => rendered.Length > 0 ? $"NOT {rendered[0]}" : "NOT",
            _ => string.Join(' ', rendered),
        };
    }

    private static string RenderAConst(JsonElement aConst)
    {
        if (TryGetDirectProperty(aConst, "isnull", out _))
        {
            return "NULL";
        }

        if (TryGetDirectProperty(aConst, "ival", out var ival))
        {
            return TryGetDirectProperty(ival, "ival", out var innerValue) && innerValue.ValueKind == JsonValueKind.Number
                ? innerValue.ToString()
                : "0";
        }

        if (TryGetDirectProperty(aConst, "fval", out var fval))
        {
            return TryGetDirectStringProperty(fval, "fval") ?? string.Empty;
        }

        if (TryGetDirectProperty(aConst, "boolval", out var boolval))
        {
            return TryGetDirectProperty(boolval, "boolval", out var innerBool) && innerBool.ValueKind == JsonValueKind.True
                ? "true"
                : "false";
        }

        if (TryGetDirectProperty(aConst, "sval", out var sval))
        {
            var text = TryGetDirectStringProperty(sval, "sval") ?? string.Empty;
            return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        return string.Empty;
    }

    /// <summary>
    /// The dotted name in <paramref name="propertyName"/> written so PostgreSQL reads it back as the same identifiers (G-70): the parser's
    /// strings have lost their quotes, so a part with a capital, or that is otherwise not plain, is quoted again. <c>*</c> (<c>old.*</c>) is kept.
    /// </summary>
    private static string? RenderQuotedName(JsonElement element, string propertyName, Func<string, string> quote)
    {
        if (!TryGetDirectProperty(element, propertyName, out var nameElement) || nameElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var part in nameElement.EnumerateArray())
        {
            if (TryGetDirectProperty(part, "A_Star", out _))
            {
                parts.Add("*");
                continue;
            }

            var namePart = TryGetStringPropertyRecursive(part, "sval")
                ?? TryGetStringPropertyRecursive(part, "str")
                ?? (part.ValueKind == JsonValueKind.String ? part.GetString() : null);
            if (!string.IsNullOrWhiteSpace(namePart))
            {
                parts.Add(quote(namePart));
            }
        }

        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    private static string RenderTypeCast(JsonElement typeCast)
    {
        var argText = TryGetDirectProperty(typeCast, "arg", out var arg) ? RenderSqlFragment(arg) : string.Empty;
        var typeText = TryGetDirectProperty(typeCast, "typeName", out var typeName)
            ? RenderQuotedName(typeName, "names", SqlIdentifiers.QuotePlain)
            : null;

        return string.IsNullOrEmpty(typeText) ? argText : $"{argText}::{typeText}";
    }

    private static string RenderFuncCall(JsonElement funcCall)
    {
        var name = RenderQuotedName(funcCall, "funcname", SqlIdentifiers.QuotePlain) ?? string.Empty;
        var args = TryGetDirectProperty(funcCall, "args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
            ? string.Join(", ", argsElement.EnumerateArray().Select(RenderSqlFragment))
            : string.Empty;

        return $"{name}({args})";
    }

    private static string RenderNullTest(JsonElement nullTest)
    {
        var argText = TryGetDirectProperty(nullTest, "arg", out var arg) ? RenderSqlFragment(arg) : string.Empty;
        var testType = TryGetDirectStringProperty(nullTest, "nulltesttype") ?? "IS_NULL";
        return string.Equals(testType, "IS_NOT_NULL", StringComparison.Ordinal)
            ? $"{argText} IS NOT NULL"
            : $"{argText} IS NULL";
    }

    private static string? TryRenderDirectSqlProperty(JsonElement element, string propertyName)
    {
        if (!TryGetDirectProperty(element, propertyName, out var result))
        {
            return null;
        }

        return TryRenderSqlFragment(result);
    }

    private static bool IsScalarJsonValue(JsonElement element)
        => element.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
}
