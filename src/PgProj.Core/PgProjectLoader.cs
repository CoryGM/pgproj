using System.Text.Json;

namespace PgProj.Core;

public sealed class PgProjectLoader
{
    /// <summary>Loads the project file on disk.</summary>
    public PgProject Load(string projectFilePath) => Load(DiskFileSource.Instance, projectFilePath);

    /// <summary>Loads the project file at <paramref name="projectFilePath"/> in <paramref name="source"/>, and reads everything else the project names (its scripts and tenant file) from there too (G-309a).</summary>
    public PgProject Load(IProjectFileSource source, string projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);

        var fullPath = Path.GetFullPath(projectFilePath);
        if (!source.FileExists(fullPath))
        {
            throw new FileNotFoundException($"Project file '{fullPath}' was not found.", fullPath);
        }

        var projectDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Unable to resolve the project directory for '{fullPath}'.");

        PgProjectConfiguration configuration;
        try
        {
            var json = source.ReadAllText(fullPath);
            configuration = JsonSerializer.Deserialize<PgProjectConfiguration>(json, JsonSerializerOptions)
                ?? new PgProjectConfiguration();
        }
        catch (JsonException exception)
        {
            throw new PgProjectValidationException($"Project file '{fullPath}' contains invalid JSON: {exception.Message}", exception);
        }

        return Build(source, configuration, projectDirectory, fullPath);
    }

    /// <summary>
    /// Loads a project from its settings and no file (G-309a), validated exactly as the file is. Relative paths are resolved against
    /// <paramref name="rootDirectory"/>, and a message that names the project file names <c>pgproj.json</c> in that directory.
    /// </summary>
    public PgProject Load(IProjectFileSource source, ProjectSettings settings, string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var projectDirectory = Path.GetFullPath(rootDirectory);
        var configuration = new PgProjectConfiguration
        {
            Name = settings.Name,
            Version = settings.Version,
            Schemas = settings.Schemas,
            Source = settings.Source,
            SourceConnectionString = settings.SourceConnectionString,
            TargetConnectionString = settings.TargetConnectionString,
            PreDeploy = settings.PreDeploy,
            PostDeploy = settings.PostDeploy,
            ExcludeGrants = settings.ExcludeGrants,
            ExcludeRoleMemberships = settings.ExcludeRoleMemberships,
            ExcludeOwners = settings.ExcludeOwners,
            SearchPath = settings.SearchPath,
            Tenants = settings.Tenants is null
                ? null
                : new TenantsConfiguration
                {
                    Template = settings.Tenants.Template,
                    Prefix = settings.Tenants.Prefix,
                    Query = settings.Tenants.Query,
                    File = settings.Tenants.File,
                    PreDeploy = settings.Tenants.PreDeploy,
                    PostDeploy = settings.Tenants.PostDeploy,
                },
        };
        return Build(source, configuration, projectDirectory, Path.Combine(projectDirectory, "pgproj.json"));
    }

    private static PgProject Build(IProjectFileSource source, PgProjectConfiguration configuration, string projectDirectory, string fullPath)
    {
        // connectionString was replaced by two directional properties (G-304a). An unknown property is otherwise ignored, so refuse this one instead of letting it do nothing.
        if (configuration.ConnectionString is not null)
        {
            throw new PgProjectValidationException(
                $"Project file '{fullPath}' has a 'connectionString' property, which is no longer read. Use 'targetConnectionString' for the database diff and publish compare and change, " +
                "and 'sourceConnectionString' for the database init scaffolded the project from.");
        }

        var name = RequireValue(configuration.Name, "name", fullPath);
        var version = RequireValue(configuration.Version, "version", fullPath);
        var sourcePath = ResolveDirectoryPath(source, projectDirectory, configuration.Source, fullPath);
        var preDeployScript = ResolveOptionalFilePath(source, projectDirectory, configuration.PreDeploy, "predeploy", fullPath);
        var postDeployScript = ResolveOptionalFilePath(source, projectDirectory, configuration.PostDeploy, "postdeploy", fullPath);
        var schemas = (configuration.Schemas ?? [])
            .Where(static schema => !string.IsNullOrWhiteSpace(schema))
            // A schema name is read as PostgreSQL reads one: folded to lower case unless double-quoted (G-206).
            .Select(static schema => PgProj.Parser.SqlIdentifiers.FoldName(schema))
            .Distinct(PgProj.Parser.SqlIdentifiers.NameComparer)
            .ToArray();
        var searchPath = ReadSearchPath(configuration.SearchPath, fullPath);
        var tenants = ReadTenants(source, configuration.Tenants, projectDirectory, fullPath, sourcePath, schemas, searchPath ?? [PgProject.DefaultSearchPath], preDeployScript, postDeployScript);
        // A tenant's scripts, like the project's, are not schema (G-290e).
        IEnumerable<string> tenantScripts = tenants is null ? [] : [.. tenants.PreDeploy?.Files.Select(static file => file.FilePath) ?? [], .. tenants.PostDeploy?.Files.Select(static file => file.FilePath) ?? []];
        var sqlFiles = FindSchemaFiles(source, sourcePath, preDeployScript, postDeployScript, tenants?.TemplatePath, tenantScripts);
        var preDeploy = ExpandOptionalScript(source, preDeployScript);
        var postDeploy = ExpandOptionalScript(source, postDeployScript);
        // The project's own scripts take one marker, run when=changed (G-290e).
        CheckRunWhen(preDeploy, [PgProject.RunWhenChanged], "the project's predeploy");
        CheckRunWhen(postDeploy, [PgProject.RunWhenChanged], "the project's postdeploy");

        return new PgProject(
            name,
            version,
            schemas,
            sourcePath,
            preDeployScript,
            postDeployScript,
            sqlFiles)
        {
            PreDeployScriptContent = NonBlank(preDeploy?.Text),
            PostDeployScriptContent = NonBlank(postDeploy?.Text),
            PreDeployScriptFiles = preDeploy?.Files ?? [],
            PostDeployScriptFiles = postDeploy?.Files ?? [],
            PreDeployScriptParts = preDeploy?.Root,
            PostDeployScriptParts = postDeploy?.Root,
            SourceConnectionString = NormalizeOptionalValue(configuration.SourceConnectionString),
            TargetConnectionString = NormalizeOptionalValue(configuration.TargetConnectionString),
            ExcludeGrants = configuration.ExcludeGrants ?? false,
            ExcludeRoleMemberships = configuration.ExcludeRoleMemberships ?? false,
            ExcludeOwners = configuration.ExcludeOwners ?? false,
            SearchPath = searchPath ?? [PgProject.DefaultSearchPath],
            HasConfiguredSearchPath = searchPath is not null,
            Tenants = tenants,
        };
    }

    public Workspace LoadWorkspace(PgProject project) => LoadWorkspace(project, DiskFileSource.Instance);

    /// <summary>Reads the project's schema files, and the tenant template's, from <paramref name="source"/>, each with the version the source gives it (G-309a).</summary>
    public Workspace LoadWorkspace(PgProject project, IProjectFileSource source)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);

        WorkspaceDocument Read(string filePath) => new() { FilePath = filePath, Content = source.ReadAllText(filePath), Version = source.Version(filePath) };

        var documents = project.SqlFiles.Select(Read).ToArray();

        return new Workspace
        {
            RootDirectory = project.SourcePath,
            Documents = documents,
            TemplateDocuments = (project.Tenants?.TemplateFiles ?? []).Select(Read).ToArray(),
        };
    }

    /// <summary>
    /// Validates <c>searchPath</c> (G-93): a non-empty list of schema names, each once. <c>"$user"</c> is refused: it names the role that
    /// deploys, which <c>build</c> cannot know. Null when the property is absent.
    /// </summary>
    private static IReadOnlyList<string>? ReadSearchPath(IReadOnlyList<string>? configured, string projectFilePath)
    {
        if (configured is null)
        {
            return null;
        }

        var schemas = new List<string>();
        foreach (var entry in configured)
        {
            var schema = entry?.Trim();
            if (string.IsNullOrEmpty(schema))
            {
                throw new PgProjectValidationException($"Project file '{projectFilePath}' has an empty entry in 'searchPath'.");
            }

            if (string.Equals(schema, "$user", StringComparison.OrdinalIgnoreCase))
            {
                throw new PgProjectValidationException($"Project file '{projectFilePath}' lists '$user' in 'searchPath'; pgproj cannot know the role a project is deployed as, so list the schema itself.");
            }

            // A schema name is read as PostgreSQL reads one in search_path: folded to lower case unless double-quoted (G-206).
            schema = PgProj.Parser.SqlIdentifiers.FoldName(schema);
            if (schemas.Contains(schema, StringComparer.Ordinal))
            {
                throw new PgProjectValidationException($"Project file '{projectFilePath}' lists '{schema}' twice in 'searchPath'.");
            }

            schemas.Add(schema);
        }

        if (schemas.Count == 0)
        {
            throw new PgProjectValidationException($"Project file '{projectFilePath}' has an empty 'searchPath'; leave it out to use public.");
        }

        return schemas;
    }

    /// <summary>
    /// Reads and checks <c>tenants</c> (G-290): <c>template</c>, a folder; <c>prefix</c>, the start of every tenant schema's name and the template's stand-in
    /// schema, read as a name in <c>pgproj.json</c> is (G-206); and at most one of <c>query</c> and <c>file</c> for the list of tenants. Null when absent.
    /// </summary>
    private static PgProjectTenants? ReadTenants(
        IProjectFileSource source,
        TenantsConfiguration? configured,
        string projectDirectory,
        string projectFilePath,
        string sourcePath,
        IReadOnlyList<string> schemas,
        IReadOnlyList<string> searchPath,
        string? preDeployScript,
        string? postDeployScript)
    {
        if (configured is null)
        {
            return null;
        }

        string Problem(string message) => $"Project file '{projectFilePath}': {message}";

        if (string.IsNullOrWhiteSpace(configured.Template))
        {
            throw new PgProjectValidationException(Problem("'tenants' is missing 'template', the folder of SQL every tenant's schema holds, written without a schema."));
        }

        var templatePath = Path.GetFullPath(Path.Combine(projectDirectory, configured.Template));
        if (!source.DirectoryExists(templatePath))
        {
            throw new PgProjectValidationException(Problem($"the tenant template folder '{templatePath}' does not exist."));
        }

        var comparison = source.IsCaseSensitive(templatePath) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var templateFolder = Path.TrimEndingDirectorySeparator(templatePath) + Path.DirectorySeparatorChar;
        var sourceFolder = Path.TrimEndingDirectorySeparator(sourcePath) + Path.DirectorySeparatorChar;
        if (sourceFolder.StartsWith(templateFolder, comparison))
        {
            throw new PgProjectValidationException(Problem($"the tenant template folder '{templatePath}' is, or holds, the source folder '{sourcePath}'. Make the template a folder of its own (it may be inside source)."));
        }

        if (string.IsNullOrWhiteSpace(configured.Prefix))
        {
            throw new PgProjectValidationException(Problem("'tenants' is missing 'prefix', the start of every tenant schema's name: with \"prefix\": \"tenant\", the tenant acme's schema is tenant_acme."));
        }

        var prefix = PgProj.Parser.SqlIdentifiers.FoldName(configured.Prefix.Trim());
        var prefixBytes = System.Text.Encoding.UTF8.GetByteCount(prefix);
        if (prefix.Length == 0 || prefix.Contains('\0', StringComparison.Ordinal))
        {
            throw new PgProjectValidationException(Problem($"the tenant prefix '{configured.Prefix}' is not a name PostgreSQL accepts."));
        }

        if (prefix.StartsWith("pg_", StringComparison.Ordinal))
        {
            throw new PgProjectValidationException(Problem($"the tenant prefix '{prefix}' starts with pg_, which PostgreSQL keeps for its own schemas."));
        }

        if (prefixBytes > PgProjectTenants.MaxSchemaNameBytes - 2)
        {
            throw new PgProjectValidationException(Problem($"the tenant prefix '{prefix}' is {prefixBytes} bytes long; with '_' and an identifier, a tenant's schema name must fit PostgreSQL's {PgProjectTenants.MaxSchemaNameBytes} bytes."));
        }

        string[] reserved = ["public", "pg_catalog", "information_schema"];
        foreach (var (list, property) in new[] { ((IReadOnlyList<string>)reserved, null), (schemas, "schemas"), (searchPath, "searchPath") })
        {
            foreach (var schema in list)
            {
                if (string.Equals(schema, prefix, StringComparison.Ordinal))
                {
                    throw new PgProjectValidationException(Problem(property is null
                        ? $"the tenant prefix cannot be '{prefix}': it is also the template's stand-in schema, and '{prefix}' is a schema PostgreSQL has already."
                        : $"'{property}' lists '{prefix}', the tenant prefix. It is the template's stand-in schema, not a real one; leave it out of '{property}'."));
                }

                if (property is not null && schema.StartsWith(prefix + "_", StringComparison.Ordinal))
                {
                    throw new PgProjectValidationException(Problem($"'{property}' lists '{schema}', which is named as a tenant's schema is (prefix '{prefix}'). A tenant's schema is found or created by pgproj; leave it out of '{property}'."));
                }
            }
        }

        var hasQuery = !string.IsNullOrWhiteSpace(configured.Query);
        var hasFile = !string.IsNullOrWhiteSpace(configured.File);
        if (hasQuery && hasFile)
        {
            throw new PgProjectValidationException(Problem("'tenants' has both 'query' and 'file'. Give one: the tenants come from a query against the database, or from a CSV file."));
        }

        // A tenant's own scripts (G-290e): run in each tenant's transaction, with their variables and markers.
        var tenantPreDeploy = ExpandOptionalScript(source, ResolveOptionalFilePath(source, projectDirectory, configured.PreDeploy, "tenants.predeploy", projectFilePath));
        var tenantPostDeploy = ExpandOptionalScript(source, ResolveOptionalFilePath(source, projectDirectory, configured.PostDeploy, "tenants.postdeploy", projectFilePath));
        CheckRunWhen(tenantPreDeploy, PgProjectTenants.TenantRunWhen, "a tenant's predeploy");
        CheckRunWhen(tenantPostDeploy, PgProjectTenants.TenantRunWhen, "a tenant's postdeploy");
        string[] tenantScriptFiles = [.. tenantPreDeploy?.Files.Select(static file => file.FilePath) ?? [], .. tenantPostDeploy?.Files.Select(static file => file.FilePath) ?? []];

        var templateFiles = source.EnumerateFiles(templatePath, "*.sql", recursive: true)
            .Where(file => !string.Equals(file, preDeployScript, comparison) && !string.Equals(file, postDeployScript, comparison))
            .Where(file => !tenantScriptFiles.Contains(file, StringComparer.FromComparison(comparison)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (hasFile)
        {
            var filePath = Path.GetFullPath(Path.Combine(projectDirectory, configured.File!));
            if (!source.FileExists(filePath))
            {
                throw new PgProjectValidationException(Problem($"the tenant file '{filePath}' does not exist."));
            }

            return new PgProjectTenants(templatePath, prefix, TenantSource.File)
            {
                TemplateFiles = templateFiles,
                PreDeploy = tenantPreDeploy,
                PostDeploy = tenantPostDeploy,
                FilePath = filePath,
                FileTenants = TenantFileReader.Read(filePath, prefix, source),
            };
        }

        return new PgProjectTenants(templatePath, prefix, hasQuery ? TenantSource.Query : TenantSource.Schemas)
        {
            TemplateFiles = templateFiles,
            PreDeploy = tenantPreDeploy,
            PostDeploy = tenantPostDeploy,
            Query = hasQuery ? configured.Query!.Trim() : null,
        };
    }

    private static string RequireValue(string? value, string propertyName, string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PgProjectValidationException($"Project file '{projectFilePath}' is missing required property '{propertyName}'.");
        }

        return value.Trim();
    }

    private static string ResolveDirectoryPath(IProjectFileSource source, string projectDirectory, string? configuredPath, string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new PgProjectValidationException($"Project file '{projectFilePath}' is missing required property 'source'.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, configuredPath));
        if (!source.DirectoryExists(fullPath))
        {
            throw new PgProjectValidationException($"The source directory '{fullPath}' configured in '{projectFilePath}' does not exist.");
        }

        return fullPath;
    }

    private static ExpandedScript? ExpandOptionalScript(IProjectFileSource source, string? filePath)
        => string.IsNullOrWhiteSpace(filePath) ? null : ScriptIncludeExpander.Expand(filePath, source);

    private static string? NonBlank(string? content)
        => string.IsNullOrWhiteSpace(content) ? null : content;

    /// <summary>The name of the folder under <c>source</c> that is never read as schema (G-124), in any case.</summary>
    public const string ScriptsFolderName = "Scripts";

    /// <summary>
    /// The <c>*.sql</c> files under <paramref name="sourcePath"/> that are read as schema (G-124): not the <c>Scripts</c> folder directly
    /// under it, which holds scripts that only a pre- or post-deployment script runs, and not the pre- and post-deployment scripts
    /// themselves, wherever they are.
    /// </summary>
    private static string[] FindSchemaFiles(IProjectFileSource source, string sourcePath, string? preDeployScript, string? postDeployScript, string? templatePath = null, IEnumerable<string>? otherScripts = null)
    {
        var others = (otherScripts ?? []).ToArray();
        // Two paths are one file when the project's file system says so (G-212), not by the operating system: macOS's default is case-insensitive.
        var comparison = source.IsCaseSensitive(sourcePath) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var sourceFolder = Path.TrimEndingDirectorySeparator(sourcePath) + Path.DirectorySeparatorChar;
        // A tenant template under source is read once per tenant, not as the shared project (G-290).
        var templateFolder = templatePath is null ? null : Path.TrimEndingDirectorySeparator(templatePath) + Path.DirectorySeparatorChar;

        // The Scripts folder is found from the files under it, so a source needs no way to list folders: a file is in it when the first folder
        // of its path under source is named Scripts (in any case).
        bool InScriptsFolder(string file)
        {
            if (!file.StartsWith(sourceFolder, comparison))
            {
                return false;
            }

            var relative = file[sourceFolder.Length..];
            var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            return separator > 0 && string.Equals(relative[..separator], ScriptsFolderName, StringComparison.OrdinalIgnoreCase);
        }

        return source.EnumerateFiles(sourcePath, "*.sql", recursive: true)
            .Where(file => !InScriptsFolder(file) && (templateFolder is null || !file.StartsWith(templateFolder, comparison)))
            .Where(file => !string.Equals(file, preDeployScript, comparison) && !string.Equals(file, postDeployScript, comparison))
            .Where(file => !others.Contains(file, StringComparer.FromComparison(comparison)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Each file's <c>run when=</c> marker (G-290e) must be one of <paramref name="allowed"/>; a file without one runs on every publish. The error names
    /// the file, its line, and what it takes.
    /// </summary>
    private static void CheckRunWhen(ExpandedScript? script, IReadOnlyList<string> allowed, string what)
    {
        foreach (var part in script?.Parts ?? [])
        {
            if (part.RunWhen is { } value && !allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                throw new PgProjectValidationException(
                    $"{part.FilePath}:{part.RunWhenLine}: '-- pgproj: run when={value}' is not a marker {what} script takes; it takes when={string.Join(" or when=", allowed)}, "
                    + "or no marker, to run on every publish.");
            }
        }
    }

    private static string? NormalizeOptionalValue(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static string? ResolveOptionalFilePath(IProjectFileSource source, string projectDirectory, string? configuredPath, string propertyName, string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, configuredPath));
        if (!source.FileExists(fullPath))
        {
            throw new PgProjectValidationException($"The file '{fullPath}' configured in '{propertyName}' for '{projectFilePath}' does not exist.");
        }

        return fullPath;
    }

    private static JsonSerializerOptions JsonSerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record class PgProjectConfiguration
    {
        public string? Name { get; init; }

        public string? Version { get; init; }

        public IReadOnlyList<string>? Schemas { get; init; }

        public string? Source { get; init; }

        /// <summary>The removed property (G-304a); read only to refuse it.</summary>
        public string? ConnectionString { get; init; }

        public string? SourceConnectionString { get; init; }

        public string? TargetConnectionString { get; init; }

        public string? PreDeploy { get; init; }

        public string? PostDeploy { get; init; }

        public bool? ExcludeGrants { get; init; }

        public bool? ExcludeRoleMemberships { get; init; }

        public bool? ExcludeOwners { get; init; }

        public IReadOnlyList<string>? SearchPath { get; init; }

        public TenantsConfiguration? Tenants { get; init; }
    }

    private sealed record class TenantsConfiguration
    {
        public string? Template { get; init; }

        public string? Prefix { get; init; }

        public string? Query { get; init; }

        public string? File { get; init; }

        public string? PreDeploy { get; init; }

        public string? PostDeploy { get; init; }
    }
}
