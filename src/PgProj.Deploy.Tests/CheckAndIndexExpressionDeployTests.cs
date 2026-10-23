using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-73: the condition of a CHECK, the predicate of an index, and an index key expression are deployed as the author wrote them. They
/// were rendered by a renderer that dropped what it did not know, so <c>BETWEEN 1 AND 9</c> was deployed as <c>BETWEEN 1, 9</c>, and the
/// outer parentheses of <c>(a &gt; 0) AND (b &gt; 0)</c> were stripped as if they were one group.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CheckAndIndexExpressionDeployTests
{
    [TestMethod]
    [DataRow("a BETWEEN 1 AND 9")]
    [DataRow("a IN (1, 2)")]
    [DataRow("a = ANY (ARRAY[1, 2])")]
    [DataRow("(a > 0) AND (b > 0)")]
    [DataRow("(a > 0) OR (b > 0)")]
    [DataRow("CASE WHEN a > 0 THEN true ELSE false END")]
    [DataRow("(a > 0) IS TRUE")]
    [DataRow("coalesce(a, 0) > 0")]
    [DataRow("a IS DISTINCT FROM b")]
    [DataRow("n COLLATE \"C\" > 'a)('")]
    public void ACheckIsDeployedAsWritten(string condition)
    {
        var statements = Statements(DatabaseModel.Empty, Build($"CREATE TABLE public.t (a integer, b integer, n text, CONSTRAINT c CHECK ({condition}));"));

        Assert.IsTrue(
            statements.Any(statement => statement.Contains($"CHECK ({condition})", StringComparison.Ordinal)),
            string.Join("\n", statements));
    }

    [TestMethod]
    public void AChangedCheckIsReplacedByTheNewCondition()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer, CONSTRAINT c CHECK (a BETWEEN 1 AND 9));"),
            Build("CREATE TABLE public.t (a integer, CONSTRAINT c CHECK (a BETWEEN 1 AND 20 AND a <> 13));"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "DROP CONSTRAINT");
        StringAssert.Contains(script, "CHECK (a BETWEEN 1 AND 20 AND a <> 13)");
    }

    [TestMethod]
    public void AnEquivalentlyWrittenCheckDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build("CREATE TABLE public.t (a integer, CONSTRAINT c CHECK (((a >= 1) AND (a <= 9))));"),
            Build("CREATE TABLE public.t (a integer, CONSTRAINT c CHECK (a BETWEEN 1 AND 9));")));
    }

    [TestMethod]
    public void AnIndexPredicateAndKeyExpressionsAreDeployedAsWritten()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build("CREATE TABLE public.t (a integer, b integer, n text); CREATE INDEX i ON public.t (lower(n), (a + b) DESC) WHERE a IN (1, 2) AND (b BETWEEN 1 AND 9 OR n IS NULL);"));

        var index = statements.Single(static statement => statement.Contains("CREATE INDEX \"i\"", StringComparison.Ordinal));
        StringAssert.Contains(index, "lower(n)");
        StringAssert.Contains(index, "(a + b)");
        StringAssert.Contains(index, "WHERE a IN (1, 2) AND (b BETWEEN 1 AND 9 OR n IS NULL)");
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
