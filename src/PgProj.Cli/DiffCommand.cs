using System.CommandLine;
using PgProj.Core;
using PgProj.Engine;

namespace PgProj.Cli;

public static class DiffCommand
{
    public static Command Create(CliContext cliContext)
    {
        ArgumentNullException.ThrowIfNull(cliContext);

        var projectOption = CreateProjectOption();
        var connectionOption = new Option<string?>("--target-connection")
        {
            HelpName = "connection-string",
            Description = "PostgreSQL connection string of the database to compare the project with. " +
                $"If not given, the {ConnectionSettings.TargetVariable} environment variable is used, then targetConnectionString from pgproj.json.",
        };
        var excludeGrantsOption = CreateExcludeGrantsOption();
        var excludeRoleMembershipsOption = CreateExcludeRoleMembershipsOption();
        var excludeOwnersOption = CreateExcludeOwnersOption();
        var formatOption = new Option<string>("--format")
        {
            HelpName = "text|json",
            Description = "Output format: 'text' (default) lists each added, removed, and modified object with the properties " +
                "that changed, then the counts; 'json' writes the comparison (the project, the documents read, the diagnostics, the change set, and the counts) as one JSON " +
                "document on standard output (everything else goes to standard error), for CI.",
            DefaultValueFactory = _ => "text",
        };
        formatOption.AcceptOnlyFromAmong("text", "json");
        var exitCodeOption = new Option<bool>("--exit-code")
        {
            Description = "Exit with 2 when there are differences (an added, removed, or modified object) and 0 when there are none, for a CI " +
                "drift check. An error is still 1. Warnings are not differences. Without it, diff exits 0 whether or not it finds differences.",
        };
        var tenantsOption = CreateTenantsOption(
            "Compare only these tenants, by identifier (acme, not tenant_acme), as a comma-separated list or the option repeated. Only for a project with "
            + "tenants; each must be a tenant the project already has. The shared schemas are compared either way.");
        var command = new Command("diff")
        {
            Description =
                """
                Parse the project's SQL files, build the semantic model, connect to a live PostgreSQL database, and report the differences between them: each added, removed, and modified object (a modified one with the properties that changed, old -> new, named as they are written: Default, Query, Columns changed), then the counts.

                The database to compare with comes from --target-connection, the PGPROJ_TARGET_CONNECTION environment variable, or the targetConnectionString property in pgproj.json, in that order; one of them is required.

                With --source-connection, a live database is the desired state in place of the project's SQL files (environment promotion, dev -> QA -> prod, and parity checks): the target is compared with the source, so + is in the source only, - in the target only, and ~ in both with a difference. The first output line says "Source A -> Target B". The source is read in a read-only transaction and must not be the target; the project's schemas, searchPath, exclude options, and pre/post-deploy scripts still apply, and pgproj.json may be absent. Not for a project with tenants. Use --exit-code and --format json for a parity check between two databases.

                --exclude-grants/--exclude-role-memberships/--exclude-owners override pgproj.json's excludeGrants/excludeRoleMemberships/excludeOwners for this run, in either direction.

                --format json writes the comparison as one JSON document on standard output, for CI: schemaVersion, status, the project, the documents read, the diagnostics with their places, the added, removed, and modified objects, and the counts. When nothing could be compared (the project has errors, the run is refused, a database cannot be reached), the document has that status, the message, and what was read, and the exit code is 1.

                A project with tenants ("tenants" in pgproj.json, a schema per tenant built from one template) is compared in parts: the shared schemas once, then each tenant's schema against the template. The tenants are grouped by the changes they need, shown once in the template's stand-in schema (the prefix); a tenant whose schema does not exist yet is counted, and tenants with nothing to change are named. A schema named as a tenant's that the tenant query or file no longer lists is left as it is, and a warning says to drop it by other means. --tenants a,b compares only those tenants. The closing counts add up every tenant. See docs/notes/tenants.md.

                Exit codes: 0 when diff ran (whatever it found), 1 on an error (no connection, a project with errors). With --exit-code: 0 when there are no differences, 2 when there are, 1 on an error; warnings do not count as differences.

                Diagram:
                  - --erd also writes an entity relationship diagram of the project's tables (Mermaid, from the SQL files, not the database): erd-<time>.mmd and a companion erd-<time>.md in the directory of pgproj.json, or the path given after --erd. It is written before the database is read.
                  - --erd-schemas/--erd-exclude-schemas (not both) narrow the diagram to some schemas; a foreign key out of them is drawn to an (external) stub.
                  - --erd-html also writes a web page (erd-<time>.html) that draws the diagram in a browser; it loads Mermaid from a CDN.
                  - --erd-existing overwrite|backup|keep says what to do with a file already at the path; by default a file --erd wrote is backed up to name.<UTC time>.ext and any other file is kept.

                --verbose also writes each step and its time to standard error; --quiet leaves out the progress lines and notices, so only the change list (or JSON) and any errors are written.

                When the change drops, renames, or retypes something the body of a PL/pgSQL routine names (a table or view, a function or procedure, a qualified column), a warning says which routine names what: PostgreSQL records no dependency for such a body, so nothing stops the change and the routine fails when it runs. The script and its order are unchanged.

                Examples:
                  pgproj diff --target-connection "Host=localhost;Database=app;Username=postgres"
                  pgproj diff --project ./db/pgproj.json --target-connection "Host=localhost;Database=app"
                  pgproj diff --target-connection "Host=localhost;Database=app" --exclude-grants
                  pgproj diff --target-connection "Host=localhost;Database=app" --format json
                  pgproj diff --target-connection "Host=localhost;Database=app" --exit-code
                  pgproj diff --target-connection "Host=localhost;Database=app" --quiet
                  pgproj diff --target-connection "Host=localhost;Database=app" --verbose
                  pgproj diff --target-connection "Host=localhost;Database=app" --erd
                  pgproj diff --target-connection "Host=localhost;Database=app" --tenants acme,globex
                  pgproj diff --source-connection "Host=localhost;Database=qa" --target-connection "Host=localhost;Database=prod" --exit-code --exclude-owners
                """,
        };

        HelpLayout.SetSummary(command, "Compare the project with a live PostgreSQL database and list what differs.");
        command.Options.Add(projectOption);
        command.Options.Add(connectionOption);
        var sourceOption = SourceProjectOptions.Create();
        command.Options.Add(sourceOption);
        command.Options.Add(excludeGrantsOption);
        command.Options.Add(excludeRoleMembershipsOption);
        command.Options.Add(excludeOwnersOption);
        command.Options.Add(formatOption);
        command.Options.Add(exitCodeOption);
        command.Options.Add(tenantsOption);
        var erdOptions = ErdOptions.AddTo(command);
        var logOptions = LogOptions.AddTo(command);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // With --format json, standard output carries only the JSON document (G-107), so progress goes to standard error.
            var json = string.Equals(parseResult.GetValue(formatOption), "json", StringComparison.Ordinal);
            var log = logOptions.Create(parseResult, infoToError: json);
            try
            {
                var projectPath = parseResult.GetValue(projectOption) ?? "pgproj.json";
                var sourceConnection = parseResult.GetResult(sourceOption) is not null ? ConnectionSettings.ResolveInitSource(parseResult.GetValue(sourceOption)) : null;
                var erdDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? Environment.CurrentDirectory;
                var startedUtc = DateTime.UtcNow;
                if (sourceConnection is null)
                {
                    log.Detail($"Reading project file: {Path.GetFullPath(projectPath)}");
                }

                // The engine reads the project and the databases and compares (G-309b); this command writes what it returns. Its progress is written as it comes,
                // and what follows the build (the diagnostics, the diagram) before any database is read from, as the command always did.
                var result = await new PgProjEngine().CompareAsync(
                    new CompareRequest
                    {
                        ProjectFilePath = projectPath,
                        ExcludeGrants = parseResult.GetValue(excludeGrantsOption),
                        ExcludeRoleMemberships = parseResult.GetValue(excludeRoleMembershipsOption),
                        ExcludeOwners = parseResult.GetValue(excludeOwnersOption),
                        TargetConnectionString = ConnectionSettings.ShellTarget(parseResult.GetValue(connectionOption)),
                        SourceConnectionString = sourceConnection,
                        Tenants = SplitTenantIdentifiers(parseResult.GetValue(tenantsOption)),
                        Progress = new LogProgress(log, announceProjectFile: sourceConnection is not null),
                        AfterBuild = async (build, token) =>
                        {
                            if (sourceConnection is null)
                            {
                                log.Info($"Loaded project: {build.ProjectName}");
                                log.Info($"Source path: {build.SourcePath}");
                                log.Info($"SQL files: {build.Documents.Count}");
                            }

                            foreach (var line in build.TenantLines)
                            {
                                log.Info(line);
                            }

                            log.Detail($"Parsed {build.Documents.Count} SQL file(s) and built the model in {build.ElapsedMilliseconds} ms.");
                            cliContext.WriteDiagnostics(build.Diagnostics, log);

                            // From the project's own files, and before any connection, so it is written whatever the database does.
                            if (build.Status != BuildStatus.Failed && erdOptions.IsRequested(parseResult))
                            {
                                await ErdFiles.WriteAsync(build.Model!, build.ProjectName!, ErdFiles.Resolve(erdOptions.Path(parseResult), erdDirectory, startedUtc, companion: true, html: erdOptions.Html(parseResult)), startedUtc, log, erdOptions.Filter(parseResult), erdOptions.Existing(parseResult), token);
                            }
                        },
                    },
                    cancellationToken);

                // --format json writes the result whatever its status (G-316): a run that did not compare is the envelope with its status and what was read.
                if (json && result.Status != CompareStatus.Succeeded)
                {
                    Console.Out.WriteLine(result.ToJson(CliContext.DisplayPath));
                }

                switch (result.Status)
                {
                    case CompareStatus.ProjectNotFound:
                        log.Error("Diff failed: project file was not found.");
                        log.Error($"Looked for: {Path.GetFullPath(projectPath)}");
                        log.Error("Use --project <path> to specify a pgproj.json file, or create one based on pgproj.sample.json.");
                        return 1;
                    case CompareStatus.Failed:
                        return 1;
                    case not CompareStatus.Succeeded:
                        log.Error($"Diff failed: {result.Message}");
                        return 1;
                }

                if (result.Tenants is { } tenants)
                {
                    if (json)
                    {
                        Console.Out.WriteLine(result.ToJson(CliContext.DisplayPath));
                    }
                    else
                    {
                        TenantDiffOutput.WriteText(result, Console.Out);
                    }

                    if (tenants.List.Unavailable is { } unavailable)
                    {
                        log.Warning(unavailable + " They are read once the shared project is published.");
                    }

                    if (tenants.List.FormerSchemas.Count > 0)
                    {
                        log.Warning(TenantDiffOutput.DescribeFormerTenants(tenants.List.FormerSchemas, forPublish: false));
                    }

                    if (result.UndroppedSchemas.Count > 0)
                    {
                        log.Warning(UndroppedSchemas.Describe(result.UndroppedSchemas));
                    }

                    return parseResult.GetValue(exitCodeOption) && result.DifferenceCount > 0 ? DifferencesExitCode : 0;
                }

                if (json)
                {
                    Console.Out.WriteLine(result.ToJson(CliContext.DisplayPath));
                }
                else
                {
                    DiffOutputWriter.WriteText(result.Changes, Console.Out);
                }

                // pgproj never drops a schema (G-166, the user's decision); it says which ones a publish would leave behind.
                if (result.UndroppedSchemas.Count > 0)
                {
                    log.Warning(UndroppedSchemas.Describe(result.UndroppedSchemas));
                }

                // A drop, rename, or retype of something a PL/pgSQL body names: PostgreSQL will not stop it and the routine fails when it runs (G-303b).
                if (result.BodyNotes.Count > 0)
                {
                    log.Warning(PlpgsqlBodyNotes.Describe(result.BodyNotes, "This change drops, renames, or retypes"));
                }

                // A drift check for CI (G-282): 2 when there is something to publish, as git diff --exit-code and terraform plan -detailed-exitcode do.
                return parseResult.GetValue(exitCodeOption) && result.DifferenceCount > 0 ? DifferencesExitCode : 0;
            }
            catch (FileNotFoundException)
            {
                log.Error("Diff failed: project file was not found.");
                log.Error($"Looked for: {Path.GetFullPath(parseResult.GetValue(projectOption) ?? "pgproj.json")}");
                log.Error("Use --project <path> to specify a pgproj.json file, or create one based on pgproj.sample.json.");
                return 1;
            }
            catch (Exception exception)
            {
                log.Error($"Diff failed: {exception.Message}");
                return 1;
            }
        });

        return command;
    }

    /// <summary>The <c>--tenants</c> option of <c>diff</c> and <c>publish</c> (G-290): tenant identifiers, a comma-separated list or the option repeated.</summary>
    internal static Option<string[]> CreateTenantsOption(string description)
        => new("--tenants")
        {
            HelpName = "identifiers",
            Description = description,
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true,
        };

    /// <summary>The identifiers <c>--tenants</c> names, exactly as written (G-290: as in the tenant file), each once.</summary>
    internal static IReadOnlyList<string> SplitTenantIdentifiers(string[]? values)
        => [.. (values ?? []).SelectMany(static value => value.Split(',')).Select(static name => name.Trim()).Where(static name => name.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>What <c>diff --exit-code</c> exits with when there are differences (G-282); an error is 1, as always.</summary>
    public const int DifferencesExitCode = 2;

    /// <summary>The added, removed, and modified objects of <paramref name="changeSet"/>: what <c>--exit-code</c> counts as differences.</summary>
    public static int DifferenceCount(PgProj.Diff.ChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        return changeSet.Added.Count + changeSet.Removed.Count + changeSet.Modified.Count;
    }

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
