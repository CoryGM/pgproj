namespace PgProj.Semantics;

/// <summary>
/// The privileges that make up <c>ALL PRIVILEGES</c> for each kind of object a grant can name (G-61). PostgreSQL reports a
/// privilege list that equals the full set as <c>ALL PRIVILEGES</c>, and a grant's identity includes its privileges, so
/// <c>GRANT USAGE, CREATE ON SCHEMA</c> in a project and <c>ALL PRIVILEGES</c> read back from the catalog were two different
/// grants: one removed and one added, forever. Both sides now use the canonical form.
/// </summary>
internal static class GrantPrivilegeSets
{
    private const string AllPrivileges = "ALL PRIVILEGES";

    private static readonly string[] SchemaPrivileges = ["CREATE", "USAGE"];
    private static readonly string[] SequencePrivileges = ["SELECT", "UPDATE", "USAGE"];
    private static readonly string[] FunctionPrivileges = ["EXECUTE"];
    private static readonly string[] TypePrivileges = ["USAGE"];

    // MAINTAIN exists from PostgreSQL 17. A project written for an earlier version, or a server that is one, lists the other
    // seven for ALL PRIVILEGES, and that must be the same grant.
    private static readonly string[] TablePrivileges = ["DELETE", "INSERT", "REFERENCES", "SELECT", "TRIGGER", "TRUNCATE", "UPDATE"];

    /// <summary>
    /// Returns <c>["ALL PRIVILEGES"]</c> when <paramref name="privileges"/> is the full set for the object that
    /// <paramref name="objectKey"/> names, and the privileges unchanged otherwise (a smaller set is a different grant).
    /// </summary>
    public static IReadOnlyList<string> Canonicalize(string objectKey, IReadOnlyList<string> privileges)
    {
        if (privileges.Count == 1 && string.Equals(privileges[0], AllPrivileges, StringComparison.OrdinalIgnoreCase))
        {
            return privileges;
        }

        var fullSet = FullSetFor(objectKey);
        if (fullSet is null)
        {
            return privileges;
        }

        var given = privileges.Select(static privilege => privilege.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        if (ReferenceEquals(fullSet, TablePrivileges))
        {
            given.Remove("MAINTAIN");
        }

        return given.SetEquals(fullSet) ? [AllPrivileges] : privileges;
    }

    private static string[]? FullSetFor(string objectKey)
    {
        // By prefix, not by substring: a schema or table may be named "tables" (G-65).
        if (StartsWithAny(objectKey, "schema:", "default schemas"))
        {
            return SchemaPrivileges;
        }

        if (StartsWithAny(objectKey, "table:", "all tables ", "default tables"))
        {
            return TablePrivileges;
        }

        if (StartsWithAny(objectKey, "sequence:", "all sequences ", "default sequences"))
        {
            return SequencePrivileges;
        }

        if (StartsWithAny(objectKey, "function:", "procedure:", "all functions ", "default functions"))
        {
            return FunctionPrivileges;
        }

        return StartsWithAny(objectKey, "default types") ? TypePrivileges : null;
    }

    private static bool StartsWithAny(string objectKey, params string[] prefixes)
        => prefixes.Any(prefix => objectKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
