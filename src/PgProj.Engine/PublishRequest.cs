using PgProj.Core;

namespace PgProj.Engine;

/// <summary>What <see cref="PgProjEngine.PublishAsync"/> does with the script it plans (G-309c).</summary>
public enum PublishMode
{
    /// <summary>Plan only: the script, the warnings, and nothing run. What <c>--dry-run</c>, <c>--output</c>, and a <c>publish</c> without <c>--execute</c> do.</summary>
    Plan,

    /// <summary>
    /// Plan on this run's own connection, check, and run what was planned, in transactions (decision 1 of G-309, #213): there is no call that runs an earlier plan, so
    /// a script is never run against a database that changed after it was shown.
    /// </summary>
    Execute,
}

/// <summary>
/// What <see cref="PgProjEngine.PublishAsync"/> publishes (G-309c): the project and databases as for <see cref="CompareRequest"/>, and the consents and settings
/// <c>pgproj publish</c> takes.
/// </summary>
public sealed record PublishRequest
{
    /// <summary>Where the project's files are read from. The disk by default.</summary>
    public IProjectFileSource Files { get; init; } = DiskFileSource.Instance;

    /// <summary>The path of the project file in <see cref="Files"/>. In source mode the project is optional (see <see cref="CompareRequest.ProjectFilePath"/>).</summary>
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

    /// <summary>The target's connection string as the shell resolved it; when null or blank, the project's <c>targetConnectionString</c> is used.</summary>
    public string? TargetConnectionString { get; init; }

    /// <summary>A live database as the desired state in place of the project's SQL files (G-304b); null publishes the project's files. Not for a project with tenants.</summary>
    public string? SourceConnectionString { get; init; }

    /// <summary>Publish only these tenants, by identifier (G-290). Only for a project with tenants.</summary>
    public IReadOnlyList<string> Tenants { get; init; } = [];

    /// <summary>Plan the script, or plan and run it.</summary>
    public PublishMode Mode { get; init; } = PublishMode.Plan;

    /// <summary>Let <see cref="PublishMode.Execute"/> run a script that can discard data or access (<c>--allow-destructive</c>, G-35).</summary>
    public bool AllowDestructive { get; init; }

    /// <summary>Consent to run statements only a superuser can run (<c>--allow-superuser-statements</c>, G-265).</summary>
    public bool AllowSuperuserStatements { get; init; }

    /// <summary>Let <see cref="PublishMode.Execute"/> run a script estimated to need more locks than the server's lock table holds (<c>--allow-lock-limit</c>, G-280).</summary>
    public bool AllowLockLimit { get; init; }

    /// <summary>How long each statement may wait for a lock: <c>5s</c>, <c>500ms</c>, <c>2min</c> (<c>--lock-timeout</c>, G-270). Null waits as long as the server allows.</summary>
    public string? LockTimeout { get; init; }

    /// <summary>Build new indexes and check new constraints after the commit, a statement at a time (<c>--concurrently</c>, G-271). Not with <see cref="CommentsOnly"/>.</summary>
    public bool Concurrently { get; init; }

    /// <summary>Deploy only the <c>COMMENT ON</c> statements (<c>--comments-only</c>, G-169).</summary>
    public bool CommentsOnly { get; init; }

    /// <summary>Told about the run as it goes, on the thread that runs it, in order.</summary>
    public IProgress<EngineEvent>? Progress { get; init; }

    /// <summary>
    /// Called once the project is read and built, before any database is read from or compared with (see <see cref="CompareRequest.AfterBuild"/>).
    /// </summary>
    public Func<BuildResult, CancellationToken, Task>? AfterBuild { get; init; }

    /// <summary>
    /// Called once, when the script is planned and checked and before anything is run: with the result as it stands (<see cref="PublishStatus.Planned"/>, its parts,
    /// script, warnings, and refusals), for a shell that shows the plan before it runs. The warnings and the script were reported as events already
    /// (<see cref="EngineEventKind.Notice"/>, <see cref="EngineEventKind.ScriptPlanned"/>).
    /// </summary>
    public Func<PublishResult, CancellationToken, Task>? AfterPlan { get; init; }
}
