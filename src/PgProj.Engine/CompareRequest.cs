using PgProj.Core;

namespace PgProj.Engine;

/// <summary>
/// What <see cref="PgProjEngine.CompareAsync"/> compares (G-309b): a project, named as for <see cref="BuildRequest"/> (by its file or by its settings), with a
/// target database, and optionally a source database in place of the project's files (source mode, G-304b).
/// </summary>
public sealed record CompareRequest
{
    /// <summary>Where the project's files are read from. The disk by default.</summary>
    public IProjectFileSource Files { get; init; } = DiskFileSource.Instance;

    /// <summary>The path of the project file in <see cref="Files"/>. In source mode the project is optional: with neither this nor <see cref="Settings"/>, or a file that is not there, the source database alone defines the project.</summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>The project's settings, for a caller with no <c>pgproj.json</c>. Needs <see cref="RootDirectory"/>.</summary>
    public ProjectSettings? Settings { get; init; }

    /// <summary>The directory relative paths in <see cref="Settings"/> are resolved against.</summary>
    public string? RootDirectory { get; init; }

    /// <summary>Override <c>excludeGrants</c> for this run, in either direction; null uses the project's setting.</summary>
    public bool? ExcludeGrants { get; init; }

    /// <inheritdoc cref="ExcludeGrants"/>
    public bool? ExcludeRoleMemberships { get; init; }

    /// <inheritdoc cref="ExcludeGrants"/>
    public bool? ExcludeOwners { get; init; }

    /// <summary>
    /// The target's connection string as the shell resolved it (the CLI: its option, then <c>PGPROJ_TARGET_CONNECTION</c>). When null or blank, the project's
    /// <c>targetConnectionString</c> is used; when that is missing too the run is <see cref="CompareStatus.Refused"/>.
    /// </summary>
    public string? TargetConnectionString { get; init; }

    /// <summary>A live database as the desired state in place of the project's SQL files (G-304b); null compares the project's files. Not for a project with tenants.</summary>
    public string? SourceConnectionString { get; init; }

    /// <summary>Compare only these tenants, by identifier (G-290). Only for a project with tenants.</summary>
    public IReadOnlyList<string> Tenants { get; init; } = [];

    /// <summary>Told about the run as it goes (the databases connected to, the live read, each tenant). Called on the thread that runs it, in order.</summary>
    public IProgress<EngineEvent>? Progress { get; init; }

    /// <summary>
    /// Called once the project is read and built, before any database is read from or compared with: with the build part of the result, whatever it holds (errors
    /// too). A shell uses it to show the diagnostics, or write a diagram, ahead of the comparison, as the CLI does.
    /// </summary>
    public Func<BuildResult, CancellationToken, Task>? AfterBuild { get; init; }
}
