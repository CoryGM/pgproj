using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-71: a generated column is compared on its parsed expression, not its text, because PostgreSQL stores it fully parenthesized. Both
/// directions are tested (<c>copilot-diff.md</c>): the same column written or read back differently reports nothing, and a real change to
/// the expression, or to whether the column is generated at all, is reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class GeneratedColumnDiffTests
{
    private const string Table = "CREATE TABLE public.t (a integer, b integer, n text, v varchar(20), g integer GENERATED ALWAYS AS ({0}) STORED);";

    [TestMethod]
    public void ASelfDiffIsEmpty()
    {
        AssertNoChanges(new DiffEngine().Diff(Build(string.Format(Table, "a * 2 + b")), Build(string.Format(Table, "a * 2 + b"))));
    }

    [TestMethod]
    [DataRow("a * 2", "(a * 2)")]
    [DataRow("a * 2 + b", "((a * 2) + b)")]
    [DataRow("a*2   +b", "A * 2 + B")]
    [DataRow("(a + b) * 2", "((a + b) * 2)")]
    [DataRow("a in (1, 2)", "(a = ANY (ARRAY[1, 2]))")]
    [DataRow("upper(v)", "upper((v)::text)")]
    [DataRow("v || 'x'", "((v)::text || 'x'::text)")]
    [DataRow("coalesce(a, 0) + 1", "(COALESCE(a, 0) + 1)")]
    [DataRow("case when a > 3 then 'big' else 'small' end", "CASE WHEN (a > 3) THEN 'big'::text ELSE 'small'::text END")]
    public void TheSameExpressionWrittenDifferentlyOrAsPostgreSqlPrintsItReportsNothing(string written, string rendered)
    {
        AssertNoChanges(new DiffEngine().Diff(Build(string.Format(Table, rendered)), Build(string.Format(Table, written))));
        AssertNoChanges(new DiffEngine().Diff(Build(string.Format(Table, written)), Build(string.Format(Table, rendered))));
    }

    [TestMethod]
    [DataRow("a * 2", "a * 3")]
    [DataRow("a * 2", "b * 2")]
    [DataRow("a + b * 2", "(a + b) * 2")]
    [DataRow("n || 'x'", "n || 'X'")]
    [DataRow("lower(n)", "upper(n)")]
    public void AChangedExpressionIsReported(string before, string after)
    {
        var changes = new DiffEngine().Diff(Build(string.Format(Table, before)), Build(string.Format(Table, after)));

        var detail = changes.Modified.Single(static change => change.NodeId == "public.t.g").Details.Single();
        Assert.AreEqual("GeneratedExpression", detail.PropertyName);
        Assert.AreEqual(before, detail.OldValue);
        Assert.AreEqual(after, detail.NewValue);
    }

    [TestMethod]
    public void ChangingWhetherAColumnIsGeneratedIsReportedBothWays()
    {
        var ordinary = "CREATE TABLE public.t (a integer, g integer);";
        var generated = "CREATE TABLE public.t (a integer, g integer GENERATED ALWAYS AS (a * 2) STORED);";

        Assert.IsTrue(new DiffEngine().Diff(Build(ordinary), Build(generated)).Modified.Any(static change => change.NodeId == "public.t.g"));
        Assert.IsTrue(new DiffEngine().Diff(Build(generated), Build(ordinary)).Modified.Any(static change => change.NodeId == "public.t.g"));
    }

    [TestMethod]
    public void AddingAndRemovingAGeneratedColumnAreReported()
    {
        var without = "CREATE TABLE public.t (a integer);";
        var with = "CREATE TABLE public.t (a integer, g integer GENERATED ALWAYS AS (a * 2) STORED);";

        Assert.IsTrue(new DiffEngine().Diff(Build(without), Build(with)).Added.Any(static change => change.NodeId == "public.t.g"));
        Assert.IsTrue(new DiffEngine().Diff(Build(with), Build(without)).Removed.Any(static change => change.NodeId == "public.t.g"));
    }

    [TestMethod]
    public void AGeneratedColumnAddedByAlterTableIsTheSameAsOneDeclaredInline()
    {
        AssertNoChanges(new DiffEngine().Diff(
            Build("CREATE TABLE public.t (a integer, g integer GENERATED ALWAYS AS (a * 2) STORED);"),
            Build("CREATE TABLE public.t (a integer); ALTER TABLE public.t ADD COLUMN g integer GENERATED ALWAYS AS (a * 2) STORED;")));
    }

    private static void AssertNoChanges(ChangeSet changes)
        => Assert.AreEqual(0, changes.Added.Count + changes.Removed.Count + changes.Modified.Count, Describe(changes));

    private static string Describe(ChangeSet changes)
        => "Added: " + string.Join(", ", changes.Added.Select(static change => change.NodeId))
            + " Removed: " + string.Join(", ", changes.Removed.Select(static change => change.NodeId))
            + " Modified: " + string.Join("; ", changes.Modified.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => detail.PropertyName + ": " + detail.OldValue + " -> " + detail.NewValue)) + "]"));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
