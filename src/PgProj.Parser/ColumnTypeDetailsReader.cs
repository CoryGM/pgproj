using System.Globalization;
using System.Text.Json;

namespace PgProj.Parser;

/// <summary>
/// The parts of a declared column type that the base type name does not carry: type modifiers such as the
/// <c>(10,2)</c> of <c>numeric(10,2)</c>, array dimensions, and the field restriction of an <c>interval</c>.
/// </summary>
internal sealed record ColumnTypeDetails(IReadOnlyList<string> Modifiers, int ArrayDimensions, string? IntervalFields)
{
    public static ColumnTypeDetails None { get; } = new([], 0, null);
}

/// <summary>
/// Reads type modifiers and array bounds from the <c>typeName</c> node of a column definition. The parser
/// previously kept only the type's name, so <c>numeric(10,2)</c> became <c>numeric</c> and <c>text[]</c> became
/// <c>text</c> (G-45).
/// </summary>
internal static class ColumnTypeDetailsReader
{
    // Bit positions of the interval field masks (PostgreSQL's INTERVAL_MASK(b) = 1 << b).
    private static readonly (int Bit, string Name)[] IntervalFieldOrder =
    [
        (2, "year"),
        (1, "month"),
        (3, "day"),
        (10, "hour"),
        (11, "minute"),
        (12, "second"),
    ];

    private const int IntervalFullRange = 0x7FFF;

    public static ColumnTypeDetails Read(JsonElement columnDefinition)
    {
        if (columnDefinition.ValueKind != JsonValueKind.Object
            || !columnDefinition.TryGetProperty("typeName", out var typeName))
        {
            return ColumnTypeDetails.None;
        }

        return ReadTypeName(typeName);
    }

    /// <summary>
    /// Reads the details from a <c>TypeName</c> node directly, for callers whose type lives under another
    /// property (a routine's <c>argType</c> or <c>returnType</c>).
    /// </summary>
    public static ColumnTypeDetails ReadTypeName(JsonElement typeName)
    {
        if (typeName.ValueKind != JsonValueKind.Object)
        {
            return ColumnTypeDetails.None;
        }

        var arrayDimensions = typeName.TryGetProperty("arrayBounds", out var bounds) && bounds.ValueKind == JsonValueKind.Array
            ? bounds.GetArrayLength()
            : 0;

        var modifiers = new List<string>();
        if (typeName.TryGetProperty("typmods", out var typmods) && typmods.ValueKind == JsonValueKind.Array)
        {
            foreach (var modifier in typmods.EnumerateArray())
            {
                modifiers.Add(RenderModifier(modifier));
            }
        }

        if (IsInterval(typeName) && modifiers.Count > 0 && int.TryParse(modifiers[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mask))
        {
            // The first modifier of an interval is the field mask; the optional second is the precision.
            return new ColumnTypeDetails(modifiers.Skip(1).ToArray(), arrayDimensions, DecodeIntervalFields(mask));
        }

        return new ColumnTypeDetails(modifiers, arrayDimensions, null);
    }

    private static bool IsInterval(JsonElement typeName)
    {
        if (!typeName.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array || names.GetArrayLength() == 0)
        {
            return false;
        }

        var last = names[names.GetArrayLength() - 1];
        return last.TryGetProperty("String", out var text)
            && text.TryGetProperty("sval", out var value)
            && string.Equals(value.GetString(), "interval", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turns a field mask into <c>year</c>, <c>day to second</c>, and so on; the full range is no restriction.</summary>
    private static string? DecodeIntervalFields(int mask)
    {
        if (mask == IntervalFullRange || mask == 0)
        {
            return null;
        }

        var present = IntervalFieldOrder.Where(field => (mask & (1 << field.Bit)) != 0).Select(static field => field.Name).ToArray();
        return present.Length switch
        {
            0 => null,
            1 => present[0],
            _ => $"{present[0]} to {present[^1]}",
        };
    }

    private static string RenderModifier(JsonElement modifier)
    {
        if (modifier.ValueKind == JsonValueKind.Object)
        {
            if (modifier.TryGetProperty("A_Const", out var constant))
            {
                return RenderConstant(constant);
            }

            if (modifier.TryGetProperty("ColumnRef", out var columnRef)
                && columnRef.TryGetProperty("fields", out var fields)
                && fields.ValueKind == JsonValueKind.Array)
            {
                // A bare word such as the Point of geometry(Point, 4326).
                return string.Join('.', fields.EnumerateArray()
                    .Select(static field => field.TryGetProperty("String", out var text) && text.TryGetProperty("sval", out var value) ? value.GetString() : null)
                    .Where(static part => !string.IsNullOrEmpty(part)));
            }
        }

        return string.Empty;
    }

    private static string RenderConstant(JsonElement constant)
    {
        // The JSON omits zero values, so an integer constant with no inner "ival" is 0 (for example numeric(10,0)).
        if (constant.TryGetProperty("ival", out var integer))
        {
            return integer.ValueKind == JsonValueKind.Object && integer.TryGetProperty("ival", out var value)
                ? value.GetInt32().ToString(CultureInfo.InvariantCulture)
                : "0";
        }

        if (constant.TryGetProperty("sval", out var text) && text.TryGetProperty("sval", out var stringValue))
        {
            return "'" + (stringValue.GetString() ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        if (constant.TryGetProperty("fval", out var number) && number.TryGetProperty("fval", out var numberValue))
        {
            return numberValue.GetString() ?? string.Empty;
        }

        return string.Empty;
    }
}
