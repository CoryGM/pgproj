namespace PgProj.Semantics;

/// <summary>
/// Extensions that every PostgreSQL database ships with and pgproj never manages. <c>plpgsql</c> is installed in
/// every new database (from <c>template1</c>), so a project that does not declare it must not have it dropped, and
/// one that does declare it (as a project exported from an existing database will) must not try to create it. Both
/// the project model and the live model leave these out, so they never appear in a diff (G-46).
/// </summary>
public static class BuiltInExtensions
{
    private static readonly string[] BuiltInNames = ["plpgsql"];

    /// <summary>The built-in extension names, lower case.</summary>
    public static IReadOnlyList<string> Names => BuiltInNames;

    /// <summary>True when <paramref name="extensionName"/> is a built-in extension that pgproj ignores.</summary>
    public static bool IsBuiltIn(string? extensionName)
        => !string.IsNullOrWhiteSpace(extensionName)
            && BuiltInNames.Contains(extensionName.Trim().Trim('"'), StringComparer.OrdinalIgnoreCase);
}
