namespace PgProj.Semantics;

/// <summary>
/// How two spellings of a collation's name are told to be the same (G-155, G-156). A project writes <c>COLLATE "C"</c>, <c>COLLATE pg_catalog."C"</c>, or <c>COLLATE ci</c> for
/// <c>app.ci</c>; the database reports <c>pg_catalog."C"</c> and <c>app.ci</c>.
/// </summary>
public static class CollationNames
{
    /// <summary>A collation name without quotes or <c>pg_catalog.</c>, in lower case.</summary>
    public static string Plain(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var unquoted = name.Replace("\"", string.Empty, StringComparison.Ordinal).Trim();
        return (unquoted.StartsWith("pg_catalog.", StringComparison.OrdinalIgnoreCase) ? unquoted["pg_catalog.".Length..] : unquoted).ToLowerInvariant();
    }

    /// <summary>
    /// Whether two collation names, either of which may be missing, name the same collation: equal in <see cref="Plain"/> form, or, when only one has a schema, the same after the
    /// last dot. No collation and <c>default</c> are the same, since the database's default collation is what a column or key without one has.
    /// </summary>
    public static bool Same(string? first, string? second)
    {
        var a = string.IsNullOrWhiteSpace(first) ? "default" : Plain(first);
        var b = string.IsNullOrWhiteSpace(second) ? "default" : Plain(second);
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        return (!a.Contains('.', StringComparison.Ordinal) || !b.Contains('.', StringComparison.Ordinal))
            && string.Equals(a[(a.LastIndexOf('.') + 1)..], b[(b.LastIndexOf('.') + 1)..], StringComparison.Ordinal);
    }
}
