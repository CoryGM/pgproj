using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-71: a generated column was deployed as an ordinary column, so its values were never computed. It is now created with
/// <c>GENERATED ALWAYS AS (expression) STORED</c>; a changed expression is re-added (its values are derived); a generated column that
/// becomes ordinary keeps its values (<c>DROP EXPRESSION</c>); and an ordinary column that becomes generated, which cannot be converted
/// in place without losing what it holds, is left as a placeholder that blocks <c>publish --execute</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class GeneratedColumnDeployTests
{
    [TestMethod]
    public void ANewTableIsCreatedWithItsGeneratedColumn()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE TABLE public.t (a integer, total integer GENERATED ALWAYS AS (a * 2) STORED NOT NULL);"));

        StringAssert.Contains(statements[0], "\"total\" integer NOT NULL GENERATED ALWAYS AS (a * 2) STORED");
        Assert.IsFalse(statements.Any(static statement => statement.Contains("TODO", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void TheExpressionIsWrittenAsTheAuthorWroteIt()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build("CREATE TABLE public.t (a integer, n text, g text GENERATED ALWAYS AS (case when a > 3 then n || ')' else 'small' end) STORED);"));

        StringAssert.Contains(statements[0], "GENERATED ALWAYS AS (case when a > 3 then n || ')' else 'small' end) STORED");
    }

    [TestMethod]
    public void AGeneratedColumnAddedToAnExistingTableIsAddedWithItsExpression()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer);"),
            Build("CREATE TABLE public.t (a integer, g integer GENERATED ALWAYS AS (a * 2) STORED);"));

        Assert.HasCount(1, statements, string.Join("\n", statements));
        Assert.AreEqual("ALTER TABLE \"public\".\"t\" ADD COLUMN \"g\" integer GENERATED ALWAYS AS (a * 2) STORED;", statements[0]);
    }

    [TestMethod]
    public void AChangedExpressionDropsAndAddsTheColumnAndKeepsItsComment()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer, g integer GENERATED ALWAYS AS (a * 2) STORED); COMMENT ON COLUMN public.t.g IS 'doubled';"),
            Build("CREATE TABLE public.t (a integer, g integer GENERATED ALWAYS AS (a * 3) STORED); COMMENT ON COLUMN public.t.g IS 'doubled';"));

        var script = string.Join("\n", statements);
        Assert.AreEqual(
            "ALTER TABLE \"public\".\"t\" DROP COLUMN \"g\";\nALTER TABLE \"public\".\"t\" ADD COLUMN \"g\" integer GENERATED ALWAYS AS (a * 3) STORED;\nCOMMENT ON COLUMN \"public\".\"t\".\"g\" IS 'doubled';",
            script);
    }

    [TestMethod]
    public void ARewrittenButEquivalentExpressionDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build("CREATE TABLE public.t (a integer, b integer, g integer GENERATED ALWAYS AS (((a * 2) + b)) STORED);"),
            Build("CREATE TABLE public.t (a integer, b integer, g integer GENERATED ALWAYS AS (a*2 + b) STORED);")));
    }

    [TestMethod]
    public void AGeneratedColumnThatBecomesOrdinaryKeepsItsValues()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer, g integer GENERATED ALWAYS AS (a * 2) STORED);"),
            Build("CREATE TABLE public.t (a integer, g integer);"));

        Assert.AreEqual("ALTER TABLE \"public\".\"t\" ALTER COLUMN \"g\" DROP EXPRESSION;", string.Join("\n", statements));
    }

    [TestMethod]
    public void AnOrdinaryColumnThatBecomesGeneratedIsNotConvertedAndBlocksExecution()
    {
        var oldModel = Build("CREATE TABLE public.t (a integer, g integer);");
        var newModel = Build("CREATE TABLE public.t (a integer, g integer GENERATED ALWAYS AS (a * 2) STORED);");
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

        var script = string.Join("\n", batch.Operations.Select(static operation => operation.GenerateSql()));
        StringAssert.Contains(script, "-- TODO: Column \"g\" of \"public\".\"t\" became a generated column.");
        Assert.IsFalse(script.Contains("DROP COLUMN", StringComparison.Ordinal), "The values it holds must not be discarded silently.");
        Assert.IsNotEmpty(batch.GetUnsupportedOperations().ToArray());
    }

    [TestMethod]
    public void ARemovedGeneratedColumnIsDropped()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer, g integer GENERATED ALWAYS AS (a * 2) STORED);"),
            Build("CREATE TABLE public.t (a integer);"));

        Assert.AreEqual("ALTER TABLE \"public\".\"t\" DROP COLUMN \"g\";", string.Join("\n", statements));
    }

    [TestMethod]
    public void AFunctionTheExpressionCallsIsCreatedBeforeTheTable()
    {
        // The function's name sorts after the table's, so only a real dependency puts it first.
        var statements = Statements(
            DatabaseModel.Empty,
            Build(
                "CREATE TABLE public.t (a integer, g integer GENERATED ALWAYS AS (public.zz_double(a)) STORED);"
                + " CREATE FUNCTION public.zz_double(x integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT x * 2 $$;"));

        var function = statements.FindIndex(static statement => statement.Contains("FUNCTION", StringComparison.Ordinal));
        var table = statements.FindIndex(static statement => statement.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, function);
        Assert.IsLessThan(table, function, string.Join("\n", statements));
    }

    [TestMethod]
    public void ADefaultAndAnIdentityColumnAreUnaffected()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE TABLE public.t (id integer GENERATED ALWAYS AS IDENTITY, s text DEFAULT 'x', a integer);"));
        var create = statements.Single(static statement => statement.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        StringAssert.Contains(create, "\"id\" integer NOT NULL GENERATED ALWAYS AS IDENTITY");
        StringAssert.Contains(create, "\"s\" text DEFAULT 'x'");
        Assert.IsFalse(create.Contains("STORED", StringComparison.Ordinal));
        Assert.IsFalse(statements[0].Contains("STORED", StringComparison.Ordinal));
    }

    /// <summary>
    /// G-97: the table changes for another reason (a column is added). The generated column's expression is the same once both sides
    /// are PostgreSQL's rendering, though the parsed comparison cannot tell (the stored implicit cast), so it must not be dropped and
    /// re-added (a full table rewrite).
    /// </summary>
    [TestMethod]
    public void AnExpressionPostgresRewroteIsNotReAddedWhenTheTableChangesForAnotherReason()
    {
        const string Rendering = "((qty)::numeric * price)";
        var statements = Statements(
            WithGeneratedRendering(Build("create table public.t (qty integer, price numeric, total numeric generated always as (((qty)::numeric * price)) stored);"), Rendering),
            WithGeneratedRendering(Build("create table public.t (qty integer, price numeric, total numeric generated always as (qty * price) stored, note text);"), Rendering));

        var joined = string.Join("\n", statements);
        StringAssert.Contains(joined, "ADD COLUMN \"note\" text");
        Assert.IsFalse(joined.Contains("\"total\"", StringComparison.Ordinal), joined);
    }

    private static DatabaseModel WithGeneratedRendering(DatabaseModel model, string rendering)
    {
        model.Tables = model.Tables.ToDictionary(
            static entry => entry.Key,
            entry => entry.Value with
            {
                Columns = entry.Value.Columns
                    .Select(column => column.IsGenerated ? column with { ServerRenderedGeneratedExpression = rendering } : column)
                    .ToArray(),
            },
            StringComparer.OrdinalIgnoreCase);
        return model;
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
