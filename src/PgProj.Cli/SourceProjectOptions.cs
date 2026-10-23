namespace PgProj.Cli;

/// <summary>The <c>--source-connection</c> option of <c>diff</c> and <c>publish</c> (G-304b).</summary>
internal static class SourceProjectOptions
{
    public static System.CommandLine.Option<string?> Create(bool build = false)
        => new("--source-connection")
        {
            HelpName = "connection-string",
            Description = build
                ? "Build a live database (the source) in place of the project's SQL files: it is scaffolded as init does, in a read-only transaction, and built, so --dump-model and --erd show the database and the diagnostics show whether what init would write builds. " +
                    $"With no value, the {ConnectionSettings.SourceVariable} environment variable is used. Only this option on the command line turns the mode on. pgproj.json is optional and still supplies schemas, searchPath, and the exclude options. Not for a project with tenants. build never connects to a target."
                : "Use this live database (the source) as the desired state, in place of the project's SQL files: the target is made to match it. " +
                $"The project's pgproj.json still supplies schemas, searchPath, the exclude options, and the pre/post-deploy scripts, which run against the target; the project file may be absent. " +
                $"With no value, the {ConnectionSettings.SourceVariable} environment variable is used. Only this option on the command line turns the mode on; the variable alone never does. " +
                "The source is read in a read-only transaction and must not be the target. Not for a project with tenants.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
        };
}
