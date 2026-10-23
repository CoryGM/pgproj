using Npgsql;
using PgProj.Core;
using PgProj.Parser;

namespace PgProj.Engine;

/// <summary>Which database, on which server, one end of a source-to-target run is.</summary>
public sealed record DatabaseEnd(string Database, string Host, int Port)
{
    /// <summary>The database and its server, as <c>name (host:port)</c>.</summary>
    public override string ToString() => $"{Database} ({Host}:{Port.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
}

/// <summary>
/// A live database as the desired state (G-304b): <c>diff</c> and <c>publish</c> with <c>--source-connection</c> compare and change the target
/// to match a source database instead of the project's SQL files. The source is read the way <c>init</c> reads a database, in a
/// <c>REPEATABLE READ</c>, <c>READ ONLY</c> transaction, and scaffolded into an in-memory project (G-309b: no temporary folder) that the ordinary path then loads, so every comparison
/// PostgreSQL renders, <c>searchPath</c>, and "deployed as written" apply unchanged. The project carries the real project's settings (<c>schemas</c>, <c>searchPath</c>,
/// the <c>exclude*</c> flags, <c>predeploy</c> and <c>postdeploy</c>, which run against the target); the real project's SQL files are not read.
/// </summary>
internal sealed class SourceProject
{
    /// <summary>The root the scaffold lives under in memory: a path that exists on no disk. A shell shows it as <c>source:/</c> (<see cref="Display"/>).</summary>
    public static readonly string VirtualRoot = Path.GetFullPath(Path.DirectorySeparatorChar + "pgproj-source-5f0c3a1e9d2b4c7f8a6e1d0b3c2a4f59");

    public const string TargetRequired = "A PostgreSQL target connection string is required. Specify --target-connection, set PGPROJ_TARGET_CONNECTION, or add targetConnectionString to pgproj.json.";

    private const string DisplayRoot = "source:/";

    private SourceProject(IProjectFileSource files, ProjectSettings settings, DatabaseEnd source, string? sourceSystemId, string? targetConnectionString, string originalDirectory, int fileCount)
    {
        Files = files;
        Settings = settings;
        Source = source;
        SourceSystemId = sourceSystemId;
        TargetConnectionString = targetConnectionString;
        OriginalDirectory = originalDirectory;
        FileCount = fileCount;
    }

    /// <summary>The scaffold in memory, over the real project's files (its deployment scripts are read from there).</summary>
    public IProjectFileSource Files { get; }

    /// <summary>The settings the run loads in place of the project's <c>pgproj.json</c>, resolved against <see cref="VirtualRoot"/>.</summary>
    public ProjectSettings Settings { get; }

    public DatabaseEnd Source { get; }

    /// <summary>The source cluster's <c>system_identifier</c>, or null when the role cannot read it.</summary>
    public string? SourceSystemId { get; }

    public string? TargetConnectionString { get; }

    /// <summary>The directory of the project the command was given (or the current directory): where a relative <c>--erd</c> path is resolved.</summary>
    public string OriginalDirectory { get; }

    public int FileCount { get; }

    /// <summary><paramref name="path"/> as a shell shows it: a path in the in-memory scaffold as <c>source:/...</c>, any other path as it is.</summary>
    public static string Display(string path)
    {
        if (path.StartsWith(VirtualRoot, StringComparison.OrdinalIgnoreCase)
            && (path.Length == VirtualRoot.Length || path[VirtualRoot.Length] is '\\' or '/'))
        {
            return DisplayRoot + path[VirtualRoot.Length..].TrimStart('\\', '/').Replace('\\', '/');
        }

        return path;
    }

    /// <summary>True for a path <see cref="Display"/> already wrote.</summary>
    public static bool IsDisplayPath(string path) => path.StartsWith(DisplayRoot, StringComparison.Ordinal);

    public static async Task<SourceProject> CreateAsync(
        IProjectFileSource originalFiles,
        string? projectPath,
        ProjectSettings? originalSettings,
        string? originalRoot,
        string sourceConnectionString,
        string? targetOption,
        bool withTarget,
        bool? excludeGrantsOverride,
        bool? excludeRoleMembershipsOverride,
        bool? excludeOwnersOverride,
        LiveComparison live,
        IProgress<EngineEvent>? progress,
        CancellationToken cancellationToken)
    {
        var fullProjectPath = projectPath is null ? null : Path.GetFullPath(projectPath);
        var originalDirectory = (originalRoot ?? (fullProjectPath is null ? null : Path.GetDirectoryName(fullProjectPath))) ?? Environment.CurrentDirectory;
        // The project file is optional in this mode: both connections and the settings below can all be given without one.
        var loader = new PgProjectLoader();
        var original = originalSettings is not null
            ? loader.Load(originalFiles, originalSettings, originalRoot!)
            : fullProjectPath is not null && originalFiles.FileExists(fullProjectPath) ? loader.Load(originalFiles, fullProjectPath) : null;
        if (original?.Tenants is not null)
        {
            throw new RefusedException("--source-connection cannot be used with a project that has tenants ('tenants' in pgproj.json): a source database has no template. Compare tenants with the project's template instead.");
        }

        // build never connects to a target (G-304c); diff and publish have one, which is not the source.
        var targetConnectionString = withTarget ? ResolveTarget(targetOption, original?.TargetConnectionString) : null;
        if (targetConnectionString is not null)
        {
            EnsureDifferentByName(sourceConnectionString, targetConnectionString);
        }

        var excludeGrants = excludeGrantsOverride ?? original?.ExcludeGrants ?? false;
        var excludeRoleMemberships = excludeRoleMembershipsOverride ?? original?.ExcludeRoleMemberships ?? false;
        var excludeOwners = excludeOwnersOverride ?? original?.ExcludeOwners ?? false;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using var connection = await LiveComparison.OpenConnectionAsync(sourceConnectionString, cancellationToken);
        progress?.Report(new EngineEvent(EngineEventKind.SourceConnected, null, stopwatch.ElapsedMilliseconds, $"Connected to the source, PostgreSQL {connection.PostgreSqlVersion} (database \"{connection.Database}\"), in {stopwatch.ElapsedMilliseconds} ms."));
        var systemId = await ReadSystemIdentifierAsync(connection, cancellationToken);
        var source = new DatabaseEnd(connection.Database ?? "database", connection.Host ?? string.Empty, connection.Port);

        // One consistent, read-only snapshot of the source: nothing it does can change the database, and the files describe one moment.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        stopwatch.Restart();
        var includeSchemas = original is { Schemas.Count: > 0 } ? original.Schemas.Select(SqlIdentifiers.QuoteName).ToArray() : null;
        var scaffold = await live.Loader.ScaffoldAsync(
            connection,
            cancellationToken,
            new ScaffoldOptions(includeSchemas, null, excludeGrants, excludeRoleMemberships, excludeOwners, CaseSensitivePaths: false));
        progress?.Report(new EngineEvent(EngineEventKind.SourceRead, null, stopwatch.ElapsedMilliseconds, $"Read the source and grouped it into {scaffold.Files.Count} file(s) in {stopwatch.ElapsedMilliseconds} ms."));

        var files = new OverlayFileSource(originalFiles, caseSensitive: false);
        files.AddDirectory(VirtualRoot);
        foreach (var file in scaffold.Files)
        {
            files.Set(Path.Combine(VirtualRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)), file.Content);
        }

        await transaction.RollbackAsync(cancellationToken);

        // The project's own path wins; otherwise the schemas the source's functions and types need, as init writes them.
        IReadOnlyList<string>? searchPath = original is { HasConfiguredSearchPath: true } ? original.SearchPath : scaffold.SearchPath is { Count: > 0 } ? [.. scaffold.SearchPath, "public"] : null;
        var settings = new ProjectSettings
        {
            Name = original?.Name ?? source.Database,
            Version = original?.Version ?? "1.0",
            Source = ".",
            TargetConnectionString = targetConnectionString,
            Schemas = original is { Schemas.Count: > 0 } ? original.Schemas : null,
            SearchPath = searchPath,

            // The project's scripts run against the target; they are named by their full paths because the in-memory project is elsewhere.
            PreDeploy = string.IsNullOrWhiteSpace(original?.PreDeployScript) ? null : original.PreDeployScript,
            PostDeploy = string.IsNullOrWhiteSpace(original?.PostDeployScript) ? null : original.PostDeployScript,
            ExcludeGrants = excludeGrants,
            ExcludeRoleMemberships = excludeRoleMemberships,
            ExcludeOwners = excludeOwners,
        };
        return new SourceProject(files, settings, source, systemId, targetConnectionString, originalDirectory, scaffold.Files.Count);
    }

    /// <summary>The target's connection string: the one the shell resolved (its option, then its environment variable), then the project's.</summary>
    public static string ResolveTarget(string? shellValue, string? projectValue)
        => !string.IsNullOrWhiteSpace(shellValue) ? shellValue
            : !string.IsNullOrWhiteSpace(projectValue) ? projectValue
            : throw new RefusedException(TargetRequired);

    /// <summary>
    /// Refuses a target that is the source (G-304b): a run that makes a database equal to itself is always a mistake, and the wrong-direction one is the risk of this mode.
    /// Called once the target is open, so it can also compare what the server says about itself.
    /// </summary>
    public async Task EnsureNotTheTargetAsync(NpgsqlConnection target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (SourceSystemId is not null
            && string.Equals(target.Database, Source.Database, StringComparison.Ordinal)
            && string.Equals(await ReadSystemIdentifierAsync(target, cancellationToken), SourceSystemId, StringComparison.Ordinal))
        {
            throw new RefusedException($"The source and the target are the same database ({Source.Database} on the same server, reached by different names). Name two different databases.");
        }
    }

    public static DatabaseEnd EndOf(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new DatabaseEnd(connection.Database ?? "database", connection.Host ?? string.Empty, connection.Port);
    }

    /// <summary>What the first output line says: which way the change goes.</summary>
    public static string Direction(DatabaseEnd source, DatabaseEnd target) => $"Source {source} -> Target {target}";

    /// <summary>Same host, port, and database in two connection strings (a host compared without regard to case, the port 5432 when absent).</summary>
    internal static void EnsureDifferentByName(string sourceConnectionString, string targetConnectionString)
    {
        NpgsqlConnectionStringBuilder source;
        NpgsqlConnectionStringBuilder target;
        try
        {
            source = new NpgsqlConnectionStringBuilder(sourceConnectionString);
            target = new NpgsqlConnectionStringBuilder(targetConnectionString);
        }
        catch (ArgumentException)
        {
            // A malformed string is described by the check that runs before every connection.
            return;
        }

        if (string.Equals(source.Host, target.Host, StringComparison.OrdinalIgnoreCase)
            && source.Port == target.Port
            && string.Equals(source.Database, target.Database, StringComparison.Ordinal))
        {
            throw new RefusedException($"The source and the target are the same database ({source.Database} on {source.Host}:{source.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}). Name two different databases.");
        }
    }

    private static async Task<string?> ReadSystemIdentifierAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        // pg_control_system() needs a superuser (or a grant); a role without it just cannot make this check.
        try
        {
            await using var command = new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()", connection);
            return (string?)await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (PostgresException)
        {
            return null;
        }
    }
}
