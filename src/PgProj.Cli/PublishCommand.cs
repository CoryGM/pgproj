using System.CommandLine;
using PgProj.Engine;
using PgProj.Deploy;

namespace PgProj.Cli;

public static class PublishCommand
{
    public static Command Create(CliContext cliContext)
    {
        ArgumentNullException.ThrowIfNull(cliContext);

        var projectOption = CreateProjectOption();
        var connectionOption = new Option<string?>("--target-connection")
        {
            HelpName = "connection-string",
            Description = "PostgreSQL connection string of the database to update. " +
                $"If not given, the {ConnectionSettings.TargetVariable} environment variable is used, then targetConnectionString from pgproj.json.",
        };
        var executeOption = new Option<bool>("--execute")
        {
            Description = "Execute the generated deployment script against PostgreSQL after it is generated.",
        };
        var outputOption = new Option<string?>("--output")
        {
            HelpName = "file",
            Description = "File path to write the generated SQL deployment script to.",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Print the generated deployment script without executing it, even if --execute is set.",
        };
        var allowDestructiveOption = new Option<bool>("--allow-destructive")
        {
            Description = "Allow --execute to run a script that can discard data or access: a dropped table, column, " +
                "materialized view, extension, or composite type field; a changed column type that can lose a value (a widening such as varchar(50) " +
                "to varchar(100) or int to bigint is not one); a cast, operator, aggregate, " +
                "rule, publication, or event trigger that is dropped and not created again; a revoked grant or role membership; " +
                "or a changed owner. Without this flag, --execute refuses to run such a script and lists the affected operations.",
        };
        var commentsOnlyOption = new Option<bool>("--comments-only")
        {
            Description = "Deploy only the COMMENT ON statements of the script (on schemas, tables, columns, constraints, and the other objects that take a comment), " +
                "and nothing else: no schema changes, no grants or owners, and not the predeploy or postdeploy script. " +
                "A comment on an object the database does not have yet (a new table, say) is skipped, with a warning that names it.",
        };
        var allowSuperuserStatementsOption = new Option<bool>(SuperuserStatements.OptionName)
        {
            Description = "Allow --execute to run statements that PostgreSQL lets only a superuser run: a created, changed, or dropped event trigger, a table's internal (foreign-key) triggers enabled or disabled (ALTER TABLE ... TRIGGER ALL), and a function or procedure " +
                "created or replaced LEAKPROOF or in an untrusted language (c, internal, plpython3u, ...). It is consent for this run, not a role switch: without it, --execute " +
                "refuses such a script even for a superuser and lists the statements; with it, a role that is not a superuser is refused before anything runs.",
        };
        var allowLockLimitOption = new Option<bool>(LockCapacity.OptionName)
        {
            Description = "Allow --execute to run a script estimated to need more locks than the server's lock table holds (max_locks_per_transaction * " +
                "(max_connections + max_prepared_transactions)). The whole script is one transaction, which keeps every relation it creates, alters, or drops " +
                "locked until it commits, so such a script is likely to fail partway with \"out of shared memory\" and roll back. Without this flag, --execute refuses it.",
        };
        var lockTimeoutOption = new Option<string?>("--lock-timeout")
        {
            HelpName = "duration",
            Description = "Wait at most this long for each lock the script needs (5s, 500ms, 2min): a statement that cannot get its lock in time fails, " +
                "the whole script is rolled back, and the error names the statement, instead of waiting behind a long transaction and blocking everything that queues " +
                "behind it. Off unless given. A printed or written script starts with SET lock_timeout.",
        };
        var concurrentlyOption = new Option<bool>("--concurrently")
        {
            Description = "Build new indexes on tables that already exist with CREATE INDEX CONCURRENTLY, and check new foreign keys and CHECK constraints after adding them " +
                "NOT VALID, after the transaction commits and one statement at a time, so they do not block writes to the table while they run. A new unique constraint, " +
                "or a primary key on columns that are already NOT NULL, is built the same way and added USING INDEX; a changed index is built under a temporary name and " +
                "swapped in. If one of those statements fails, what it was building is dropped, the error names it, and running publish --concurrently again does the rest.",
        };
        var excludeGrantsOption = CreateExcludeGrantsOption();
        var excludeRoleMembershipsOption = CreateExcludeRoleMembershipsOption();
        var excludeOwnersOption = CreateExcludeOwnersOption();
        var tenantsOption = DiffCommand.CreateTenantsOption(
            "Publish only these tenants, by identifier (acme, not tenant_acme), as a comma-separated list or the option repeated: to try a change on a few tenants, "
            + "to finish after a run that stopped, or to set up a new one. Only for a project with tenants; each must be a tenant the project already has. The shared "
            + "schemas are published either way.");
        var command = new Command("publish")
        {
            Description =
                """
                Parse the project's SQL files, build the semantic model, diff it against a live PostgreSQL database, and generate a deployment script that brings the database up to date.

                The database to update comes from --target-connection, the PGPROJ_TARGET_CONNECTION environment variable, or the targetConnectionString property in pgproj.json, in that order; one of them is required.

                With --source-connection, a live database is the desired state in place of the project's SQL files (environment promotion, dev -> QA -> prod): the target is changed to match the source. The first output line says "Source A -> Target B". The source is read in a read-only transaction and must not be the target; every check (destructive changes, locks, superuser statements, roles, tablespaces, server version) runs against the target, and what exists only in the target is dropped as in any publish (with --allow-destructive). The project's schemas, searchPath, exclude options (use --exclude-owners and --exclude-grants when environments differ), and pre/post-deploy scripts still apply, the scripts running against the target; pgproj.json may be absent. Not for a project with tenants.

                A project with tenants ("tenants" in pgproj.json, a schema per tenant built from one template) is published in parts: the shared changes in one transaction, with the pre- and post-deploy scripts, then each tenant in a transaction of its own, its script starting with SET search_path TO the tenant's schema. A tenant the tenant query or file lists whose schema does not exist yet has it created. Every part is checked before anything runs; a failure stops the run, says what was published, which part failed (rolled back), and what was not started, and publishing again finishes. A schema the query or file no longer lists is never dropped: a warning says to drop it by other means. --tenants a,b publishes only those tenants. A tenant's own scripts (tenants.predeploy and tenants.postdeploy) run in its transaction, around the template's changes, with :"tenant_schema", :'tenant_schema', and :'tenant_id' filled in; a file starting with -- pgproj: run when=tenant-changed or when=tenant-created runs only then, and a file of the project's own scripts marked when=changed only when there are changes. A printed or written script has a line before each tenant's part. See docs/notes/tenants.md.

                Printing, saving, or running the script:
                  - By default the script is printed to the console. Use --output to save it to a file, and --execute to run it directly against the database (use --dry-run to preview without running it).
                  - --execute runs the script in a single transaction; a script that is printed or written is not wrapped in one (a warning says so), so run it inside a transaction yourself (psql --single-transaction -f <file>).
                  - --execute refuses to run a script that can discard data unless --allow-destructive is also given.
                  - Before anything runs, a warning names each change that rewrites, scans, or blocks writes to a table that already exists (a column type change, SET NOT NULL, a new index or constraint, SET ACCESS METHOD, ...), which on a large table holds its lock for as long as it takes.
                  - --lock-timeout <duration> bounds how long each statement waits for a lock: past it, the script is rolled back and the statement named, instead of the publish stalling everything behind it.
                  - --concurrently moves the slow part of new indexes and constraints on tables that already exist out of the transaction: after it commits, each index is built with CREATE INDEX CONCURRENTLY (on a partitioned table: created ON ONLY the parent, then built on each partition and attached), a changed index is built under a temporary name and swapped in, and each new foreign key or CHECK constraint, added NOT VALID in the transaction, is validated. These statements run one at a time, each on its own; a failed one has what it built dropped, is named with the error, and the next publish --concurrently does the rest. A printed or written script has them after a line that says so; run the part above it in one transaction and the part below it without one.
                  - An index PostgreSQL marks INVALID (left by a failed CREATE INDEX CONCURRENTLY) is rebuilt: concurrently with --concurrently, in the transaction without it.
                  - --execute refuses a script with statements only a superuser can run (an event trigger; ALTER TABLE ... TRIGGER ALL on foreign-key triggers; a LEAKPROOF routine or one in an untrusted language such as c) unless --allow-superuser-statements is also given, and then refuses a connected role that is not a superuser, before running anything. A printed or written script starts with a comment naming them, and a warning says so.
                  - --execute refuses a script estimated to need more locks than the server's lock table holds (max_locks_per_transaction * (max_connections + max_prepared_transactions); one transaction keeps every relation it changes locked until it commits) unless --allow-lock-limit is also given; from 80% of it, publish warns, and a printed or written script is only warned about.
                  - --execute also refuses, before running anything, a project that names a tablespace or a role the server lacks or uses a feature the server's version lacks, or that changes a collation, a text search dictionary's template, or a configuration's parser that something still uses (it names each one); --dry-run and --output only warn about the first three.

                Comments only:
                  - --comments-only deploys just the COMMENT ON statements the script would contain, to publish documentation (see documentation.md) without touching the schema; every other change is left for a later publish, and the predeploy and postdeploy scripts are not included.
                  - A comment on an object the database does not have yet is skipped: the statement runs in a DO block that reports a warning ("pgproj publish --comments-only skipped a comment") and goes on, so the script also runs under psql. Publish again without --comments-only to create the object and set its comment.
                  - It works with --execute, --dry-run, and --output as usual. The checks that guard other changes (--allow-destructive, a missing tablespace or role, a feature the server lacks) do not apply, because nothing else is deployed.

                Schemas are never dropped: a schema the database has that the project no longer uses is left in place (its objects are dropped as usual), and a warning names it.

                --exclude-grants/--exclude-role-memberships/--exclude-owners override pgproj.json's excludeGrants/excludeRoleMemberships/excludeOwners for this run, in either direction.

                Diagram:
                  - --erd also writes an entity relationship diagram of the project's tables (Mermaid, from the SQL files, not the database): erd-<time>.mmd and a companion erd-<time>.md in the directory of pgproj.json, or the path given after --erd. It is written before the database is read, so --dry-run writes it too.
                  - --erd-schemas/--erd-exclude-schemas (not both) narrow the diagram to some schemas; a foreign key out of them is drawn to an (external) stub.
                  - --erd-html also writes a web page (erd-<time>.html) that draws the diagram in a browser; it loads Mermaid from a CDN.
                  - --erd-existing overwrite|backup|keep says what to do with a file already at the path; by default a file --erd wrote is backed up to name.<UTC time>.ext and any other file is kept.

                --verbose also writes each step and its time to standard error. --quiet leaves out the progress lines and warnings; the script (when it is printed), errors, and refusals are still written.

                When the change drops, renames, or retypes something the body of a PL/pgSQL routine names (a table or view, a function or procedure, a qualified column), a warning says which routine names what: PostgreSQL records no dependency for such a body, so nothing stops the change and the routine fails when it runs. The script and its order are unchanged.

                Examples:
                  pgproj publish --target-connection "Host=localhost;Database=app;Username=postgres"
                  pgproj publish --target-connection "Host=localhost;Database=app" --output ./deploy.sql
                  pgproj publish --target-connection "Host=localhost;Database=app" --execute
                  pgproj publish --target-connection "Host=localhost;Database=app" --execute --dry-run
                  pgproj publish --target-connection "Host=localhost;Database=app" --execute --allow-destructive
                  pgproj publish --target-connection "Host=localhost;Database=app" --execute --exclude-grants
                  pgproj publish --target-connection "Host=localhost;Database=app;Username=postgres" --execute --allow-superuser-statements
                  pgproj publish --target-connection "Host=localhost;Database=app" --execute --lock-timeout 5s
                  pgproj publish --target-connection "Host=localhost;Database=app" --execute --concurrently --lock-timeout 5s
                  pgproj publish --target-connection "Host=localhost;Database=app" --execute --allow-lock-limit
                  pgproj publish --target-connection "Host=localhost;Database=app" --comments-only --execute
                  pgproj publish --target-connection "Host=localhost;Database=app" --comments-only --output ./comments.sql
                  pgproj publish --target-connection "Host=localhost;Database=app" --output ./deploy.sql --quiet
                  pgproj publish --target-connection "Host=localhost;Database=app" --dry-run --erd docs/model.mmd
                  pgproj publish --target-connection "Host=localhost;Database=app" --dry-run --verbose
                  pgproj publish --target-connection "Host=localhost;Database=app" --execute --tenants acme,globex
                  pgproj publish --source-connection "Host=localhost;Database=qa" --target-connection "Host=localhost;Database=prod" --dry-run --exclude-owners
                """,
        };

        HelpLayout.SetSummary(command, "Generate the script that brings a live PostgreSQL database up to date, and optionally run it.");
        command.Options.Add(projectOption);
        command.Options.Add(connectionOption);
        var sourceOption = SourceProjectOptions.Create();
        command.Options.Add(sourceOption);
        command.Options.Add(executeOption);
        command.Options.Add(outputOption);
        command.Options.Add(dryRunOption);
        command.Options.Add(allowDestructiveOption);
        command.Options.Add(commentsOnlyOption);
        command.Options.Add(allowSuperuserStatementsOption);
        command.Options.Add(allowLockLimitOption);
        command.Options.Add(lockTimeoutOption);
        command.Options.Add(concurrentlyOption);
        command.Options.Add(tenantsOption);
        command.Options.Add(excludeGrantsOption);
        command.Options.Add(excludeRoleMembershipsOption);
        command.Options.Add(excludeOwnersOption);
        var erdOptions = ErdOptions.AddTo(command);
        var logOptions = LogOptions.AddTo(command);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var log = logOptions.Create(parseResult);
            try
            {
                var projectPath = parseResult.GetValue(projectOption) ?? "pgproj.json";
                var connectionString = parseResult.GetValue(connectionOption);
                var execute = parseResult.GetValue(executeOption);
                var outputPath = parseResult.GetValue(outputOption);
                var dryRun = parseResult.GetValue(dryRunOption);
                var commentsOnly = parseResult.GetValue(commentsOnlyOption);
                var lockTimeout = parseResult.GetValue(lockTimeoutOption);
                var concurrently = parseResult.GetValue(concurrentlyOption);
                if (PublishTexts.RequestProblem(concurrently, commentsOnly, lockTimeout) is { } problem)
                {
                    log.Error($"Publish failed: {problem}");
                    return 1;
                }

                var sourceConnection = parseResult.GetResult(sourceOption) is not null ? ConnectionSettings.ResolveInitSource(parseResult.GetValue(sourceOption)) : null;
                var erdDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? Environment.CurrentDirectory;
                var startedUtc = DateTime.UtcNow;
                var executing = execute && !dryRun;
                if (sourceConnection is null)
                {
                    log.Detail($"Reading project file: {Path.GetFullPath(projectPath)}");
                }

                // The engine reads the project and the databases, plans the script, checks it, and runs it (G-309c); this command prints what it returns, in the order it
                // always did: the build's header and diagnostics and the diagram first, then (once the script is planned, and before anything is run) the script and
                // the warnings, then what the run does as it goes.
                var result = await new PgProjEngine().PublishAsync(
                    new PublishRequest
                    {
                        ProjectFilePath = projectPath,
                        ExcludeGrants = parseResult.GetValue(excludeGrantsOption),
                        ExcludeRoleMemberships = parseResult.GetValue(excludeRoleMembershipsOption),
                        ExcludeOwners = parseResult.GetValue(excludeOwnersOption),
                        TargetConnectionString = ConnectionSettings.ShellTarget(connectionString),
                        SourceConnectionString = sourceConnection,
                        Tenants = DiffCommand.SplitTenantIdentifiers(parseResult.GetValue(tenantsOption)),
                        Mode = executing ? PublishMode.Execute : PublishMode.Plan,
                        AllowDestructive = parseResult.GetValue(allowDestructiveOption),
                        AllowSuperuserStatements = parseResult.GetValue(allowSuperuserStatementsOption),
                        AllowLockLimit = parseResult.GetValue(allowLockLimitOption),
                        LockTimeout = lockTimeout,
                        Concurrently = concurrently,
                        CommentsOnly = commentsOnly,
                        Progress = new LogProgress(
                            log,
                            announceProjectFile: sourceConnection is not null,
                            keepsScript: !executing || !string.IsNullOrWhiteSpace(outputPath),
                            writeScript: script => WriteScript(script, outputPath, executing, log)),
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

                switch (result.Status)
                {
                    case PublishStatus.ProjectNotFound:
                        log.Error("Publish failed: project file was not found.");
                        log.Error($"Looked for: {Path.GetFullPath(projectPath)}");
                        log.Error("Use --project <path> to specify a pgproj.json file, or create one based on pgproj.sample.json.");
                        return 1;
                    case PublishStatus.Failed:
                        return 1;
                    case PublishStatus.Refused when result.Refusals.Count > 0:
                        // What the checks refused, after the warnings the engine reported as it found them; a tenants publish that stopped after the shared part says so.
                        foreach (var refusal in result.Refusals)
                        {
                            log.Error(refusal.Text);
                        }

                        if (result.Message is not null)
                        {
                            log.Error(result.Message);
                        }

                        return 1;
                    case PublishStatus.ExecutionFailed when result.Execution is { FailureReport.Count: > 0 } execution:
                        foreach (var line in execution.FailureReport)
                        {
                            log.Error(line);
                        }

                        return 1;
                    case not (PublishStatus.Planned or PublishStatus.Published):
                        log.Error($"Publish failed: {result.Message}");
                        return 1;
                }

                return 0;
            }
            catch (FileNotFoundException)
            {
                log.Error("Publish failed: project file was not found.");
                log.Error($"Looked for: {Path.GetFullPath(parseResult.GetValue(projectOption) ?? "pgproj.json")}");
                log.Error("Use --project <path> to specify a pgproj.json file, or create one based on pgproj.sample.json.");
                return 1;
            }
            catch (Exception exception)
            {
                log.Error($"Publish failed: {exception.Message}");
                return 1;
            }
        });

        return command;
    }

    /// <summary>
    /// The planned script, when the engine has composed it and before anything is run (G-309c, G-316): to a file with <c>--output</c>, and to the console unless it is
    /// run. The warnings are written as the engine finds them, and the refusals from the result.
    /// </summary>
    private static void WriteScript(string script, string? outputPath, bool executing, CliLog log)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var fullOutputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory);
            File.WriteAllText(fullOutputPath, script);
            log.Info($"Wrote deployment script: {fullOutputPath}");
        }

        if (!executing)
        {
            Console.WriteLine(script);
        }
    }

    /// <summary>A duration PostgreSQL's <c>lock_timeout</c> accepts (G-270); the engine decides.</summary>
    internal static bool IsDuration(string value) => PublishTexts.IsDuration(value);

    /// <summary>The warning for a script that is printed or written without a transaction (G-165); the engine words it.</summary>
    internal const string ScriptNotTransactionalWarning = PublishTexts.ScriptNotTransactionalWarning;

    /// <summary>The same for a <c>--concurrently</c> script (G-271).</summary>
    internal const string ConcurrentScriptWarning = PublishTexts.ConcurrentScriptWarning;

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
