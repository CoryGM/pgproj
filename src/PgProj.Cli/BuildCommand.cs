using System.CommandLine;
using System.Text.Json;
using PgProj.Core;
using PgProj.Engine;

namespace PgProj.Cli;

public static class BuildCommand
{
    public static Command Create(CliContext cliContext)
    {
        ArgumentNullException.ThrowIfNull(cliContext);

        var projectOption = CreateProjectOption();
        var dumpModelOption = CreateDumpModelOption();
        var excludeGrantsOption = CreateExcludeGrantsOption();
        var excludeRoleMembershipsOption = CreateExcludeRoleMembershipsOption();
        var excludeOwnersOption = CreateExcludeOwnersOption();
        var sourceOption = SourceProjectOptions.Create(build: true);
        var formatOption = new Option<string>("--format")
        {
            HelpName = "text|json",
            Description = "Output format: 'text' (default) prints the project's header and its diagnostics; 'json' writes the result (the project, the documents read, " +
                "the diagnostics with their places, and the counts) as one JSON document on standard output (everything else goes to standard error), for an IDE or CI.",
            DefaultValueFactory = _ => "text",
        };
        formatOption.AcceptOnlyFromAmong("text", "json");
        var command = new Command("build")
        {
            Description =
                """
                Parse the SQL files referenced by a pgproj.json project, build the semantic model, and print any diagnostics (errors and warnings) discovered while parsing.

                The command exits with code 0 when the model has no errors, or 1 when errors are found.

                With --source-connection, a live database is built in place of the project's SQL files: it is scaffolded as init does (in a read-only transaction), so --dump-model and --erd describe the database, and the diagnostics say whether what init would write builds. Only the option on the command line turns the mode on (a bare option reads PGPROJ_SOURCE_CONNECTION); pgproj.json is optional and still supplies schemas, searchPath, and the exclude options; not for a project with tenants. build never connects to a target.

                A project with tenants ("tenants" in pgproj.json: a template folder every tenant's schema holds, and the prefix of their schema names) has its template read once, in the stand-in schema (the prefix), and checked: a template statement that creates something in a shared schema, writes a schema on the tenant's own objects, refers to one tenant's schema, or holds something that belongs to the whole database is an error, and so is a shared statement that reaches into a tenant. A CSV file of tenants is read and each bad line reported. See docs/notes/tenants.md.

                Diagram:
                  - --erd also writes an entity relationship diagram of the project's tables (Mermaid, from the SQL files): erd-<time>.mmd and a companion erd-<time>.md in the directory of pgproj.json, or the path given after --erd. Nothing is written if the project has errors.
                  - --erd-schemas/--erd-exclude-schemas (not both) narrow the diagram to some schemas; a foreign key out of them is drawn to an (external) stub.
                  - --erd-html also writes a web page (erd-<time>.html) that draws the diagram in a browser; it loads Mermaid from a CDN.
                  - --erd-existing overwrite|backup|keep says what to do with a file already at the path; by default a file --erd wrote is backed up to name.<UTC time>.ext and any other file is kept.

                --format json writes the result as one JSON document on standard output, the envelope diff --format json starts with: schemaVersion, status (succeeded, failed, projectNotFound, projectInvalid, and with --source-connection refused or connectionFailed), the project, the documents read, the diagnostics, each with its file, line, column, endLine, and endColumn when known, the counts by severity, and with --source-connection the source database. The header, the diagnostics' text, and every other line go to standard error. The exit code is the same.

                --verbose also writes each file read and the time taken to standard error; --quiet writes only the errors (to standard error). The exit code is the same either way.

                Examples:
                  pgproj build
                  pgproj build --project ./db/pgproj.json
                  pgproj build --dump-model ./model.json
                  pgproj build --format json
                  pgproj build --quiet
                  pgproj build --verbose
                  pgproj build --erd
                  pgproj build --source-connection "Host=localhost;Database=app" --dump-model ./model.json
                  pgproj build --erd docs/model
                  pgproj build --erd docs/model --erd-html --erd-existing overwrite
                """,
        };

        HelpLayout.SetSummary(command, "Parse the project's SQL files, build the model, and report its errors and warnings.");
        command.Options.Add(projectOption);
        command.Options.Add(dumpModelOption);
        command.Options.Add(excludeGrantsOption);
        command.Options.Add(excludeRoleMembershipsOption);
        command.Options.Add(excludeOwnersOption);
        command.Options.Add(sourceOption);
        command.Options.Add(formatOption);
        var erdOptions = ErdOptions.AddTo(command);
        var logOptions = LogOptions.AddTo(command);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // With --format json, standard output carries only the JSON document (G-309f, as diff's since G-107), so the rest goes to standard error.
            var json = string.Equals(parseResult.GetValue(formatOption), "json", StringComparison.Ordinal);
            var log = logOptions.Create(parseResult, infoToError: json);
            var startedUtc = DateTime.UtcNow;
            try
            {
                var projectPath = parseResult.GetValue(projectOption) ?? "pgproj.json";

                // A live database in place of the project's files (G-304c), built by the engine through its public API (G-318); only the option turns the mode on.
                var sourceMode = parseResult.GetResult(sourceOption) is not null;
                var erdDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? Environment.CurrentDirectory;
                if (!sourceMode)
                {
                    log.Detail($"Reading project file: {Path.GetFullPath(projectPath)}");
                }

                // The engine reads the files, parses, and builds the model (G-309a); this command writes what it returns. In source mode the engine's lines about the
                // source are written as they come, and the project file is announced once the source is scaffolded, where the command has always said so.
                var events = new EventList(sourceMode ? new LogProgress(log, announceProjectFile: true) : null);
                var result = await new PgProjEngine().BuildAsync(
                    new BuildRequest
                    {
                        ProjectFilePath = projectPath,
                        SourceConnectionString = sourceMode ? ConnectionSettings.ResolveInitSource(parseResult.GetValue(sourceOption)) : null,
                        ExcludeGrants = parseResult.GetValue(excludeGrantsOption),
                        ExcludeRoleMemberships = parseResult.GetValue(excludeRoleMembershipsOption),
                        ExcludeOwners = parseResult.GetValue(excludeOwnersOption),
                        Progress = events,
                    },
                    cancellationToken);

                // --format json writes the result whatever its status, as diff's does (G-316): a run that built nothing is the envelope with its status.
                if (json && result.Status is not (BuildStatus.Succeeded or BuildStatus.Failed))
                {
                    Console.Out.WriteLine(result.ToJson(CliContext.DisplayPath));
                }

                if (result.Status == BuildStatus.ProjectNotFound)
                {
                    return ReportProjectNotFound(log, parseResult.GetValue(projectOption) ?? "pgproj.json");
                }

                if (result.Status is BuildStatus.ProjectInvalid or BuildStatus.Refused or BuildStatus.ConnectionFailed)
                {
                    log.Error($"Build failed: {result.Message}");
                    return 1;
                }

                var model = result.Model!;
                if (result.Source is not { } source)
                {
                    log.Info($"Loaded project: {result.ProjectName}");
                    log.Info($"Source path: {result.SourcePath}");
                    log.Info($"SQL files: {result.Documents.Count}");
                }
                else
                {
                    log.Info($"Source database: {source}");
                    log.Info($"Files scaffolded from it: {result.Documents.Count}");
                }
                foreach (var line in result.TenantLines)
                {
                    log.Info(line);
                }

                if (log.IsVerbose)
                {
                    foreach (var path in events.Documents)
                    {
                        log.Detail($"Read: {CliContext.DisplayPath(path)}");
                    }

                    log.Detail($"Parsed {result.Documents.Count} SQL file(s) and built the model in {result.ElapsedMilliseconds} ms.");
                }

                cliContext.WriteDiagnostics(result.Diagnostics, log);

                if (erdOptions.IsRequested(parseResult))
                {
                    await ErdFiles.WriteAsync(model, result.ProjectName!, ErdFiles.Resolve(erdOptions.Path(parseResult), erdDirectory, startedUtc, companion: true, html: erdOptions.Html(parseResult)), startedUtc, log, erdOptions.Filter(parseResult), erdOptions.Existing(parseResult), cancellationToken);
                }

                var dumpModelPath = parseResult.GetValue(dumpModelOption);
                if (!string.IsNullOrWhiteSpace(dumpModelPath))
                {
                    using var stream = new FileStream(dumpModelPath, FileMode.Create);
                    JsonSerializer.Serialize(stream, model, new JsonSerializerOptions { WriteIndented = true });
                    log.Info($"Model written to: {Path.GetFullPath(dumpModelPath)}");
                }

                if (json)
                {
                    Console.Out.WriteLine(result.ToJson(CliContext.DisplayPath));
                }

                return result.Status == BuildStatus.Failed ? 1 : 0;
            }
            catch (FileNotFoundException)
            {
                return ReportProjectNotFound(log, parseResult.GetValue(projectOption) ?? "pgproj.json");
            }
            catch (Exception exception)
            {
                log.Error($"Build failed: {exception.Message}");
                return 1;
            }
        });

        return command;
    }

    private static int ReportProjectNotFound(CliLog log, string projectPath)
    {
        log.Error("Build failed: project file was not found.");
        log.Error($"Looked for: {Path.GetFullPath(projectPath)}");
        log.Error("Use --project <path> to specify a pgproj.json file, or create one based on pgproj.sample.json.");
        return 1;
    }

    /// <summary>
    /// What the engine reported while it worked: the files read are kept in order and written after the run, as the command always wrote them; an event with a
    /// line of its own (the source's, in source mode) goes to <paramref name="lines"/> as it comes.
    /// </summary>
    private sealed class EventList(IProgress<EngineEvent>? lines) : IProgress<EngineEvent>
    {
        public List<string> Documents { get; } = [];

        public void Report(EngineEvent value)
        {
            if (value.Kind == EngineEventKind.DocumentRead && value.Path is not null)
            {
                Documents.Add(value.Path);
            }

            lines?.Report(value);
        }
    }

    private static Option<string?> CreateDumpModelOption()
        => new("--dump-model")
        {
            HelpName = "file",
            Description = "Optional. Write the built semantic model as indented JSON to the given file (for debugging).",
        };

    private static Option<string> CreateProjectOption()
        => new("--project")
        {
            HelpName = "path",
            Description = "Path to the pgproj.json project file. Defaults to 'pgproj.json' in the current directory.",
            DefaultValueFactory = _ => "pgproj.json",
        };

    private static Option<bool?> CreateExcludeGrantsOption()
        => new("--exclude-grants")
        {
            Description = "Exclude object/schema/column privilege grants from the model. " +
                "Overrides the excludeGrants value from pgproj.json (pass explicitly in either direction). " +
                "Included by default.",
        };

    private static Option<bool?> CreateExcludeRoleMembershipsOption()
        => new("--exclude-role-memberships")
        {
            Description = "Exclude role memberships (GRANT role TO grantee) from the model. " +
                "Overrides the excludeRoleMemberships value from pgproj.json (pass explicitly in either direction). " +
                "Included by default.",
        };

    private static Option<bool?> CreateExcludeOwnersOption()
        => new("--exclude-owners")
        {
            Description = "Exclude object owners (ALTER ... OWNER TO) from the model. " +
                "Overrides the excludeOwners value from pgproj.json (pass explicitly in either direction). " +
                "Included by default.",
        };
}
