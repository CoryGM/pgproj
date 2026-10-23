namespace PgProj.Semantics;

/// <summary>
/// Canonicalizes the parts of a column type that the base type name does not carry, so two spellings of the
/// same type compare equal while a genuine difference (<c>varchar(10)</c> versus <c>varchar(20)</c>) does not
/// (G-45). The declared values are kept as written for deployment; these forms are for comparison only.
/// </summary>
public static class ColumnTypeNormalizer
{
    /// <summary>
    /// Returns the modifiers as PostgreSQL reports them for the type. <c>numeric(5)</c> is stored, and
    /// reported by <c>format_type</c>, as <c>numeric(5,0)</c>, so a lone precision gets an explicit scale of 0.
    /// (The parser already expands the bare <c>char</c> and <c>bit</c> defaults to a length of 1.)
    /// </summary>
    public static IReadOnlyList<string> NormalizeModifiers(string? dataType, IReadOnlyList<string> modifiers)
    {
        ArgumentNullException.ThrowIfNull(modifiers);

        var trimmed = modifiers.Select(static modifier => modifier.Trim()).ToArray();
        return IsNumeric(dataType) && trimmed.Length == 1
            ? [trimmed[0], "0"]
            : trimmed;
    }

    /// <summary>
    /// The base type name for comparison: trimmed, lower-case, without the <c>pg_catalog.</c> prefix. The parser
    /// leaves a name such as <c>timestamptz</c> or <c>int8</c> bare but rewrites the equivalent
    /// <c>timestamp with time zone</c> or <c>bigint</c> to <c>pg_catalog.timestamptz</c> / <c>pg_catalog.int8</c>,
    /// so without this the same type written two ways compares as different. Deployed SQL keeps the name as written.
    /// </summary>
    public static string NormalizeBaseType(string? dataType)
    {
        var name = dataType?.Trim() ?? string.Empty;
        const string catalogPrefix = "pg_catalog.";
        if (name.StartsWith(catalogPrefix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[catalogPrefix.Length..];
        }

        // A serial column keeps its underlying type spelled as written (integer), while a declared integer reaches the
        // model as pg_catalog.int4, and PostgreSQL reports int4: the same type must compare equal (G-58). Every built-in alias is
        // folded, from the one table (G-207).
        return TypeNameAliases.Canonical(name);
    }

    /// <summary>
    /// <see cref="NormalizeBaseType"/>, with a leading <c>public.</c> also dropped (G-75). <c>public</c> is on the
    /// default search path, so PostgreSQL's own <c>format_type()</c> prints a type declared there — including an
    /// extension type such as <c>public.citext</c> — without its schema; without this, a project that
    /// schema-qualifies such a type never converges against a live column read back unqualified: the diff reports
    /// a phantom modification, and a table alter re-emits a harmless but wasteful <c>ALTER COLUMN ... TYPE ...</c>
    /// no-op on every publish. Deliberately narrower than <see cref="NormalizeBaseType"/> itself: it is used only
    /// where the question is "is this column's own declared type the same type PostgreSQL reports back" (a
    /// column's <see cref="ColumnDefinition.GetComparableTypeSignature"/> and the diff's <c>DataType</c>/<c>RawType</c>
    /// comparison), not by <see cref="RoutineTypeNames"/>'s routine-argument canonicalization, where a schema is
    /// part of a routine's identity (G-67) and stripping it here would rename routine keys as a side effect. A
    /// type in any other schema stays significant, the same boundary <see cref="DefaultExpressionCanonicalizer"/>
    /// already applies to default-expression text (G-63/G-70).
    /// </summary>
    public static string NormalizeBaseTypeForColumnComparison(string? dataType)
    {
        var name = NormalizeBaseType(dataType);
        const string publicPrefix = "public.";
        return name.StartsWith(publicPrefix, StringComparison.OrdinalIgnoreCase)
            ? name[publicPrefix.Length..]
            : name;
    }

    /// <summary>
    /// A canonical text of a full declared type (base type, modifiers, interval fields, array-ness): equal for two spellings of
    /// the same type and different for a genuine change such as <c>varchar(10)</c> to <c>varchar(20)</c>. Shared by a column
    /// (<see cref="ColumnDefinition.GetComparableTypeSignature"/>) and a composite type's field (G-99).
    /// </summary>
    public static string BuildComparableTypeSignature(string? dataType, IReadOnlyList<string> modifiers, int arrayDimensions, string? intervalFields)
    {
        ArgumentNullException.ThrowIfNull(modifiers);

        var normalizedModifiers = NormalizeModifiers(dataType, modifiers);
        var signature = NormalizeBaseTypeForColumnComparison(dataType);
        if (!string.IsNullOrWhiteSpace(intervalFields))
        {
            signature += " " + intervalFields.Trim().ToLowerInvariant();
        }

        if (normalizedModifiers.Count > 0)
        {
            signature += "(" + string.Join(",", normalizedModifiers).ToLowerInvariant() + ")";
        }

        return arrayDimensions > 0 ? signature + "[]" : signature;
    }

    private static bool IsNumeric(string? dataType)
    {
        var name = NormalizeBaseType(dataType);
        return name is "numeric" or "decimal";
    }
}
