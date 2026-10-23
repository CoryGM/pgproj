using System.Text.RegularExpressions;

namespace PgProj.Semantics;

/// <summary>
/// A type of a routine's parameter or result in the form it is compared in (G-65, G-66): its name as PostgreSQL has it (G-206), without <c>pg_catalog.</c>,
/// without modifiers (PostgreSQL does not keep them for routine parameters, so <c>varchar(10)</c> is <c>varchar</c>), and with
/// the long spellings folded (<c>integer</c> and <c>pg_catalog.int4</c> are one type, and so are <c>character varying</c> and
/// <c>varchar</c>), and without a leading <c>public.</c>, which a live read (pinned to the <c>public</c> search path) never writes and a project may
/// (G-137). The type as written stays on the routine for deployment; this is for comparison only.
/// </summary>
public static class RoutineTypeNames
{
    private static readonly Regex Modifiers = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Canonicalize(string? dataType)
    {
        // A type's name keeps its case (G-206): pg_query has folded an unquoted one, and "Mood" and mood are two types.
        var text = Modifiers.Replace((dataType ?? "text").Trim(), string.Empty);
        text = Whitespace.Replace(text, " ").Trim();

        var arrayMarks = string.Empty;
        while (text.EndsWith("[]", StringComparison.Ordinal))
        {
            arrayMarks += "[]";
            text = text[..^2].TrimEnd();
        }

        var baseType = ColumnTypeNormalizer.NormalizeBaseType(text);

        return (baseType.StartsWith("public.", StringComparison.Ordinal) ? baseType["public.".Length..] : baseType) + arrayMarks;
    }
}
