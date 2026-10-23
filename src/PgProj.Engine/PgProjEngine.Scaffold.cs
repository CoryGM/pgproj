using System.Diagnostics;
using Npgsql;
using PgProj.Core;
using PgProj.Parser;

namespace PgProj.Engine;

public sealed partial class PgProjEngine
{
    /// <summary>
    /// Reads a live database into a new project (what <c>pgproj init</c> does, G-309d): one SQL file per object and a <c>pgproj.json</c>, and for a database with a
    /// schema per tenant a template and the tenants that differ from it. It returns the files' text and writes none: the shell writes them in
    /// <see cref="ScaffoldRequest.AfterScaffold"/> or once it returns. A request that cannot be run is an <see cref="InitStatus"/>, not an exception; a defect is an
    /// exception, as is a cancelled <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task<InitResult> ScaffoldAsync(ScaffoldRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourceConnectionString))
        {
            throw new ArgumentException("A request names the database to read (SourceConnectionString).", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Every message it returns or reports is masked (G-318); the files it scaffolds are the database's SQL, as they are.
        request = request with { Progress = EngineMasking.Progress(request.Progress) };
        return EngineMasking.Init(await ScaffoldOrStopAsync(request, cancellationToken));
    }

    private static async Task<InitResult> ScaffoldOrStopAsync(ScaffoldRequest request, CancellationToken cancellationToken)
    {
        var fullProjectPath = Path.GetFullPath(request.ProjectFilePath);
        var projectDirectory = Path.GetDirectoryName(fullProjectPath) ?? Environment.CurrentDirectory;
        var fullOutputPath = Path.GetFullPath(request.OutputDirectory, projectDirectory);
        var failed = new InitResult { ProjectFilePath = fullProjectPath, OutputDirectory = fullOutputPath };
        var connecting = false;
        try
        {
            if (string.IsNullOrWhiteSpace(request.TenantPrefix) && (request.TenantFrom is not null || request.TenantFile || request.TenantQuery is not null))
            {
                throw new RefusedException("--tenant-from, --tenant-file, and --tenant-query need --tenant-prefix.");
            }

            if (request.TenantFile && request.TenantQuery is not null)
            {
                throw new RefusedException("--tenant-file and --tenant-query cannot be used together: the tenants come from a file or from a query.");
            }

            connecting = true;
            var stopwatch = Stopwatch.StartNew();
            await using var connection = await LiveComparison.OpenConnectionAsync(request.SourceConnectionString, cancellationToken);
            connecting = false;
            return await RunScaffoldAsync(request, connection, stopwatch, fullProjectPath, projectDirectory, fullOutputPath, cancellationToken);
        }
        catch (RefusedException exception)
        {
            return failed with { Status = InitStatus.Refused, Message = exception.Message };
        }
        catch (NpgsqlException exception) when (connecting)
        {
            return failed with { Status = InitStatus.ConnectionFailed, Message = exception.Message };
        }
        catch (System.Net.Sockets.SocketException exception) when (connecting)
        {
            return failed with { Status = InitStatus.ConnectionFailed, Message = exception.Message };
        }
    }

    private static async Task<InitResult> RunScaffoldAsync(
        ScaffoldRequest request,
        NpgsqlConnection connection,
        Stopwatch stopwatch,
        string fullProjectPath,
        string projectDirectory,
        string fullOutputPath,
        CancellationToken cancellationToken)
    {
        var startedUtc = request.StartedUtc ?? DateTime.UtcNow;
        var progress = request.Progress;
        var live = new LiveComparison();
        var serverVersion = connection.PostgreSqlVersion.ToString();
        var database = connection.Database ?? string.Empty;
        progress?.Report(new EngineEvent(EngineEventKind.SourceConnected, null, stopwatch.ElapsedMilliseconds, $"Connected to PostgreSQL {serverVersion} (database \"{connection.Database}\") in {stopwatch.ElapsedMilliseconds} ms."));
        stopwatch.Restart();

        var (includeSchemas, excludeSchemas) = (request.IncludeSchemas, request.ExcludeSchemas);
        var (excludeGrants, excludeRoleMemberships, excludeOwners) = (request.ExcludeGrants, request.ExcludeRoleMemberships, request.ExcludeOwners);

        // Whether the files can differ only by case is the output folder's to say (G-212), which the shell answers through its file source.
        var caseSensitivePaths = request.Files.IsCaseSensitive(fullOutputPath);

        // A database with a schema per tenant (G-290d): the shared schemas without the tenants', and one tenant as the template.
        InitTenants.Choice? tenantChoice = null;
        InitTenants.Template? template = null;
        IReadOnlyList<string>? tenantSearchPath = null;
        var sharedExclude = excludeSchemas;
        if (!string.IsNullOrWhiteSpace(request.TenantPrefix))
        {
            tenantChoice = await InitTenants.ChooseAsync(connection, request.TenantPrefix, request.TenantFrom, cancellationToken);
            if (includeSchemas?.Select(SqlIdentifiers.FoldName).FirstOrDefault(schema => tenantChoice.TenantSchemas.Contains(schema, StringComparer.Ordinal) || schema == tenantChoice.Prefix) is { } tenantNamed)
            {
                throw new RefusedException($"--schemas names '{tenantNamed}', which is a tenant's schema (or the stand-in's name) with --tenant-prefix {tenantChoice.Prefix}; tenants are scaffolded into the template.");
            }

            // The tenants' schemas, and one named as the stand-in, are not shared (a schema the database lacks is ignored).
            if (includeSchemas is null)
            {
                sharedExclude = [.. (excludeSchemas ?? []).Select(SqlIdentifiers.FoldName), .. tenantChoice.TenantSchemas, tenantChoice.Prefix];
            }

            var tenantScaffold = await ScaffoldPartAsync(live, connection, new ScaffoldOptions([SqlIdentifiers.QuoteName(tenantChoice.TemplateSchema)], null, excludeGrants, excludeRoleMemberships, excludeOwners, caseSensitivePaths), cancellationToken);
            template = InitTenants.ToTemplate(tenantScaffold.Files, tenantChoice.TemplateSchema, tenantChoice.Prefix);
            tenantSearchPath = tenantScaffold.SearchPath?.Where(schema => schema != tenantChoice.TemplateSchema).ToArray();
        }

        var scaffold = await ScaffoldPartAsync(live, connection, new ScaffoldOptions(includeSchemas, sharedExclude?.Select(SqlIdentifiers.QuoteName).ToArray(), excludeGrants, excludeRoleMemberships, excludeOwners, caseSensitivePaths), cancellationToken);
        IReadOnlyList<ScaffoldedFile> files = template is null ? scaffold.Files : [.. scaffold.Files, .. template.Files];
        if (request.Diagram is { } diagramFilter)
        {
            CheckDiagramSchemas(diagramFilter, scaffold.ModeledSchemas);
        }

        progress?.Report(new EngineEvent(EngineEventKind.SourceRead, null, stopwatch.ElapsedMilliseconds, $"Read the live database and grouped it into {files.Count} file(s) in {stopwatch.ElapsedMilliseconds} ms."));

        var projectName = string.IsNullOrWhiteSpace(request.Name) ? InitProjectFile.GetDatabaseNameOrDefault(request.SourceConnectionString) : request.Name;
        var sourceRelativePath = Path.GetRelativePath(projectDirectory, fullOutputPath).Replace(Path.DirectorySeparatorChar, '/');
        var source = sourceRelativePath.StartsWith('.') ? sourceRelativePath : $"./{sourceRelativePath}";
        var searchPath = tenantSearchPath is { Count: > 0 } ? [.. scaffold.SearchPath ?? [], .. tenantSearchPath.Where(schema => !(scaffold.SearchPath ?? []).Contains(schema, StringComparer.Ordinal))] : scaffold.SearchPath;
        var tenantsJson = tenantChoice is null ? null : new InitProjectFile.TenantsJson($"{source.TrimEnd('/')}/{InitTenants.TemplateFolder}", tenantChoice.Prefix, request.TenantFile ? "./" + InitResult.TenantFileName : null, request.TenantQuery);
        var projectJson = InitProjectFile.Build(projectName, source, InitProjectFile.RedactPassword(request.SourceConnectionString), excludeGrants, excludeRoleMemberships, excludeOwners, includeSchemas is not null || excludeSchemas is not null ? scaffold.ModeledSchemas : null, searchPath, tenantsJson);
        var tenantFileText = tenantChoice is not null && request.TenantFile ? InitTenants.BuildCsv(tenantChoice.Identifiers) : null;

        // The new project read back as it will be on disk (the tenants are compared with it, and the diagram is built from its parse trees): laid over the shell's files,
        // so what is already in the output folder is read too.
        BuildResult? project = null;
        ModelBuild? built = null;
        if (tenantChoice is not null || request.Diagram is not null)
        {
            var readBack = Stopwatch.StartNew();
            var overlay = new OverlayFileSource(request.Files);
            overlay.AddDirectory(fullOutputPath);
            foreach (var file in files)
            {
                overlay.Set(Path.Combine(fullOutputPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)), file.Content);
            }

            overlay.Set(fullProjectPath, projectJson);
            if (tenantFileText is not null)
            {
                overlay.Set(Path.Combine(projectDirectory, InitResult.TenantFileName), tenantFileText);
            }

            var snapshot = ProjectSnapshot.Read(overlay, fullProjectPath, null, null, cancellationToken);
            built = ProjectModelBuilder.Build(snapshot, excludeGrants, excludeRoleMemberships, excludeOwners, likeSourceTrees: null, cancellationToken);
            project = ToBuildResult(snapshot, built, fullProjectPath, readBack.ElapsedMilliseconds);
        }

        var messages = new List<InitMessage>();
        ScaffoldedTenants? tenants = null;
        InitTenantSummary? tenantSummary = null;
        if (tenantChoice is not null && template is not null)
        {
            messages.Add(new(EngineEventLevel.Information, $"Tenants: {tenantChoice.TenantSchemas.Count} schema(s) named {tenantChoice.Prefix}_...; the template ({InitTenants.TemplateFolder}, {template.Files.Count} file(s)) was scaffolded from {tenantChoice.TemplateSchema}."));
            if (template.LeftOut.Count > 0)
            {
                messages.Add(new(EngineEventLevel.Warning, $"Left out of the template, since they belong to the whole database rather than a schema: {string.Join(", ", template.LeftOut)}."));
            }

            if (template.BodiesNamingSchema.Count > 0)
            {
                messages.Add(new(EngineEventLevel.Warning, $"These template files have a function body that still names {tenantChoice.TemplateSchema} (a body is kept as written); change it to the tenant's own objects without a schema: {string.Join(", ", template.BodiesNamingSchema)}."));
            }

            var differing = await InitTenants.FindDifferingAsync(live, connection, built!, tenantChoice, excludeGrants, excludeRoleMemberships, excludeOwners, cancellationToken);
            if (differing.Count == 0)
            {
                messages.Add(new(EngineEventLevel.Information, "Every other tenant matches the template."));
            }
            else
            {
                messages.Add(new(EngineEventLevel.Warning, $"{differing.Count} tenant(s) differ from the template; pgproj diff shows how, and pgproj publish would make each match it: "
                    + string.Join(", ", differing.Take(10).Select(static item => $"{item.Schema} ({item.Added} added, {item.Removed} removed, {item.Modified} modified)"))
                    + (differing.Count > 10 ? $", and {differing.Count - 10} more (the --report lists them all)" : string.Empty) + "."));
            }

            tenantSummary = new InitTenantSummary(tenantChoice.Prefix, tenantChoice.TemplateSchema, tenantChoice.TenantSchemas, differing, template.LeftOut, template.BodiesNamingSchema);
            tenants = new ScaffoldedTenants(
                tenantChoice.Prefix,
                tenantChoice.TemplateSchema,
                tenantChoice.TenantSchemas,
                template.Files.Count,
                [.. differing.Select(static item => new InitTenantDifference(item.Schema, item.Added, item.Removed, item.Modified))],
                template.LeftOut,
                template.BodiesNamingSchema);
        }

        if (searchPath is { Count: > 0 } neededSearchPath)
        {
            messages.Add(new(EngineEventLevel.Information, $"Set \"searchPath\" to [{string.Join(", ", neededSearchPath.Select(static schema => $"\"{schema}\""))}, \"public\"]: objects in {(neededSearchPath.Count == 1 ? "that schema use" : "those schemas use")} unqualified names that the database found through its search_path."));
        }

        // A base type is not modeled (G-241): it and its input and output functions are left out, and what uses it needs it created first.
        if (scaffold.UnsupportedObjects.Where(static item => item.ObjectType is "Base type" or "Shell type").Select(static item => $"{item.SchemaName}.{item.Name}").ToArray() is { Length: > 0 } baseTypes)
        {
            messages.Add(new(EngineEventLevel.Warning, $"Left out {(baseTypes.Length == 1 ? "a base type" : "base types")}, with {(baseTypes.Length == 1 ? "its" : "their")} input and output functions: {string.Join(", ", baseTypes)}. pgproj does not model base types (they need a superuser); create {(baseTypes.Length == 1 ? "it" : "them")} before publishing anything that uses {(baseTypes.Length == 1 ? "it" : "them")}, for example in a predeploy script run by a superuser."));
        }

        // An INVALID index is left out as pg_dump leaves it out (G-271): it is not used by queries, and the project should state the index it was meant to be.
        if (scaffold.UnsupportedObjects.Where(static item => item.ObjectType == UnsupportedObject.InvalidIndex).Select(static item => $"{item.SchemaName}.{item.Name}").ToArray() is { Length: > 0 } invalidIndexes)
        {
            messages.Add(new(EngineEventLevel.Warning, $"Left out {(invalidIndexes.Length == 1 ? "an index" : "indexes")} PostgreSQL marks INVALID (left by a failed CREATE INDEX CONCURRENTLY), as pg_dump does: {string.Join(", ", invalidIndexes)}. Add {(invalidIndexes.Length == 1 ? "it" : "them")} to the project if {(invalidIndexes.Length == 1 ? "it is" : "they are")} wanted; publish rebuilds an invalid index the project states."));
        }

        // A table whose internal (foreign-key) triggers are in mixed states cannot be stated by TRIGGER ALL (G-261): the most common state is written.
        if (scaffold.UnsupportedObjects.Where(static item => item.ObjectType == UnsupportedObject.MixedInternalTriggerStates).Select(static item => $"{item.SchemaName}.{item.Name}").ToArray() is { Length: > 0 } mixedTables)
        {
            messages.Add(new(EngineEventLevel.Warning, $"The internal (foreign-key) triggers of {(mixedTables.Length == 1 ? "a table are" : "some tables are")} not all in one state ENABLE or DISABLE TRIGGER ALL can state, so {(mixedTables.Length == 1 ? "its" : "their")} most common state was written: {string.Join(", ", mixedTables)}."));
        }

        var result = new InitResult
        {
            Status = InitStatus.Succeeded,
            ProjectFilePath = fullProjectPath,
            OutputDirectory = fullOutputPath,
            ServerVersion = serverVersion,
            Database = database,
            Files = files,
            ProjectFileText = projectJson,
            TenantFileText = tenantFileText,
            ReportFileName = request.Report ? InitReport.FileName(startedUtc) : null,
            DiagramText = request.Diagram is { } filter && built is { Model.HasErrors: false } ? ErdDiagram.Build(built.Model, filter) : null,
            SearchPath = searchPath ?? [],
            ModeledSchemas = scaffold.ModeledSchemas,
            UnsupportedObjects = scaffold.UnsupportedObjects,
            UnreadObjectKinds = InitProjectFile.UnreadObjectKinds,
            Tenants = tenants,
            Messages = messages,
            Project = project,
        };

        // The shell writes the files and the diagram here; the report, made after, links the diagram and says what was done about a file already at its path.
        var diagram = request.AfterScaffold is { } afterScaffold ? await afterScaffold(EngineMasking.Init(result), cancellationToken) : null;
        if (!request.Report)
        {
            return result;
        }

        var endedUtc = DateTime.UtcNow;
        var connectionBuilder = new NpgsqlConnectionStringBuilder(request.SourceConnectionString);
        var reportText = InitReport.Build(new InitReportInput(
            startedUtc,
            endedUtc,
            [.. request.ReportArguments.Select(static argument => (argument.Name, SecretRedactor.Redact(argument.Value)))],
            connectionBuilder.Host ?? string.Empty,
            connectionBuilder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            serverVersion,
            database,
            projectDirectory,
            fullOutputPath,
            includeSchemas is not null ? "--schemas" : excludeSchemas is not null ? "--exclude-schemas" : null,
            scaffold,
            InitProjectFile.UnreadObjectKinds,
            excludeGrants,
            excludeRoleMemberships,
            excludeOwners,
            diagram is null ? null : Path.GetRelativePath(fullOutputPath, diagram.Path).Replace('\\', '/'),
            diagram?.Placement,
            tenantSummary));
        return result with { ReportText = reportText };
    }

    /// <summary>One scaffold of the database; a choice of schemas the loader cannot make (a schema the database lacks, none left) is the user's to fix.</summary>
    private static async Task<ScaffoldResult> ScaffoldPartAsync(LiveComparison live, NpgsqlConnection connection, ScaffoldOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return await live.Loader.ScaffoldAsync(connection, cancellationToken, options);
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("--", StringComparison.Ordinal))
        {
            throw new RefusedException(exception.Message, exception);
        }
    }

    /// <summary>A schema the diagram's filter names that was not scaffolded is an error that names it.</summary>
    private static void CheckDiagramSchemas(ErdFilter filter, IReadOnlyCollection<string> schemas)
    {
        var missing = filter.Names.Where(name => !schemas.Contains(name, SqlIdentifiers.NameComparer)).ToArray();
        if (missing.Length > 0)
        {
            throw new RefusedException($"{filter.OptionName} names {(missing.Length == 1 ? "a schema" : "schemas")} the project has no table in: {string.Join(", ", missing)}.");
        }
    }
}
