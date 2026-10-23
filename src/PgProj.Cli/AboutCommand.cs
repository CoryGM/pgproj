using System.CommandLine;
using System.Globalization;
using System.Reflection;
using PgProj.Engine;
using PgProj.Parser;

namespace PgProj.Cli;

/// <summary>
/// <c>pgproj about</c> (G-332): pgproj's version and environment, the project it finds, and the target and source databases with where each connection came
/// from and what the server says, for a bug report. The engine finds it all (<see cref="PgProjEngine.AboutAsync"/>); the command prints it. It exits 0 whatever
/// it could not read: a missing project or an unreachable database is part of the report.
/// </summary>
public static class AboutCommand
{
    public static Command Create(CliContext cliContext)
    {
        ArgumentNullException.ThrowIfNull(cliContext);

        var projectOption = new Option<string>("--project")
        {
            HelpName = "path",
            Description = "Path to the pgproj.json project file to describe. Defaults to 'pgproj.json' in the current directory. A project that is not there is reported, not an error.",
            DefaultValueFactory = _ => "pgproj.json",
        };
        var targetOption = new Option<string?>("--target-connection")
        {
            HelpName = "connection-string",
            Description = "The target database, as diff and publish take it. " +
                $"If not given, the {ConnectionSettings.TargetVariable} environment variable is used, then targetConnectionString from pgproj.json.",
        };
        var sourceOption = new Option<string?>("--source-connection")
        {
            HelpName = "connection-string",
            Description = "The source database, as init and document take it. " +
                $"If not given, the {ConnectionSettings.SourceVariable} environment variable is used, then sourceConnectionString from pgproj.json.",
        };

        var command = new Command("about")
        {
            Description =
                """
                Prints what a bug report needs, in a form safe to paste into an issue:
                  - pgproj: its version, how it was installed, the operating system and its architecture, the .NET runtime, and whether PostgreSQL's parser (libpg_query) loads on this machine.
                  - The project: the pgproj.json it finds (or that it finds none), its name, source folder and number of SQL files, schemas, search path, deploy scripts, and tenants. The project is read, not built: pgproj build checks it.
                  - The target and source databases: where each connection string came from (the option, the environment variable, or pgproj.json, in that order, as the other commands take them), the host, port, database, and user, and, connected to, the server's PostgreSQL version, whether the role is a superuser, and whether it can create temporary objects (without that, diff compares views and defaults as text, which can show changes that are not there).

                A database it cannot reach within five seconds, or that refuses the login, is reported with the reason, and about still exits with 0. It only reads: it changes nothing in a database.

                No password is ever printed. Host names and paths are; remove them before pasting if they are private.

                Examples:
                  pgproj about
                  pgproj about --project ./db/pgproj.json
                  pgproj about --target-connection "Host=localhost;Database=app;Username=postgres"
                """,
        };

        HelpLayout.SetSummary(command, "Print pgproj's version and environment, the project it finds, and its databases, for a bug report.");
        command.Options.Add(projectOption);
        command.Options.Add(targetOption);
        command.Options.Add(sourceOption);
        var logOptions = LogOptions.AddTo(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var log = logOptions.Create(parseResult);
            try
            {
                var (target, targetOrigin) = Shell(parseResult.GetValue(targetOption), "--target-connection", ConnectionSettings.TargetVariable);
                var (source, sourceOrigin) = Shell(parseResult.GetValue(sourceOption), "--source-connection", ConnectionSettings.SourceVariable);
                var result = await new PgProjEngine().AboutAsync(
                    new AboutRequest
                    {
                        ProjectFilePath = parseResult.GetValue(projectOption) ?? "pgproj.json",
                        TargetConnectionString = target,
                        TargetConnectionOrigin = targetOrigin,
                        SourceConnectionString = source,
                        SourceConnectionOrigin = sourceOrigin,
                    },
                    cancellationToken);

                foreach (var line in Lines(result, CliVersion(), InstallKind.Describe()))
                {
                    Console.Out.WriteLine(SecretRedactor.Redact(line));
                }

                return 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                log.Error($"About failed: {exception.Message}");
                return 1;
            }
        });

        return command;
    }

    /// <summary>The connection string as the shell has it, the option then the environment variable, with the name of the one it came from.</summary>
    private static (string? Value, string? Origin) Shell(string? optionValue, string optionName, string variable)
    {
        if (!string.IsNullOrWhiteSpace(optionValue))
        {
            return (optionValue, optionName);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? (null, null) : (fromEnvironment, $"the {variable} environment variable");
    }

    /// <summary>The CLI's own version, as <c>pgproj --version</c> prints it.</summary>
    private static string CliVersion()
        => typeof(AboutCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>The report, line by line.</summary>
    public static IEnumerable<string> Lines(AboutResult result, string cliVersion, string installKind)
    {
        ArgumentNullException.ThrowIfNull(result);

        yield return $"pgproj {cliVersion}" + (result.EngineVersion == cliVersion ? string.Empty : $" (engine {result.EngineVersion})");
        yield return $"  Installed as: {installKind}";
        yield return $"  Operating system: {result.OperatingSystem} ({result.OperatingSystemArchitecture}" +
            (result.ProcessArchitecture == result.OperatingSystemArchitecture ? ")" : $", running as {result.ProcessArchitecture})");
        yield return $"  .NET: {result.Runtime} ({result.RuntimeIdentifier})";
        yield return result.ParserLoaded
            ? "  PostgreSQL parser: loads (libpg_query, PostgreSQL 18's grammar)"
            : $"  PostgreSQL parser: does not load, so no SQL can be read: {result.ParserMessage}";
        yield return string.Empty;

        switch (result.ProjectStatus)
        {
            case AboutProjectStatus.Found:
                var project = result.Project!;
                yield return $"Project: {CliContext.DisplayPath(result.ProjectFilePath!)}";
                yield return $"  Name: {project.Name} (version {project.Version})";
                yield return $"  Source: {CliContext.DisplayPath(project.SourcePath)} ({Count(project.SqlFileCount, "SQL file")})";
                yield return $"  Schemas: {(project.Schemas.Count == 0 ? "not listed (every schema the SQL files use)" : string.Join(", ", project.Schemas))}";
                yield return $"  Search path: {string.Join(", ", project.SearchPath)}";
                if (project.PreDeployScript is { } preDeploy)
                {
                    yield return $"  Pre-deploy script: {preDeploy}";
                }

                if (project.PostDeployScript is { } postDeploy)
                {
                    yield return $"  Post-deploy script: {postDeploy}";
                }

                string[] excluded = [.. new[] { (project.ExcludeGrants, "grants"), (project.ExcludeRoleMemberships, "role memberships"), (project.ExcludeOwners, "owners") }.Where(static item => item.Item1).Select(static item => item.Item2)];
                if (excluded.Length > 0)
                {
                    yield return $"  Left out of comparisons: {string.Join(", ", excluded)}";
                }

                foreach (var line in project.TenantLines)
                {
                    yield return $"  {line}";
                }

                break;
            case AboutProjectStatus.NotFound:
                yield return $"Project: none found at {result.ProjectFilePath}";
                break;
            case AboutProjectStatus.Invalid:
                yield return $"Project: {CliContext.DisplayPath(result.ProjectFilePath!)} could not be read: {result.ProjectMessage}";
                break;
            default:
                yield return "Project: none named";
                break;
        }

        yield return string.Empty;
        foreach (var line in Connection("Target database", result.Target))
        {
            yield return line;
        }

        foreach (var line in Connection("Source database", result.Source))
        {
            yield return line;
        }

        yield return string.Empty;
        yield return "No password is shown. Host names and paths are: remove them before pasting this into an issue if they are private.";
    }

    private static IEnumerable<string> Connection(string label, AboutConnection? connection)
    {
        if (connection is null)
        {
            yield return $"{label}: none configured";
            yield break;
        }

        yield return $"{label}: from {connection.Origin}";
        var where = connection.Host is null ? "the default host" : connection.Port is { } port ? $"{connection.Host}:{port.ToString(CultureInfo.InvariantCulture)}" : connection.Host;
        if (connection.Host is not null || connection.Database is not null || connection.Username is not null)
        {
            yield return $"  {where}, database {connection.Database ?? "(the user's)"}, user {connection.Username ?? "(the default)"}";
        }

        switch (connection.Status)
        {
            case AboutConnectionStatus.Connected:
                yield return $"  Server: PostgreSQL {connection.ServerVersion}";
                yield return $"  Superuser: {YesNo(connection.IsSuperuser)}; can create temporary objects: {YesNo(connection.CanCreateTemporaryObjects)}";
                break;
            case AboutConnectionStatus.Failed:
                yield return $"  Could not connect: {connection.Message}";
                break;
            default:
                yield return "  Not connected to.";
                break;
        }
    }

    private static string YesNo(bool? value) => value switch { true => "yes", false => "no", null => "unknown" };

    private static string Count(int count, string noun) => $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";
}
