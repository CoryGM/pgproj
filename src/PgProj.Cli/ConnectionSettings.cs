namespace PgProj.Cli;

/// <summary>
/// Which database a command connects to (G-304a). A connection is directional: the <b>source</b> is a database a command reads (<c>init</c> scaffolds a project from it,
/// <c>document</c> documents it), the <b>target</b> is the database <c>diff</c> compares the project with and <c>publish</c> changes. Each is taken from, in order,
/// the command-line option, an environment variable (so a CI job keeps a password off the command line), and <c>pgproj.json</c> (the target's
/// <c>targetConnectionString</c>, the source's <c>sourceConnectionString</c>).
/// </summary>
public static class ConnectionSettings
{
    public const string SourceVariable = "PGPROJ_SOURCE_CONNECTION";

    public const string TargetVariable = "PGPROJ_TARGET_CONNECTION";

    /// <summary>The target as the shell knows it, before the project is read: the option, then the environment variable; null when neither is set. The engine then falls back to the project's.</summary>
    public static string? ShellTarget(string? optionValue)
        => FirstNonBlank(optionValue, Environment.GetEnvironmentVariable(TargetVariable));

    public static string ResolveTarget(string? optionValue, string? projectValue)
        => ResolveTarget(optionValue, projectValue, Environment.GetEnvironmentVariable);

    public static string ResolveTarget(string? optionValue, string? projectValue, Func<string, string?> environment)
        => FirstNonBlank(optionValue, environment(TargetVariable), projectValue)
            ?? throw new InvalidOperationException(
                $"A PostgreSQL target connection string is required. Specify --target-connection, set {TargetVariable}, or add targetConnectionString to pgproj.json.");

    public static string ResolveSource(string? optionValue, string? projectValue)
        => ResolveSource(optionValue, projectValue, Environment.GetEnvironmentVariable);

    public static string ResolveSource(string? optionValue, string? projectValue, Func<string, string?> environment)
        => FirstNonBlank(optionValue, environment(SourceVariable), projectValue)
            ?? throw new InvalidOperationException(
                $"A PostgreSQL source connection string is required. Specify --source-connection, set {SourceVariable}, or add sourceConnectionString to pgproj.json.");

    /// <summary>The source for <c>init</c>, which has no <c>pgproj.json</c> to read yet: the option, then the variable.</summary>
    public static string ResolveInitSource(string? optionValue)
        => ResolveInitSource(optionValue, Environment.GetEnvironmentVariable);

    public static string ResolveInitSource(string? optionValue, Func<string, string?> environment)
        => FirstNonBlank(optionValue, environment(SourceVariable))
            ?? throw new InvalidOperationException(
                $"A PostgreSQL source connection string is required. Specify --source-connection or set {SourceVariable}.");

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
}
