using PgProj.Core;

namespace PgProj.Engine;

/// <summary>
/// What <see cref="PgProjEngine.BuildAsync"/> builds (G-309a): a project, named either by its <c>pgproj.json</c> in a file source
/// (<see cref="ProjectFilePath"/>) or by its settings and a root directory (<see cref="Settings"/>, <see cref="RootDirectory"/>), not both; or a live database
/// in place of the project's files (<see cref="SourceConnectionString"/>, G-318).
/// </summary>
public sealed record BuildRequest
{
    /// <summary>Where the project's files are read from. The disk by default.</summary>
    public IProjectFileSource Files { get; init; } = DiskFileSource.Instance;

    /// <summary>
    /// The path of the project file in <see cref="Files"/>; relative paths are resolved against the current directory, as the CLI's <c>--project</c> is. With
    /// <see cref="SourceConnectionString"/> the project is optional: with neither this nor <see cref="Settings"/>, or a file that is not there, the database alone
    /// defines the project.
    /// </summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>
    /// A live database to build in place of the project's SQL files (G-318; the CLI's <c>build --source-connection</c>, G-304c): it is scaffolded in memory, as
    /// <c>init</c> would write it, in a read-only transaction, and built, so the model and the diagnostics describe the database and say whether what <c>init</c>
    /// writes builds. Its files are named <c>source:/...</c>. The project, when there is one, still supplies <c>schemas</c>, <c>searchPath</c>, and the
    /// <c>exclude*</c> settings; a project with tenants is <see cref="BuildStatus.Refused"/>. A build never connects to a target. Null builds the project's files.
    /// </summary>
    public string? SourceConnectionString { get; init; }

    /// <summary>The project's settings, for a caller with no <c>pgproj.json</c>; validated as the file is. Needs <see cref="RootDirectory"/>.</summary>
    public ProjectSettings? Settings { get; init; }

    /// <summary>The directory relative paths in <see cref="Settings"/> are resolved against.</summary>
    public string? RootDirectory { get; init; }

    /// <summary>
    /// Override <c>excludeGrants</c>, <c>excludeRoleMemberships</c>, or <c>excludeOwners</c> for this run, in either direction (the CLI's
    /// <c>--exclude-*</c> options, G-87, G-146); null uses the project's setting.
    /// </summary>
    public bool? ExcludeGrants { get; init; }

    /// <inheritdoc cref="ExcludeGrants"/>
    public bool? ExcludeRoleMemberships { get; init; }

    /// <inheritdoc cref="ExcludeGrants"/>
    public bool? ExcludeOwners { get; init; }

    /// <summary>Told about the run as it goes (each file read, the model built). Called on the thread that runs it.</summary>
    public IProgress<EngineEvent>? Progress { get; init; }

    /// <summary>
    /// The parses of the project's files kept from the last build (G-311): a file whose text is unchanged is not parsed again, and the cache is left holding
    /// this build's files. An editor keeps one per open project; null parses every file.
    /// </summary>
    public ParseCache? ParseCache { get; init; }
}
