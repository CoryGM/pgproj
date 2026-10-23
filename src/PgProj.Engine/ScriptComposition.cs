using PgProj.Core;
using PgProj.Dependencies;
using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>
/// Turns a comparison into the script <c>publish</c> deploys (G-309c; this was in <c>CliContext</c>): the dependency graph, the operations with the pre- and post-deploy
/// scripts, the script text, and the checks that read the operations (what is unsupported, what can discard data).
/// </summary>
internal static class ScriptComposition
{
    public static DependencyGraph BuildDependencyGraph(DatabaseModel model)
        => new DependencyAnalyzer().Analyze(model);

    public static DependencyGraph BuildDependencyGraph(DatabaseModel oldModel, DatabaseModel newModel)
        => new DependencyAnalyzer().Analyze(oldModel, newModel);

    public static ChangeSet Diff(DatabaseModel oldModel, DatabaseModel newModel)
        => new DiffEngine().Diff(oldModel, newModel);

    public static OperationBatch Generate(ChangeSet changes, DependencyGraph graph)
        => new ScriptGenerator().Generate(changes, graph);

    public static OperationBatch Generate(ChangeSet changes, DependencyGraph graph, PgProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new ScriptGenerator().Generate(changes, graph, project.PreDeployScriptContent, project.PostDeployScriptContent);
    }

    /// <summary>The batch with these pre- and post-deploy scripts (G-290e: the files that run for this part, a tenant's variables substituted).</summary>
    public static OperationBatch Generate(ChangeSet changes, DependencyGraph graph, string? preDeployScript, string? postDeployScript)
        => new ScriptGenerator().Generate(changes, graph, preDeployScript, postDeployScript);

    public static string GenerateSqlScript(OperationBatch batch)
        => new ScriptGenerator().GenerateSqlScript(batch);

    /// <summary>
    /// The script, preceded by <c>SET search_path TO ...</c> when the project sets <c>searchPath</c> and the script has something to run
    /// (G-93): a view, routine body, default, or type written without a schema is then resolved when it is deployed exactly as
    /// <c>build</c> resolved it, whatever the deploying role's own path is. Without <c>searchPath</c> the script is unchanged.
    /// </summary>
    public static string GenerateSqlScript(OperationBatch batch, PgProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var script = new ScriptGenerator().GenerateSqlScript(batch);
        return project.HasConfiguredSearchPath && SqlStatementSplitter.Split(script).Count > 0
            ? "SET search_path TO " + string.Join(", ", project.SearchPath.Select(SqlIdentifiers.QuoteName)) + ";" + "\n\n" + script
            : script;
    }

    /// <summary>
    /// The operations in <paramref name="batch"/> that only emit a TODO/unsupported placeholder rather than real SQL. A non-empty result means <c>publish --execute</c>
    /// must refuse to run the batch (G-28); the script remains available for inspection.
    /// </summary>
    public static IReadOnlyList<SqlOperation> GetUnsupportedOperations(OperationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch.GetUnsupportedOperations();
    }

    /// <summary>
    /// The operations in <paramref name="batch"/> that can discard data that existed before they ran (G-35). A non-empty result means <c>publish --execute</c>
    /// refuses to run the batch unless the caller allows it; the script remains available for inspection.
    /// </summary>
    public static IReadOnlyList<DestructiveOperation> GetDestructiveOperations(OperationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch.GetDestructiveOperations();
    }
}
