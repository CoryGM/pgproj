using PgProj.Diff;

namespace PgProj.Engine;

/// <summary>How a publish ended (G-309c).</summary>
public enum PublishStatus
{
    /// <summary><see cref="PublishMode.Plan"/>: the script is planned and nothing was run. <see cref="PublishResult.Warnings"/> says what to look at.</summary>
    Planned,

    /// <summary><see cref="PublishMode.Execute"/>: the script was run and committed (and, with <c>--concurrently</c>, the statements after it).</summary>
    Published,

    /// <summary>The project was read and its model has errors, so nothing was planned: <see cref="CompareResult.Diagnostics"/> of <see cref="PublishResult.Compare"/> says what.</summary>
    Failed,

    /// <summary>There is no project file at <see cref="CompareResult.ProjectFilePath"/> of <see cref="PublishResult.Compare"/>.</summary>
    ProjectNotFound,

    /// <summary>The project file or settings are not valid: <see cref="PublishResult.Message"/> says why.</summary>
    ProjectInvalid,

    /// <summary>
    /// The publish cannot be run as asked: the request is inconsistent, or <see cref="PublishMode.Execute"/> was refused by a check (<see cref="PublishResult.Refusals"/>
    /// names each), and nothing was run. <see cref="PublishResult.Message"/> says why when no refusal does.
    /// </summary>
    Refused,

    /// <summary>A database could not be connected to: <see cref="PublishResult.Message"/> is what the driver said.</summary>
    ConnectionFailed,

    /// <summary>
    /// <see cref="PublishMode.Execute"/> began and a statement failed: <see cref="PublishResult.Execution"/> says what ran and what did not. What was committed stays
    /// committed; the failing transaction was rolled back.
    /// </summary>
    ExecutionFailed,
}

/// <summary>What a warning or a refusal is about (G-309c).</summary>
public enum PublishNoticeKind
{
    /// <summary>A change rewrites, scans, or blocks writes to a table that already exists, for as long as it takes (G-270). A warning.</summary>
    LockRewrite,

    /// <summary>The script is estimated to need more locks than the server's lock table holds (G-280): a warning from 80% of it, a refusal over it.</summary>
    LockLimit,

    /// <summary>The script has statements only a superuser can run (G-265): a warning, and a refusal without consent or without the role.</summary>
    Superuser,

    /// <summary>Operations could only emit a placeholder (G-28). A refusal; its items that are blocked changes are <see cref="BlockedChange"/>.</summary>
    Unsupported,

    /// <summary>
    /// A change to an object something still uses that PostgreSQL will not let pgproj carry out: a collation, a text search dictionary's template, a configuration's
    /// parser (G-148). Part of the <see cref="Unsupported"/> refusal, whose text names both; the refusal itself is this kind when every item is one (G-316).
    /// </summary>
    BlockedChange,

    /// <summary>Operations can discard data or access (G-35). A refusal without <see cref="PublishRequest.AllowDestructive"/>.</summary>
    Destructive,

    /// <summary>The project uses features the server's version lacks (G-121): a warning for a planned script, a refusal for <see cref="PublishMode.Execute"/>.</summary>
    NewerThanServer,

    /// <summary>The project's grants and owners name roles the server lacks (G-147): a warning, or a refusal.</summary>
    MissingRole,

    /// <summary>The project's tables name tablespaces the server lacks (G-151): a warning, or a refusal.</summary>
    MissingTablespace,

    /// <summary>A change drops, renames, or retypes something a PL/pgSQL body names (G-303b). A warning.</summary>
    PlpgsqlBody,

    /// <summary>A schema the database has that the project no longer uses is left in place (G-166). A warning.</summary>
    UndroppedSchema,

    /// <summary>A script that is printed or written is not wrapped in a transaction. A warning.</summary>
    NotTransactional,

    /// <summary>Schemas named as tenants' that the tenant list no longer includes are left as they are (G-290). A warning.</summary>
    FormerTenant,

    /// <summary>The tenant query could not run, so the tenants were not read (G-290). A warning.</summary>
    TenantsNotRead,

    /// <summary>The server sent a notice while the script ran, such as a comment <c>--comments-only</c> skipped (G-169). A warning, found by running.</summary>
    ServerNotice,
}

/// <summary>One thing a warning or refusal names (G-316): an object of the model, a role, a tablespace, a feature, or a statement, so a shell can go to it.</summary>
/// <param name="Kind">What the item is about: the notice's kind, or, in an <see cref="PublishNoticeKind.Unsupported"/> refusal, <see cref="PublishNoticeKind.BlockedChange"/> for a blocked change.</param>
/// <param name="Text">The item as <c>pgproj publish</c> lists it.</param>
/// <param name="NodeKind">The kind of the object, when it is an object of the model.</param>
/// <param name="NodeId">The object's key in the model (<c>schema.name</c>, ...), or the role's, tablespace's, or schema's name; null for a feature.</param>
/// <param name="Detail">What is said about it: why it is destructive, the lock it takes, what names it, the version it needs.</param>
/// <param name="Part">The part of a tenants publish it is in (<c>shared schemas</c> or a tenant's schema); null for the whole publish.</param>
public sealed record PublishItem(PublishNoticeKind Kind, string Text, ChangeNodeKind? NodeKind = null, string? NodeId = null, string? Detail = null, string? Part = null);

/// <summary>
/// One warning or refusal of a publish (G-309c), with the items it names and the text <c>pgproj publish</c> prints for it.
/// </summary>
/// <param name="Kind">What it is about.</param>
/// <param name="Text">The text as <c>pgproj publish</c> prints it (possibly several lines).</param>
/// <param name="Items">What it names; empty when it names nothing.</param>
/// <param name="Part">The part of a tenants publish it is for (<c>shared schemas</c> or a tenant's schema); null for the whole publish.</param>
/// <param name="ForKeptScript">
/// True when it is about the script as printed or written (G-316: the script is not wrapped in a transaction), so a shell shows it only when it keeps the script; a
/// plan always does.
/// </param>
public sealed record PublishNotice(PublishNoticeKind Kind, string Text, IReadOnlyList<PublishItem> Items, string? Part = null, bool ForKeptScript = false);

/// <summary>A statement that runs after the transaction commits, with <c>--concurrently</c> (G-271).</summary>
public sealed record PublishStep(string Description, string Sql);

/// <summary>
/// One transaction of a publish (G-309c): the whole of an ordinary project, or, for a project with tenants, the shared schemas or one tenant (G-290c).
/// </summary>
public sealed record PublishPart
{
    /// <summary>How messages name the part: null for an ordinary project, <c>shared schemas</c>, or a tenant's schema.</summary>
    public string? Label { get; init; }

    /// <summary>What differs in this part.</summary>
    public ChangeSet Changes { get; init; } = new() { Added = [], Removed = [], Modified = [] };

    /// <summary>The script the transaction runs.</summary>
    public string TransactionalScript { get; init; } = string.Empty;

    /// <summary>The statements <c>--concurrently</c> runs after the commit, one at a time.</summary>
    public IReadOnlyList<PublishStep> AfterCommit { get; init; } = [];

    /// <summary>The whole script as printed or written: the transaction's, then the <c>--concurrently</c> part after its marker line.</summary>
    public string Script { get; init; } = string.Empty;

    /// <summary>The statements in it only a superuser can run, one line each.</summary>
    public IReadOnlyList<string> SuperuserStatements { get; init; } = [];

    /// <summary>True when the part has something to run.</summary>
    public bool HasStatements { get; init; }
}

/// <summary>A statement that failed during <see cref="PublishMode.Execute"/> (G-309c).</summary>
/// <param name="Message">What <c>pgproj publish</c> prints: PostgreSQL's <c>SQLSTATE: message</c>, then its detail, its hint, the statement, and what became of the transaction.</param>
/// <param name="SqlState">PostgreSQL's SQLSTATE, or null when the failure was not the server's.</param>
/// <param name="Detail">The server's detail, when it gave one and the connection string does not redact it.</param>
/// <param name="Hint">The server's hint.</param>
/// <param name="Statement">The statement that failed, as one short line with any password masked.</param>
/// <param name="AfterCommit">True when it was one of the <c>--concurrently</c> statements after the commit, false when it was in the transaction.</param>
/// <param name="NotDone">For an after-commit failure, the steps that did not run, the failing one first.</param>
public sealed record PublishFailure(
    string Message,
    string? SqlState,
    string? Detail,
    string? Hint,
    string? Statement,
    bool AfterCommit,
    IReadOnlyList<string> NotDone);

/// <summary>What <see cref="PublishMode.Execute"/> did (G-309c).</summary>
public sealed record PublishExecution
{
    /// <summary>For a project with tenants, the parts published, in order, as messages name them (<c>the shared schemas</c> or a tenant's schema).</summary>
    public IReadOnlyList<string> Published { get; init; } = [];

    /// <summary>The part that failed, named as <see cref="Published"/> names them; null for an ordinary project or when nothing failed.</summary>
    public string? FailedPart { get; init; }

    /// <summary>The failed statement; null when nothing failed.</summary>
    public PublishFailure? Failure { get; init; }

    /// <summary>For a project with tenants, the parts after the failed one, which were not started.</summary>
    public IReadOnlyList<string> NotStarted { get; init; } = [];

    /// <summary>What <c>pgproj publish</c> says about a failed run, a line each: the failure, and for a project with tenants what was published and what was not (G-316).</summary>
    public IReadOnlyList<string> FailureReport { get; init; } = [];
}

/// <summary>
/// The result of <see cref="PgProjEngine.PublishAsync"/> (G-309c): the build and compare part, the plan (one <see cref="PublishPart"/> per transaction), the
/// warnings and refusals, the script, and, after <see cref="PublishMode.Execute"/>, what ran. A problem the user caused is a <see cref="Status"/>, never an exception.
/// </summary>
public sealed record PublishResult
{
    /// <summary>The version of the result's shape; it changes only when a member is removed or changes meaning.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The version of this result's shape, <see cref="CurrentSchemaVersion"/>; a reader checks it before relying on the members.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>How the publish ended.</summary>
    public PublishStatus Status { get; init; }

    /// <summary>Why the publish did not complete (not valid, refused, could not connect, a statement failed); null otherwise.</summary>
    public string? Message { get; init; }

    /// <summary>The build part and what the project differs from the database in (for a project with tenants, in the shared schemas and in each tenant).</summary>
    public CompareResult Compare { get; init; } = new();

    /// <summary>The transactions, in the order they run: one for an ordinary project; the shared schemas and then each tenant otherwise.</summary>
    public IReadOnlyList<PublishPart> Parts { get; init; } = [];

    /// <summary>
    /// What to look at before the script is run, in the order <c>pgproj publish</c> prints it; each is also reported, as it is found, as an
    /// <see cref="EngineEventKind.Notice"/> event carrying it. After a run, the notices found while it ran are at the end.
    /// </summary>
    public IReadOnlyList<PublishNotice> Warnings { get; init; } = [];

    /// <summary>
    /// What stopped <see cref="PublishMode.Execute"/> before anything ran (for a project with tenants whose tenants were read after the shared part, before any tenant
    /// ran), as <c>pgproj publish</c> prints it; empty otherwise.
    /// </summary>
    public IReadOnlyList<PublishNotice> Refusals { get; init; } = [];

    /// <summary>
    /// The whole script as printed or written: for a project with tenants, the shared part and each tenant's under a line naming it; with a lock timeout, starting
    /// with <c>SET lock_timeout</c>. Empty when there is nothing to run.
    /// </summary>
    public string Script { get; init; } = string.Empty;

    /// <summary>What <see cref="PublishMode.Execute"/> did; null for a plan.</summary>
    public PublishExecution? Execution { get; init; }
}
