using PgProj.Core;
using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>
/// What <c>publish</c> runs for one part of the database (G-290): the whole of an ordinary project, or, for a project with tenants, the shared schemas or
/// one tenant's schema. Each is one transaction, followed by the statements <c>--concurrently</c> runs after it commits.
/// </summary>
/// <param name="Label">How messages name the part: null for an ordinary project, <c>shared schemas</c>, or the tenant's schema.</param>
/// <param name="Batch">Every operation, the checks' input (after <c>--comments-only</c>, when given).</param>
/// <param name="Transactional">The operations run in the transaction.</param>
/// <param name="TransactionalScript">The transaction's script, headed by the superuser comment and the <c>SET search_path</c> it needs.</param>
/// <param name="Script">The whole script as printed or written: the transaction's, then the <c>--concurrently</c> part after its marker line.</param>
internal sealed record PublishUnit(
    string? Label,
    DatabaseModel ProjectModel,
    DatabaseModel LiveModel,
    ChangeSet Changes,
    OperationBatch FullBatch,
    OperationBatch Batch,
    OperationBatch Transactional,
    IReadOnlyList<AfterCommitStep> AfterCommit,
    IReadOnlyList<SuperuserStatement> SuperuserStatements,
    string TransactionalScript,
    string Script)
{
    /// <summary>True when the part has something to run.</summary>
    public bool HasStatements => SqlStatementSplitter.Split(Script).Count > 0;

    /// <summary>
    /// Plans one part from its comparison. <paramref name="searchPath"/> is the path its statements need: null for the project's own (G-93, the script is
    /// headed by <c>SET search_path</c> only when <c>pgproj.json</c> sets one), or a tenant's schema then <c>searchPath</c> (G-290), always written.
    /// <paramref name="deployScripts"/> are the pre- and post-deploy scripts this part runs (G-290e: <see cref="DeployScripts"/>): the project's for an
    /// ordinary project or the shared part, a tenant's for a tenant.
    /// </summary>
    public static PublishUnit Build(
        PgProject project,
        string? label,
        DatabaseModel projectModel,
        DatabaseModel liveModel,
        ChangeSet changes,
        (string? Pre, string? Post) deployScripts,
        IReadOnlyList<string>? searchPath,
        bool commentsOnly,
        bool concurrently,
        int serverMajorVersion,
        IReadOnlySet<(string Schema, string Name)>? relationNames,
        IReadOnlyCollection<string> untrustedLanguages,
        IProgress<EngineEvent>? progress)
    {
        ArgumentNullException.ThrowIfNull(project);

        var dependencyGraph = ScriptComposition.BuildDependencyGraph(liveModel, projectModel);
        var fullBatch = ScriptComposition.Generate(changes, dependencyGraph, deployScripts.Pre, deployScripts.Post);
        var batch = fullBatch;
        if (commentsOnly)
        {
            // Only the COMMENT ON statements (G-169); the other operations and the pre/post-deploy scripts are left for a publish without it.
            batch = CommentStatementOperation.CommentsOnly(fullBatch);
            progress?.Report(new EngineEvent(
                EngineEventKind.CommentsOnlyPlanned,
                Message: $"--comments-only{(label is null ? string.Empty : $" ({label})")}: deploying {batch.Operations.Count} COMMENT ON statement(s); {fullBatch.Operations.Count - batch.Operations.Count} other operation(s) are not part of this script.",
                Level: EngineEventLevel.Information));
        }

        // Statements only a superuser can run (G-265); none with --comments-only, which deploys only comments.
        var superuserStatements = commentsOnly ? [] : global::PgProj.Engine.SuperuserStatements.Find(batch, untrustedLanguages);

        // With --concurrently (G-271), the slow part of new indexes and constraints on existing tables leaves the transaction; every check still covers all of it.
        var plan = concurrently ? ConcurrentPublishPlan.Create(batch, liveModel, serverMajorVersion, relationNames!) : null;
        var transactional = plan?.Transactional ?? batch;
        var afterCommit = plan?.AfterCommit ?? [];
        var body = searchPath is null
            ? ScriptComposition.GenerateSqlScript(transactional, project)
            : WithSearchPath(ScriptComposition.GenerateSqlScript(transactional), searchPath);
        var transactionalScript = global::PgProj.Engine.SuperuserStatements.ScriptHeader(superuserStatements) + body;
        var script = afterCommit.Count == 0
            ? transactionalScript
            : transactionalScript.TrimEnd() + Environment.NewLine + Environment.NewLine + plan!.AfterCommitScript() + Environment.NewLine;

        return new PublishUnit(label, projectModel, liveModel, changes, fullBatch, batch, transactional, afterCommit, superuserStatements, transactionalScript, script);
    }

    /// <summary>A tenant's script starts by putting its schema first on the path, so the template's unqualified names are deployed as <c>build</c> resolved them.</summary>
    private static string WithSearchPath(string script, IReadOnlyList<string> searchPath)
        => SqlStatementSplitter.Split(script).Count == 0
            ? script
            : "SET search_path TO " + string.Join(", ", searchPath.Select(PgProj.Parser.SqlIdentifiers.QuoteName)) + ";" + Environment.NewLine + Environment.NewLine + script;
}
