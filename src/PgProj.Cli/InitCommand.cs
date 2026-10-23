using System.CommandLine;
using PgProj.Engine;
using PgProj.Parser;

namespace PgProj.Cli;

/// <summary>
/// Scaffolds a new pgproj project from an existing live database (G-82): writes one SQL file per object,
/// following the same layout `samples/MultiSchema` (this repository's own hand-authored sample project) already uses, plus a
/// generated <c>pgproj.json</c>.
/// </summary>
public static class InitCommand
{
    /// <summary>
    /// What <c>init</c> can't read (G-152): the engine's list (G-309d), which the help and the end of a run show.
    /// </summary>
    internal static readonly string[] UnreadObjectKinds = InitProjectFile.UnreadObjectKinds;

    /// <summary>Shown in <c>init</c>'s help and after it runs: a heading, one bullet per kind, and a closing line (G-157).</summary>
    internal static readonly string UnreadObjectKindsNotice =
        "Not read by pgproj, so absent from the scaffolded project even if the database has them:"
        + Environment.NewLine
        + string.Join(Environment.NewLine, UnreadObjectKinds.Select(static kind => "  - " + kind))
        + Environment.NewLine
        + "Check for them before relying on the scaffold as complete.";

    public static Command Create(CliContext cliContext)
    {
        ArgumentNullException.ThrowIfNull(cliContext);

        var projectOption = new Option<string>("--project")
        {
            HelpName = "path",
            Description = "Path to write the new pgproj.json file to. Defaults to 'pgproj.json' in the current directory.",
            DefaultValueFactory = _ => "pgproj.json",
        };
        var connectionOption = new Option<string?>("--source-connection")
        {
            HelpName = "connection-string",
            Description = "PostgreSQL connection string of the database to read the schema from (the source). " +
                $"If not given, the {ConnectionSettings.SourceVariable} environment variable is used; one of them is required.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var outputOption = new Option<string>("--output", "-o")
        {
            HelpName = "directory",
            Description = "Base directory to write the scaffolded SQL files to, relative to --project's directory unless absolute. Created (including any missing parent directories) if it doesn't already exist. Defaults to './sql'.",
            DefaultValueFactory = _ => "./sql",
        };
        var nameOption = new Option<string?>("--name")
        {
            Description = "Name for the generated pgproj.json's \"name\" field. Defaults to the database name from --source-connection.",
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite an existing pgproj.json at --project or write into a non-empty --output directory. Without this, init refuses to touch either.",
        };
        var excludeGrantsOption = new Option<bool>("--exclude-grants")
        {
            Description = "Don't scaffold object/schema/column privilege grants. When given, the generated " +
                "pgproj.json also sets excludeGrants: true, so later build/diff/publish runs keep matching what " +
                "was scaffolded.",
        };
        var excludeRoleMembershipsOption = new Option<bool>("--exclude-role-memberships")
        {
            Description = "Don't scaffold role memberships (GRANT role TO grantee). When given, the generated " +
                "pgproj.json also sets excludeRoleMemberships: true, so later build/diff/publish runs keep " +
                "matching what was scaffolded.",
        };
        var excludeOwnersOption = new Option<bool>("--exclude-owners")
        {
            Description = "Don't scaffold object owners (ALTER ... OWNER TO). When given, the generated " +
                "pgproj.json also sets excludeOwners: true, so later build/diff/publish runs keep matching what " +
                "was scaffolded.",
        };

        var schemasOption = new Option<string[]>("--schemas")
        {
            HelpName = "schemas",
            Description = "Scaffold only these schemas, as a comma-separated list or the option repeated. A schema not named is left out, and 'public' is only included " +
                "if it is named. Cannot be combined with --exclude-schemas. A name the database lacks is an error. The generated pgproj.json lists the schemas it modeled. A name is folded to lower case unless it is in double quotes, as pg_dump -n does.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true,
        };
        var excludeSchemasOption = new Option<string[]>("--exclude-schemas")
        {
            HelpName = "schemas",
            Description = "Scaffold every schema except these, as a comma-separated list or the option repeated. Cannot be combined with --schemas. " +
                "The generated pgproj.json lists the schemas it modeled. A name is folded to lower case unless it is in double quotes, as pg_dump -n does.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true,
        };
        var reportOption = new Option<bool>("--report")
        {
            Description = "Also write init-report-<yyyy-MM-dd-HH-mm-ss>.md (the time in UTC) at the root of --output: the arguments used, a summary, every object modeled with its file, " +
                "what was not modeled (not supported, or excluded), and what is cluster-wide.",
        };

        var tenantPrefixOption = new Option<string?>("--tenant-prefix")
        {
            HelpName = "prefix",
            Description = "Scaffold a database with a schema per tenant (G-290): the schemas named <prefix>_... are tenants. One of them becomes the template (the Tenant folder, written "
                + "without its schema name), the others are compared with it and those that differ are named, the shared schemas are scaffolded as usual, and pgproj.json gets a "
                + "\"tenants\" block. Folded to lower case unless in double quotes.",
        };
        var tenantFromOption = new Option<string?>("--tenant-from")
        {
            HelpName = "identifier",
            Description = "With --tenant-prefix: the tenant (its identifier, acme for tenant_acme) to scaffold the template from. Defaults to the first tenant by name.",
        };
        var tenantFileOption = new Option<bool>("--tenant-file")
        {
            Description = "With --tenant-prefix: also write tenants.csv beside pgproj.json, listing the tenants found, and point pgproj.json's \"tenants\" at it, so the "
                + "tenants are that list from now on (pgproj creates a listed tenant's schema). Not with --tenant-query.",
        };
        var tenantQueryOption = new Option<string?>("--tenant-query")
        {
            HelpName = "sql",
            Description = "With --tenant-prefix: write this query into pgproj.json's \"tenants\", so the tenants are the identifiers it returns from now on (pgproj creates a "
                + "returned tenant's schema). Not run by init. Not with --tenant-file.",
        };
        var command = new Command("init")
        {
            Description =
                $"""
                Connects to a live PostgreSQL database, reads its schema, and writes a new pgproj project from it: a generated pgproj.json and one SQL file per object.

                Where each object is written:
                  - One file per table, view, function, procedure, sequence, type, aggregate, operator, collation, text search dictionary and configuration, cast, event trigger, and publication.
                  - A table's constraints, indexes, triggers, row-level security policies, rules, extended statistics, and grants live alongside it, in the table's file. A materialized view's indexes live in the view's file.
                  - Schema-level and default-privilege grants, and a schema's comment, live in the schema's own file.
                  - An object's owner and comment live beside the object; a constraint's, trigger's, or policy's comment follows it.

                Refuses to overwrite an existing pgproj.json or write into a non-empty output directory unless --force is given.

                When a function body or a type names another function or type without a schema, because the database found it through the search_path of the session that created it, the generated pgproj.json sets "searchPath" to the fewest schemas that find them all, followed by public, so the project builds. init says so when it does.

                The generated pgproj.json's sourceConnectionString is --source-connection as given, with the password replaced by a placeholder; it records where the project came from, and document --source-connection with no value reads it. It is never the database diff and publish change: name that with --target-connection, the PGPROJ_TARGET_CONNECTION environment variable, or targetConnectionString, which init does not write.

                {UnreadObjectKindsNotice}

                Choosing what is scaffolded:
                  - --schemas scaffolds only the schemas listed; --exclude-schemas scaffolds all but the schemas listed (not both). The generated pgproj.json lists the schemas that were modeled.
                  - --exclude-grants/--exclude-role-memberships/--exclude-owners skip scaffolding that category, and are also written into the generated pgproj.json so later build/diff/publish runs keep matching.

                A database with a schema per tenant (--tenant-prefix tenant, where the tenants are tenant_acme, tenant_globex, ...):
                  - The shared schemas are scaffolded as usual; the tenants' are not.
                  - One tenant (the first by name, or --tenant-from acme) is scaffolded into the Tenant folder of --output, with its schema name taken out, so it reads as the template every tenant is built from. Where it names the schema itself (GRANT ... ON SCHEMA), it names the prefix, the template's stand-in schema.
                  - The other tenants are compared with the template, and each that differs is named; pgproj diff shows how.
                  - pgproj.json gets a "tenants" block: the template folder and the prefix, and, with --tenant-file, tenants.csv listing the tenants found, or, with --tenant-query, the query that lists them. See docs/notes/tenants.md.

                Other files:
                  - --report writes a Markdown report of the run at the root of --output.
                  - --erd writes a Mermaid entity relationship diagram of the scaffolded tables (erd-<time>.mmd in the directory of pgproj.json, or the path given), built from the new SQL files; the report links it.
                  - --erd-schemas/--erd-exclude-schemas narrow the diagram to some schemas.
                  - --erd-html also writes a web page that draws the diagram in a browser (it loads Mermaid from a CDN).
                  - --erd-existing overwrite|backup|keep says what to do with a file already at the path (the default backs up a file --erd wrote and keeps any other).

                --verbose also writes each step, its time, and each file written to standard error. --quiet writes nothing but errors (including the notice above, so check it before relying on the scaffold).

                Examples:
                  pgproj init --source-connection "Host=localhost;Database=app;Username=postgres"
                  pgproj init --source-connection "Host=localhost;Database=app" -o ./schema --project ./db/pgproj.json
                  pgproj init --source-connection "Host=localhost;Database=app" --force
                  pgproj init --source-connection "Host=localhost;Database=app" --exclude-grants --exclude-role-memberships
                  pgproj init --source-connection "Host=localhost;Database=app" --schemas app,reporting --report
                  pgproj init --source-connection "Host=localhost;Database=app" --exclude-schemas audit,staging
                  pgproj init --source-connection "Host=localhost;Database=app" --report --erd
                  pgproj init --source-connection "Host=localhost;Database=app" --verbose
                  pgproj init --source-connection "Host=localhost;Database=app" --tenant-prefix tenant
                  pgproj init --source-connection "Host=localhost;Database=app" --tenant-prefix tenant --tenant-from acme --tenant-file
                """,
        };

        HelpLayout.SetSummary(command, "Scaffold a new project (pgproj.json and SQL files) from a live PostgreSQL database.");
        command.Options.Add(projectOption);
        command.Options.Add(connectionOption);
        command.Options.Add(outputOption);
        command.Options.Add(nameOption);
        command.Options.Add(forceOption);
        command.Options.Add(excludeGrantsOption);
        command.Options.Add(excludeRoleMembershipsOption);
        command.Options.Add(excludeOwnersOption);
        command.Options.Add(schemasOption);
        command.Options.Add(excludeSchemasOption);
        command.Options.Add(reportOption);
        command.Options.Add(tenantPrefixOption);
        command.Options.Add(tenantFromOption);
        command.Options.Add(tenantFileOption);
        command.Options.Add(tenantQueryOption);
        var erdOptions = ErdOptions.AddTo(command);
        command.Validators.Add(result =>
        {
            var schemasGiven = result.GetResult(schemasOption) is not null;
            var excludeGiven = result.GetResult(excludeSchemasOption) is not null;
            if (schemasGiven && excludeGiven)
            {
                result.AddError("--schemas and --exclude-schemas cannot be used together.");
            }
            else if ((schemasGiven && SplitSchemaNames(result.GetValue(schemasOption)).Count == 0) || (excludeGiven && SplitSchemaNames(result.GetValue(excludeSchemasOption)).Count == 0))
            {
                result.AddError("A list of schema names is needed after --schemas and --exclude-schemas.");
            }

            var prefixGiven = !string.IsNullOrWhiteSpace(result.GetValue(tenantPrefixOption));
            if (!prefixGiven && (result.GetResult(tenantFromOption) is not null || result.GetValue(tenantFileOption) || result.GetResult(tenantQueryOption) is not null))
            {
                result.AddError("--tenant-from, --tenant-file, and --tenant-query need --tenant-prefix.");
            }

            if (result.GetValue(tenantFileOption) && result.GetResult(tenantQueryOption) is not null)
            {
                result.AddError("--tenant-file and --tenant-query cannot be used together: the tenants come from a file or from a query.");
            }
        });
        var logOptions = LogOptions.AddTo(command);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var log = logOptions.Create(parseResult);
            try
            {
                var projectPath = parseResult.GetValue(projectOption) ?? "pgproj.json";
                var connectionString = ConnectionSettings.ResolveInitSource(parseResult.GetValue(connectionOption));
                var outputPath = parseResult.GetValue(outputOption) ?? "./sql";
                var name = parseResult.GetValue(nameOption);
                var force = parseResult.GetValue(forceOption);
                var excludeGrants = parseResult.GetValue(excludeGrantsOption);
                var excludeRoleMemberships = parseResult.GetValue(excludeRoleMembershipsOption);
                var excludeOwners = parseResult.GetValue(excludeOwnersOption);
                var includeSchemas = parseResult.GetResult(schemasOption) is null ? null : SplitSchemaNames(parseResult.GetValue(schemasOption));
                var excludeSchemas = parseResult.GetResult(excludeSchemasOption) is null ? null : SplitSchemaNames(parseResult.GetValue(excludeSchemasOption));
                var writeReport = parseResult.GetValue(reportOption);
                var tenantPrefix = parseResult.GetValue(tenantPrefixOption);
                var tenantFile = parseResult.GetValue(tenantFileOption);
                var tenantQuery = parseResult.GetValue(tenantQueryOption);
                var writeErd = erdOptions.IsRequested(parseResult);
                var startedUtc = DateTime.UtcNow;

                var fullProjectPath = Path.GetFullPath(projectPath);
                var projectDirectory = Path.GetDirectoryName(fullProjectPath) ?? Environment.CurrentDirectory;
                var fullOutputPath = Path.GetFullPath(outputPath, projectDirectory);

                if (!force)
                {
                    if (File.Exists(fullProjectPath))
                    {
                        log.Error($"Init failed: '{fullProjectPath}' already exists. Use --force to overwrite it, or --project to write somewhere else.");
                        return 1;
                    }

                    if (Directory.Exists(fullOutputPath) && Directory.EnumerateFileSystemEntries(fullOutputPath).Any())
                    {
                        log.Error($"Init failed: output directory '{fullOutputPath}' already exists and is not empty. Use --force to write into it anyway, or --output to write somewhere else.");
                        return 1;
                    }
                }

                // The engine reads the database and makes the files (G-309d); the command writes them, and says so, where it always has.
                var result = await new PgProjEngine().ScaffoldAsync(
                    new ScaffoldRequest
                    {
                        SourceConnectionString = connectionString,
                        ProjectFilePath = fullProjectPath,
                        OutputDirectory = fullOutputPath,
                        Name = name,
                        IncludeSchemas = includeSchemas,
                        ExcludeSchemas = excludeSchemas,
                        ExcludeGrants = excludeGrants,
                        ExcludeRoleMemberships = excludeRoleMemberships,
                        ExcludeOwners = excludeOwners,
                        TenantPrefix = string.IsNullOrWhiteSpace(tenantPrefix) ? null : tenantPrefix,
                        TenantFrom = parseResult.GetValue(tenantFromOption),
                        TenantFile = tenantFile,
                        TenantQuery = tenantQuery,
                        Report = writeReport,
                        ReportArguments = writeReport ? ReportArguments() : [],
                        Diagram = writeErd ? erdOptions.Filter(parseResult) : null,
                        StartedUtc = startedUtc,
                        Progress = new LogProgress(log),
                        AfterScaffold = async (scaffolded, token) =>
                        {
                            // Created explicitly (not just implicitly via the per-file writes below) so the output directory
                            // exists even for a database with nothing to scaffold, since the generated pgproj.json's "source"
                            // must resolve to an existing directory.
                            Directory.CreateDirectory(fullOutputPath);

                            foreach (var file in scaffolded.Files)
                            {
                                var fullFilePath = Path.Combine(fullOutputPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                                Directory.CreateDirectory(Path.GetDirectoryName(fullFilePath) ?? fullOutputPath);
                                await File.WriteAllTextAsync(fullFilePath, file.Content, token);
                                log.Detail($"Wrote: {fullFilePath}");
                            }

                            Directory.CreateDirectory(projectDirectory);
                            await File.WriteAllTextAsync(fullProjectPath, scaffolded.ProjectFileText, token);

                            log.Info($"Wrote {scaffolded.Files.Count} SQL file(s) to: {fullOutputPath}");
                            log.Info($"Wrote project file: {fullProjectPath}");
                            if (scaffolded.TenantFileText is { } csv)
                            {
                                var csvPath = Path.Combine(projectDirectory, InitResult.TenantFileName);
                                await File.WriteAllTextAsync(csvPath, csv, token);
                                log.Info($"Wrote the tenant file: {csvPath} ({scaffolded.Tenants!.TenantSchemas.Count} tenant(s))");
                            }

                            foreach (var message in scaffolded.Messages)
                            {
                                if (message.Level == EngineEventLevel.Warning)
                                {
                                    log.Warning(message.Text);
                                }
                                else
                                {
                                    log.Info(message.Text);
                                }
                            }

                            // The diagram is built from the new SQL files (their parse trees), as the engine read the project back, and is linked from the report.
                            if (!writeErd)
                            {
                                return null;
                            }

                            var erd = await ErdFiles.WriteAsync(scaffolded.Project!.Model!, scaffolded.Project.ProjectName!, ErdFiles.Resolve(erdOptions.Path(parseResult), projectDirectory, startedUtc, companion: false, html: erdOptions.Html(parseResult)), startedUtc, log, erdOptions.Filter(parseResult), erdOptions.Existing(parseResult), token);
                            return erd is null ? null : new InitDiagramFile(erd.DiagramPath, erd.Placement);
                        },
                    },
                    cancellationToken);

                if (result.Status != InitStatus.Succeeded)
                {
                    log.Error($"Init failed: {result.Message}");
                    return 1;
                }

                if (result.ReportText is { } reportText)
                {
                    var reportPath = Path.Combine(fullOutputPath, result.ReportFileName!);
                    await File.WriteAllTextAsync(reportPath, reportText, cancellationToken);
                    log.Info($"Wrote report: {reportPath}");
                }

                log.Info(UnreadObjectKindsNotice);
                log.Info("Run 'pgproj build' to validate the scaffolded project.");
                return 0;

                // The arguments as they were used, for the report; the engine masks the password (G-318).
                List<InitArgument> ReportArguments()
                {
                    var arguments = new List<InitArgument>
                    {
                        new("--source-connection", connectionString),
                        new("--project", fullProjectPath),
                        new("--output", fullOutputPath),
                    };
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        arguments.Add(new("--name", name));
                    }

                    if (includeSchemas is not null)
                    {
                        arguments.Add(new("--schemas", string.Join(", ", includeSchemas)));
                    }

                    if (excludeSchemas is not null)
                    {
                        arguments.Add(new("--exclude-schemas", string.Join(", ", excludeSchemas)));
                    }

                    if (writeErd)
                    {
                        arguments.Add(new("--erd", erdOptions.Path(parseResult) is { Length: > 0 } erdValue ? erdValue : "true"));
                    }

                    if (erdOptions.Filter(parseResult) is { IsEmpty: false } erdFilter)
                    {
                        arguments.Add(new(erdFilter.OptionName, string.Join(", ", erdFilter.Schemas)));
                    }

                    if (erdOptions.Html(parseResult))
                    {
                        arguments.Add(new("--erd-html", "true"));
                    }

                    if (erdOptions.Existing(parseResult) != ErdExisting.Default)
                    {
                        arguments.Add(new("--erd-existing", erdOptions.Existing(parseResult).ToString().ToLowerInvariant()));
                    }

                    // The prefix as the engine uses it, folded as a name typed outside SQL is (G-206).
                    if (!string.IsNullOrWhiteSpace(tenantPrefix))
                    {
                        arguments.Add(new("--tenant-prefix", SqlIdentifiers.FoldName(tenantPrefix.Trim())));
                        if (parseResult.GetValue(tenantFromOption) is { } tenantFrom)
                        {
                            arguments.Add(new("--tenant-from", tenantFrom));
                        }

                        if (tenantQuery is not null)
                        {
                            arguments.Add(new("--tenant-query", tenantQuery));
                        }
                    }

                    foreach (var (flag, given) in new[] { ("--force", force), ("--exclude-grants", excludeGrants), ("--exclude-role-memberships", excludeRoleMemberships), ("--exclude-owners", excludeOwners), ("--tenant-file", tenantFile), ("--report", true) })
                    {
                        if (given)
                        {
                            arguments.Add(new(flag, "true"));
                        }
                    }

                    return arguments;
                }
            }
            catch (Exception exception)
            {
                log.Error($"Init failed: {exception.Message}");
                return 1;
            }
        });

        return command;
    }

    /// <summary>The schema names in an option's values: each value may itself be a comma-separated list; blanks are dropped and a repeat is kept once.</summary>
    internal static IReadOnlyList<string> SplitSchemaNames(string[]? values)
        => [.. (values ?? []).SelectMany(static value => value.Split(',')).Select(static name => name.Trim()).Where(static name => name.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Replaces the value of a <c>Password</c>/<c>Pwd</c> key with <c>&lt;password&gt;</c>, leaving every other
    /// part of the connection string exactly as given (spacing, key casing, key order). The engine does it (G-309d).
    /// </summary>
    public static string RedactPassword(string connectionString) => InitProjectFile.RedactPassword(connectionString);
}
