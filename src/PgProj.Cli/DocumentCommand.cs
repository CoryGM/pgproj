using PgProj.Engine;
using System.CommandLine;
using System.Globalization;
using PgProj.Cli.Documentation;

namespace PgProj.Cli;

/// <summary><c>pgproj document</c> (G-159): writes the database's documentation, a data dictionary and an entity relationship diagram, as HTML, Markdown, and JSON, from the project's files or a live database.</summary>
public static class DocumentCommand
{
    public static Command Create(CliContext cliContext)
    {
        ArgumentNullException.ThrowIfNull(cliContext);

        var projectOption = new Option<string>("--project")
        {
            HelpName = "path",
            Description = "Path to the pgproj.json project file to document. Defaults to 'pgproj.json' in the current directory. Not read when --source-connection is given.",
            DefaultValueFactory = _ => "pgproj.json",
        };
        var connectionOption = new Option<string?>("--source-connection")
        {
            HelpName = "connection-string",
            Description = "Document this live PostgreSQL database (the source) instead of the project's files. " +
                $"With no value, the {ConnectionSettings.SourceVariable} environment variable is used, then sourceConnectionString from pgproj.json.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var formatOption = new Option<string[]>("--format")
        {
            HelpName = "html,markdown,json",
            Description = "The formats to write: html, markdown, and json, as a comma-separated list or the option repeated. Defaults to html and json.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true,
        };
        var outputOption = new Option<string?>("--output")
        {
            HelpName = "directory",
            Description = "The directory to write database-documentation.html, .md, and .json to. Relative to the directory of pgproj.json (or to the current directory with --source-connection). Defaults to 'docs'.",
        };
        var existingOption = new Option<string?>("--existing")
        {
            HelpName = "overwrite|backup|keep",
            Description = "What to do when a file is already there: 'overwrite' replaces it; 'backup' renames it to name.<UTC time>.ext first and writes the new one at the path; " +
                "'keep' leaves it and writes the new one to name.<UTC time>.ext. All the files are handled together. Without this option a file 'document' wrote is backed up, and a file it did not write is kept.",
        };
        var schemasOption = new Option<string[]>("--schemas")
        {
            HelpName = "schemas",
            Description = "Document only these schemas (and draw only their tables), as a comma-separated list or the option repeated. 'public' is only included if it is named. Cannot be combined with --exclude-schemas. A name is folded to lower case unless it is in double quotes, as pg_dump -n does.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true,
        };
        var excludeSchemasOption = new Option<string[]>("--exclude-schemas")
        {
            HelpName = "schemas",
            Description = "Document every schema except these, in the same list format. Cannot be combined with --schemas.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true,
        };
        var noFailOption = new Option<bool>("--no-fail-on-doc-errors")
        {
            Description = "Exit with 0 even when a comment's tags have errors (a date that is not ISO 8601, both retention tags, ...). The files are written and the errors listed either way; by default the exit code is 1 after an error.",
        };

        var command = new Command("document")
        {
            Description =
                """
                Writes the documentation of a database: a data dictionary (schemas, tables with their columns, keys, constraints, triggers, row-level security policies, and indexes, views, functions and procedures, types, and sequences) and an entity relationship diagram, as HTML, Markdown, and JSON. It documents the project's SQL files, or with --source-connection a live database, and the result is the same either way.

                What an object's comment says is part of the documentation. Write it with COMMENT ON: the text is the description, and lines that start with @tag: value add structure. A comment can be left plain; every tag is optional. The tags are:
                  @business-name     the name business users know it by
                  @description       the description (the text before the first tag is the description too; both are shown)
                  @owner             who is responsible for the meaning of the data (not the database owner)
                  @classification    the data classification, in whatever scheme you use
                  @pii               true or false (yes and no work too)
                  @deprecated        alone, or with the reason or the replacement
                  @since-version     the release it appeared in
                  @since-date        an ISO 8601 date (2026-10-02) or date and time (2026-10-02T14:03:00Z)
                  @retention-months  how many months the data is kept (a whole number above zero)
                  @retention-date    the UTC date (or time ending in Z) it is kept until; not together with @retention-months
                  @example           a sample value (may be repeated)
                  @see-also          another object (app.orders) or a URL (may be repeated)

                Some tags apply only to some objects (pii: tables, views, columns; classification and the retention tags: schemas, tables, views, columns; @example: columns, constraints, routines, types); one on another kind of object is shown with a warning that does not change the exit code. Any other @name: value is kept and shown as a custom attribute, and is never checked. A value continues on the indented lines after it.

                  COMMENT ON COLUMN app.customer.email IS $$
                  The address receipts and password resets are sent to.

                  @business-name: Customer Email
                  @pii: true
                  @classification: confidential
                  @retention-months: 84
                  @example: jane@example.com
                  @see-also: app.customer
                  $$;

                A tag whose value cannot be read is shown with the object and listed in a summary. The files are still written; the exit code is 1 after such an error unless --no-fail-on-doc-errors is given. A schema's, a table constraint's, a trigger's, or a policy's comment is its description. Comments on event triggers, rules, and domains' constraints are not read (pgproj does not model them yet).

                Output:
                  - --format chooses the formats (html and json by default).
                  - --output is the directory (docs by default, under the directory of pgproj.json). Files already there are backed up to name.<UTC time>.ext if document wrote them and kept if not, unless --existing says otherwise.
                  - --schemas or --exclude-schemas limit the document (and the diagram) to some schemas; run the command once per schema for a document of each.
                  - The HTML page loads Mermaid from a CDN to draw the diagram, so it needs internet access to show it.

                --verbose also writes each step and the time taken to standard error; --quiet writes only the errors (to standard error).

                Examples:
                  pgproj document
                  pgproj document --format html,markdown,json --output ./docs
                  pgproj document --source-connection "Host=localhost;Database=app" --format markdown
                  pgproj document --schemas billing --output ./docs/billing
                  pgproj document --existing overwrite --no-fail-on-doc-errors
                """,
        };

        HelpLayout.SetSummary(command, "Write the database's documentation: a data dictionary and an entity relationship diagram, as HTML, Markdown, and JSON.");
        command.Options.Add(projectOption);
        command.Options.Add(connectionOption);
        command.Options.Add(formatOption);
        command.Options.Add(outputOption);
        command.Options.Add(existingOption);
        command.Options.Add(schemasOption);
        command.Options.Add(excludeSchemasOption);
        command.Options.Add(noFailOption);
        command.Validators.Add(result =>
        {
            if (result.GetResult(formatOption) is not null)
            {
                var names = SplitNames(result.GetValue(formatOption));
                var unknown = names.Where(static name => DocumentationFiles.ParseFormat(name) is null).ToArray();
                if (names.Count == 0 || unknown.Length > 0)
                {
                    result.AddError(unknown.Length > 0 ? $"--format takes html, markdown, and json, not {string.Join(", ", unknown)}." : "A list of formats is needed after --format.");
                }
            }

            if (result.GetResult(existingOption) is not null && ErdOptions.ParseExisting(result.GetValue(existingOption)) is null)
            {
                result.AddError("--existing takes overwrite, backup, or keep.");
            }

            var schemasGiven = result.GetResult(schemasOption) is not null;
            var excludeGiven = result.GetResult(excludeSchemasOption) is not null;
            if (schemasGiven && excludeGiven)
            {
                result.AddError("--schemas and --exclude-schemas cannot be used together.");
            }
            else if ((schemasGiven && InitCommand.SplitSchemaNames(result.GetValue(schemasOption)).Count == 0) || (excludeGiven && InitCommand.SplitSchemaNames(result.GetValue(excludeSchemasOption)).Count == 0))
            {
                result.AddError("A list of schema names is needed after --schemas and --exclude-schemas.");
            }
        });
        var logOptions = LogOptions.AddTo(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var log = logOptions.Create(parseResult);
            try
            {
                var startedUtc = DateTime.UtcNow;
                // Only --source-connection on the command line makes document read a database; the variable and the file supply its value, they never switch the mode (G-304a).
                string? connectionString = null;
                if (parseResult.GetResult(connectionOption) is not null)
                {
                    var optionValue = parseResult.GetValue(connectionOption);
                    var fromProject = string.IsNullOrWhiteSpace(optionValue) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionSettings.SourceVariable))
                        ? cliContext.LoadProject(parseResult.GetValue(projectOption) ?? "pgproj.json").SourceConnectionString
                        : null;
                    connectionString = ConnectionSettings.ResolveSource(optionValue, fromProject);
                }

                var formats = parseResult.GetResult(formatOption) is null
                    ? [DocFormat.Html, DocFormat.Json]
                    : SplitNames(parseResult.GetValue(formatOption)).Select(static name => DocumentationFiles.ParseFormat(name)!.Value).Distinct().ToArray();
                var existing = parseResult.GetResult(existingOption) is null ? ErdExisting.Default : ErdOptions.ParseExisting(parseResult.GetValue(existingOption)) ?? ErdExisting.Default;
                var filter = parseResult.GetResult(schemasOption) is not null
                    ? new ErdFilter(InitCommand.SplitSchemaNames(parseResult.GetValue(schemasOption)), Exclude: false)
                    : parseResult.GetResult(excludeSchemasOption) is not null
                        ? new ErdFilter(InitCommand.SplitSchemaNames(parseResult.GetValue(excludeSchemasOption)), Exclude: true)
                        : ErdFilter.None;

                var databaseMode = !string.IsNullOrWhiteSpace(connectionString);
                var projectPath = parseResult.GetValue(projectOption) ?? "pgproj.json";
                if (!databaseMode)
                {
                    log.Detail($"Reading project file: {Path.GetFullPath(projectPath)}");
                }

                // The engine reads the project or the database and renders the documentation (G-309d); the command places and writes the files.
                var result = await new PgProjEngine().DocumentAsync(
                    new DocumentRequest
                    {
                        ProjectFilePath = databaseMode ? null : projectPath,
                        SourceConnectionString = databaseMode ? connectionString : null,
                        Formats = formats,
                        Filter = filter,
                        GeneratedUtc = startedUtc,
                        Progress = new LogProgress(log),
                        AfterBuild = (build, _) =>
                        {
                            log.Info($"Loaded project: {build.ProjectName}");
                            log.Info($"Source path: {build.SourcePath}");
                            log.Info($"SQL files: {build.Documents.Count}");
                            foreach (var line in build.TenantLines)
                            {
                                log.Info(line);
                            }

                            cliContext.WriteDiagnostics(build.Model!, log);
                            return Task.CompletedTask;
                        },
                    },
                    cancellationToken);

                switch (result.Status)
                {
                    case DocumentStatus.ProjectNotFound:
                        WriteProjectNotFound(log, projectPath);
                        return 1;
                    case DocumentStatus.Failed:
                        log.Error("No documentation was written: the project has errors (see the diagnostics).");
                        return 1;
                    case not DocumentStatus.Succeeded:
                        log.Error($"Document failed: {result.Message}");
                        return 1;
                }

                var baseDirectory = databaseMode ? Environment.CurrentDirectory : Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? Environment.CurrentDirectory;
                var directory = Path.GetFullPath(parseResult.GetValue(outputOption) is { Length: > 0 } output ? output : "docs", baseDirectory);
                await DocumentationFiles.WriteAsync(directory, result.Files, result.GeneratedUtc, existing, log, cancellationToken);

                var counts = result.Counts!;
                log.Info($"Documented {counts.Schemas} schema(s): {counts.Tables} table(s) with {counts.Columns} column(s), {counts.Views} view(s), {counts.Routines} function(s) and procedure(s), {counts.Types} type(s), {counts.Sequences} sequence(s).");
                foreach (var problem in result.Problems)
                {
                    var text = $"{problem.Object}: {problem.Message}";
                    if (problem.Severity == DocSeverity.Error)
                    {
                        log.Error($"Documentation error: {text}");
                    }
                    else
                    {
                        log.Warning($"Documentation warning: {text}");
                    }
                }

                if (counts.Errors > 0 && !parseResult.GetValue(noFailOption))
                {
                    log.Error($"{counts.Errors.ToString(CultureInfo.InvariantCulture)} documentation error(s). The files were written; use --no-fail-on-doc-errors to exit with 0 anyway.");
                    return 1;
                }

                return 0;
            }
            catch (FileNotFoundException)
            {
                WriteProjectNotFound(log, parseResult.GetValue(projectOption) ?? "pgproj.json");
                return 1;
            }
            catch (Exception exception)
            {
                log.Error($"Document failed: {exception.Message}");
                return 1;
            }
        });

        return command;
    }

    private static void WriteProjectNotFound(CliLog log, string projectPath)
    {
        log.Error("Document failed: project file was not found.");
        log.Error($"Looked for: {Path.GetFullPath(projectPath)}");
        log.Error("Use --project <path> to specify a pgproj.json file, or --source-connection to document a live database.");
    }

    /// <summary>A comma-separated list, or several, as the names in it.</summary>
    private static IReadOnlyList<string> SplitNames(string[]? values)
        => (values ?? []).SelectMany(static value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Where(static name => name.Length > 0).ToArray();
}
