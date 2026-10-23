using PgProj.Core;

namespace PgProj.Engine;

/// <summary>
/// What <see cref="PgProjEngine.AboutAsync"/> describes (G-332): the project at <see cref="ProjectFilePath"/>, if there is one, and the target and source
/// databases, as the shell resolved their connection strings (an option, then an environment variable); a connection the shell leaves null is taken from the
/// project's <c>pgproj.json</c>, as <c>diff</c> and <c>document</c> take it.
/// </summary>
public sealed record AboutRequest
{
    /// <summary>Where the project's files are read from. The disk by default.</summary>
    public IProjectFileSource Files { get; init; } = DiskFileSource.Instance;

    /// <summary>The path of the project file in <see cref="Files"/>; null describes no project. A path where there is no file is reported, not an error.</summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>The target database's connection string as the shell resolved it, or null to take the project's <c>targetConnectionString</c>.</summary>
    public string? TargetConnectionString { get; init; }

    /// <summary>Where the shell found <see cref="TargetConnectionString"/>, in its own words (<c>--target-connection</c>, <c>PGPROJ_TARGET_CONNECTION</c>).</summary>
    public string? TargetConnectionOrigin { get; init; }

    /// <summary>The source database's connection string as the shell resolved it, or null to take the project's <c>sourceConnectionString</c>.</summary>
    public string? SourceConnectionString { get; init; }

    /// <summary>Where the shell found <see cref="SourceConnectionString"/>, in its own words (<c>--source-connection</c>, <c>PGPROJ_SOURCE_CONNECTION</c>).</summary>
    public string? SourceConnectionOrigin { get; init; }

    /// <summary>Whether to connect to each database to read its version and the role's privileges. When false, each is described from its connection string alone.</summary>
    public bool Connect { get; init; } = true;

    /// <summary>How long to wait for each database to answer before it is reported as not reachable. Five seconds by default.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
