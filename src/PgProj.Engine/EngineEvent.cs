namespace PgProj.Engine;

/// <summary>What a run reports while it works (G-309a), to the <see cref="IProgress{T}"/> a request carries.</summary>
public enum EngineEventKind
{
    /// <summary>The project file (or settings) was read; <see cref="EngineEvent.Path"/> is its path.</summary>
    ProjectRead,

    /// <summary>A SQL file was read into the run's snapshot; <see cref="EngineEvent.Path"/> is its path.</summary>
    DocumentRead,

    /// <summary>The files were parsed and the model was built; <see cref="EngineEvent.ElapsedMilliseconds"/> is the time of the whole run so far.</summary>
    ModelBuilt,

    /// <summary>The source database (source mode, <c>init</c>, <c>document</c>) was connected to; <see cref="EngineEvent.Message"/> says so.</summary>
    SourceConnected,

    /// <summary>The source database was read and scaffolded: in memory (source mode, G-309b), or into the files of a new project (<c>init</c>, G-309d).</summary>
    SourceRead,

    /// <summary>The target database was connected to (G-309b).</summary>
    TargetConnected,

    /// <summary>The live database is about to be read; the message names the schemas (G-309b).</summary>
    LiveReadStarted,

    /// <summary>The live database was read and rendered (G-309b), or read to be documented (G-309d).</summary>
    LiveRead,

    /// <summary>The project was compared with the live database (G-309b).</summary>
    Compared,

    /// <summary>A step of a tenant comparison: the shared schemas, or one tenant (G-309b).</summary>
    TenantStep,

    /// <summary>With <c>--comments-only</c>, how many <c>COMMENT ON</c> statements a part deploys (G-309c); information.</summary>
    CommentsOnlyPlanned,

    /// <summary>Which database a publish reads from and which it changes (source mode, G-309c); information.</summary>
    Direction,

    /// <summary>One part of a tenants publish was planned (G-309c).</summary>
    PartPlanned,

    /// <summary>The tenants were read after the shared changes were published, because the tenant query reads what they create (G-309c); information.</summary>
    TenantsReadAfterShared,

    /// <summary>A part's transactional script ran and committed (G-309c); information.</summary>
    ScriptExecuted,

    /// <summary>The statements <c>--concurrently</c> leaves for after the commit are about to run (G-309c); information.</summary>
    AfterCommitStarted,

    /// <summary>One of those statements is about to run; the message is its number and what it does (G-309c); information.</summary>
    AfterCommitStep,

    /// <summary>All of them ran (G-309c); information.</summary>
    AfterCommitDone,

    /// <summary>A part of a tenants publish was published (G-309c); information.</summary>
    PartPublished,

    /// <summary>A tenants publish finished: the summary line (G-309c); information.</summary>
    PublishFinished,

    /// <summary>
    /// A publish's script is composed (G-316), with <see cref="EngineEvent.Script"/> as printed or written: reported where <c>pgproj publish</c> has always shown it,
    /// after the warnings about the changes and before those about the script, so a shell that shows it here keeps that order.
    /// </summary>
    ScriptPlanned,

    /// <summary>A warning of a publish, as it is found (<see cref="EngineEvent.Notice"/>): the plan's, and those found while it runs, such as a comment <c>--comments-only</c> skipped (G-309c, G-316); a warning.</summary>
    Notice,

    /// <summary>The documentation's dictionary and diagram were built (G-309d).</summary>
    DocumentationBuilt,
}

/// <summary>How a shell should show an event's message: as a line of progress detail, or as information the person running it always sees.</summary>
public enum EngineEventLevel
{
    /// <summary>Detail, for a log of what the run is doing.</summary>
    Detail,

    /// <summary>Information: what a command that publishes says as it goes.</summary>
    Information,

    /// <summary>A warning: something the person running it should know, found while it ran.</summary>
    Warning,
}

/// <summary>One thing a run did.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Path">The file it happened to, or null.</param>
/// <param name="ElapsedMilliseconds">The time since the run started, for events that measure it.</param>
/// <param name="Message">The engine's own plain-English line about it, for a log; null for events that carry only data.</param>
/// <param name="Level">Whether the message is detail, information, or a warning (G-309c).</param>
/// <param name="Notice">For a <see cref="EngineEventKind.Notice"/> of a publish, the warning itself, as the result carries it (G-316).</param>
/// <param name="Script">For <see cref="EngineEventKind.ScriptPlanned"/>, the script as printed or written (G-316).</param>
public sealed record EngineEvent(EngineEventKind Kind, string? Path = null, long ElapsedMilliseconds = 0, string? Message = null, EngineEventLevel Level = EngineEventLevel.Detail, PublishNotice? Notice = null, string? Script = null);
