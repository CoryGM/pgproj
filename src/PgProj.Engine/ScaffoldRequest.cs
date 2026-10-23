using PgProj.Core;

namespace PgProj.Engine;

/// <summary>One argument of the run as the shell was given it, for the report (<see cref="ScaffoldRequest.ReportArguments"/>); a secret in it is masked by the engine (G-318).</summary>
public sealed record InitArgument(string Name, string Value);

/// <summary>Where the shell wrote the diagram of a new project, and what it did about a file already there, so the report can link it and say so.</summary>
/// <param name="Path">The full path of the diagram (<c>.mmd</c>) file.</param>
/// <param name="Placement">What was done about a file already at the path, and why; null when that was not decided.</param>
public sealed record InitDiagramFile(string Path, ErdPlacement? Placement);

/// <summary>What <c>--erd</c> did about the files at its path, and why (for the <c>init</c> report).</summary>
/// <param name="Action">What was done: written, overwritten, backed up, or written under a new name.</param>
/// <param name="Reason">Why: nothing was there, the default for what was there, or the user's <c>--erd-existing</c>.</param>
public sealed record ErdPlacement(string Action, string Reason);

/// <summary>
/// What <see cref="PgProjEngine.ScaffoldAsync"/> scaffolds (G-309d): a live database read into a new project, one SQL file per object and a <c>pgproj.json</c>, with
/// the choices <c>pgproj init</c> offers. The engine returns the files' text and writes none; the shell writes them where the request says they go.
/// </summary>
public sealed record ScaffoldRequest
{
    /// <summary>The database to read, as the shell resolved its connection string. Required.</summary>
    public string SourceConnectionString { get; init; } = string.Empty;

    /// <summary>Where the new <c>pgproj.json</c> goes; a relative path is resolved against the current directory.</summary>
    public string ProjectFilePath { get; init; } = "pgproj.json";

    /// <summary>The folder the SQL files go to; a relative path is resolved against the folder of <see cref="ProjectFilePath"/>.</summary>
    public string OutputDirectory { get; init; } = "./sql";

    /// <summary>The project's name in <c>pgproj.json</c>; the database's name when null or blank.</summary>
    public string? Name { get; init; }

    /// <summary>Scaffold only these schemas (a name is folded unless quoted); null scaffolds every schema. Not with <see cref="ExcludeSchemas"/>.</summary>
    public IReadOnlyList<string>? IncludeSchemas { get; init; }

    /// <summary>Scaffold every schema except these; null leaves none out. Not with <see cref="IncludeSchemas"/>.</summary>
    public IReadOnlyList<string>? ExcludeSchemas { get; init; }

    /// <summary>Leave out grants (G-87), and say so in <c>pgproj.json</c>.</summary>
    public bool ExcludeGrants { get; init; }

    /// <inheritdoc cref="ExcludeGrants"/>
    public bool ExcludeRoleMemberships { get; init; }

    /// <inheritdoc cref="ExcludeGrants"/>
    public bool ExcludeOwners { get; init; }

    /// <summary>A database with a schema per tenant (G-290d): the schemas named <c>prefix_...</c> are tenants, and one of them becomes the template. Null for none.</summary>
    public string? TenantPrefix { get; init; }

    /// <summary>The tenant (its identifier) to scaffold the template from; the first by name when null.</summary>
    public string? TenantFrom { get; init; }

    /// <summary>Also make <c>tenants.csv</c>, listing the tenants found, and point <c>pgproj.json</c> at it. Not with <see cref="TenantQuery"/>.</summary>
    public bool TenantFile { get; init; }

    /// <summary>The query <c>pgproj.json</c> lists the tenants with from now on (not run here). Not with <see cref="TenantFile"/>.</summary>
    public string? TenantQuery { get; init; }

    /// <summary>Also write the Markdown report of the run (G-157).</summary>
    public bool Report { get; init; }

    /// <summary>The arguments the report lists, in order.</summary>
    public IReadOnlyList<InitArgument> ReportArguments { get; init; } = [];

    /// <summary>
    /// Draw the new project's tables (G-158), keeping the schemas this filter keeps; null draws nothing. A schema it names that is not scaffolded is
    /// <see cref="InitStatus.Refused"/>.
    /// </summary>
    public ErdFilter? Diagram { get; init; }

    /// <summary>The time the run started, which names the report; now when null.</summary>
    public DateTime? StartedUtc { get; init; }

    /// <summary>
    /// The files the new project is laid over when it is read back (to compare the tenants, and to draw it): the disk by default, so a file already in the
    /// output folder is read as it would be once the new files are written beside it. The engine only reads it.
    /// </summary>
    public IProjectFileSource Files { get; init; } = DiskFileSource.Instance;

    /// <summary>Told about the run as it goes (the database connected to, read, and grouped into files). Called on the thread that runs it, in order.</summary>
    public IProgress<EngineEvent>? Progress { get; init; }

    /// <summary>
    /// Called once everything is scaffolded, read back, and compared, before the report is made: with the result so far (<see cref="InitResult.ReportText"/> not
    /// set yet). The shell writes the files there, and the diagram from <see cref="InitResult.Project"/>, and returns where the diagram went (null when it wrote
    /// none), so the report can link it, as the CLI does.
    /// </summary>
    public Func<InitResult, CancellationToken, Task<InitDiagramFile?>>? AfterScaffold { get; init; }
}
