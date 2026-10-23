using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>
/// A column type change that keeps every value the column can hold (G-281, approved by the user 2026-10-13), so it is not destructive and
/// <c>publish --execute</c> runs it without <c>--allow-destructive</c>. Whether it rewrites the table is a different question, answered by the lock
/// warning (G-270): <c>int</c> to <c>bigint</c> keeps every value and rewrites the table.
/// </summary>
public static class ColumnTypeWidening
{
    /// <summary>
    /// True for: <c>varchar</c> or <c>char</c> made longer or unbounded, or turned into <c>varchar</c> or <c>text</c> at least as long; <c>numeric</c> with at
    /// least as many digits before and after the point, or unconstrained; <c>smallint</c> to <c>int</c> or <c>bigint</c>, and <c>int</c> to <c>bigint</c>;
    /// <c>real</c> to <c>double precision</c>; <c>varbit</c> made longer or unbounded; <c>timestamp</c>, <c>timestamptz</c>, <c>time</c>, or <c>timetz</c> given
    /// more fractional digits. Array-ness must not change. Anything else, a domain included, is not known to keep every value.
    /// </summary>
    public static bool KeepsEveryValue(ColumnDefinition oldColumn, ColumnDefinition newColumn)
    {
        ArgumentNullException.ThrowIfNull(oldColumn);
        ArgumentNullException.ThrowIfNull(newColumn);
        if (oldColumn.ArrayDimensions != newColumn.ArrayDimensions)
        {
            return false;
        }

        var oldBase = BaseType(oldColumn.DataType);
        var newBase = BaseType(newColumn.DataType);
        if (!TryModifiers(oldColumn.TypeModifiers, out var oldModifiers) || !TryModifiers(newColumn.TypeModifiers, out var newModifiers))
        {
            return false;
        }

        return (oldBase, newBase) switch
        {
            ("int2", "int4" or "int8") or ("int4", "int8") or ("float4", "float8") => oldModifiers.Length == 0 && newModifiers.Length == 0,
            ("varchar" or "bpchar", "text") => true,
            ("varchar" or "bpchar", "varchar") or ("bpchar", "bpchar") or ("varbit", "varbit") => AtLeastAsLong(oldModifiers, newModifiers),
            ("numeric", "numeric") => KeepsDigits(oldModifiers, newModifiers),
            ("timestamp", "timestamp") or ("timestamptz", "timestamptz") or ("time", "time") or ("timetz", "timetz") => KeepsFraction(oldModifiers, newModifiers),
            _ => false,
        };
    }

    // An unbounded type (no modifier) holds every value of a bounded one; char without a length is char(1).
    private static bool AtLeastAsLong(int[] oldModifiers, int[] newModifiers)
        => newModifiers.Length == 0 || (oldModifiers.Length > 0 && newModifiers[0] >= oldModifiers[0]);

    // numeric(p, s) holds p - s digits before the point and s after; unconstrained numeric holds both.
    private static bool KeepsDigits(int[] oldModifiers, int[] newModifiers)
    {
        if (newModifiers.Length == 0)
        {
            return true;
        }

        if (oldModifiers.Length == 0)
        {
            return false;
        }

        var (oldPrecision, oldScale) = (oldModifiers[0], oldModifiers.ElementAtOrDefault(1));
        var (newPrecision, newScale) = (newModifiers[0], newModifiers.ElementAtOrDefault(1));
        return newScale >= oldScale && newPrecision - newScale >= oldPrecision - oldScale;
    }

    // A time type without a precision keeps six fractional digits, the most there is.
    private static bool KeepsFraction(int[] oldModifiers, int[] newModifiers)
        => (newModifiers.Length == 0 ? 6 : newModifiers[0]) >= (oldModifiers.Length == 0 ? 6 : oldModifiers[0]);

    private static bool TryModifiers(IReadOnlyList<string> modifiers, out int[] values)
    {
        values = new int[modifiers.Count];
        for (var index = 0; index < modifiers.Count; index++)
        {
            if (!int.TryParse(modifiers[index].Trim(), out values[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string BaseType(string? dataType)
    {
        var name = (dataType ?? string.Empty).Trim().ToLowerInvariant();
        if (name.StartsWith("pg_catalog.", StringComparison.Ordinal))
        {
            name = name["pg_catalog.".Length..];
        }

        return name switch
        {
            "smallint" => "int2",
            "integer" or "int" => "int4",
            "bigint" => "int8",
            "real" => "float4",
            "double precision" => "float8",
            "character varying" => "varchar",
            "character" or "char" => "bpchar",
            "bit varying" => "varbit",
            "decimal" => "numeric",
            "timestamp without time zone" => "timestamp",
            "timestamp with time zone" => "timestamptz",
            "time without time zone" => "time",
            "time with time zone" => "timetz",
            _ => name,
        };
    }
}
