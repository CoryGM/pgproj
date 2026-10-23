using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-136: a typed table is compared like any table (its columns come from its type), plus the type it is of; the same typed table written another way is not a change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypedTableDiffTests
{
    private const string Types = "CREATE TYPE public.r AS (k integer, v text); CREATE TYPE public.r2 AS (k integer, v text); ";

    [TestMethod]
    public void ATypedTableDiffedWithItselfIsEmpty()
    {
        var model = Build(Types + "CREATE TABLE public.t OF public.r (v WITH OPTIONS DEFAULT 'x');");

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    public void TheSameTypedTableWrittenAnotherWayReportsNothing()
    {
        var first = Build(Types + "CREATE TABLE public.t OF public.r (v WITH OPTIONS NOT NULL DEFAULT 'x');");
        var second = Build(Types + "CREATE TABLE t OF r (v WITH OPTIONS DEFAULT 'x' NOT NULL);");

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(first, second)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(second, first)));
    }

    [TestMethod]
    public void ADifferentTypeIsReported()
    {
        var diff = new DiffEngine().Diff(Build(Types + "CREATE TABLE public.t OF public.r;"), Build(Types + "CREATE TABLE public.t OF public.r2;"));

        var modified = diff.Modified.Single(static change => change.NodeKind == ChangeNodeKind.Table);
        Assert.IsTrue(modified.Details.Any(static detail => detail.PropertyName == nameof(TableDefinition.OfTypeKey)));
    }

    [TestMethod]
    public void ATableThatBecomesTypedIsReported()
    {
        var diff = new DiffEngine().Diff(Build(Types + "CREATE TABLE public.t (k integer, v text);"), Build(Types + "CREATE TABLE public.t OF public.r;"));

        Assert.IsTrue(diff.Modified.Single(static change => change.NodeKind == ChangeNodeKind.Table).Details.Any(static detail => detail.PropertyName == nameof(TableDefinition.OfTypeKey)));
    }

    [TestMethod]
    public void AFieldAddedToTheTypeIsAChangeToTheTypeAndToTheTable()
    {
        var diff = new DiffEngine().Diff(
            Build(Types + "CREATE TABLE public.t OF public.r;"),
            Build("CREATE TYPE public.r AS (k integer, v text, extra text); CREATE TYPE public.r2 AS (k integer, v text); CREATE TABLE public.t OF public.r;"));

        Assert.AreEqual(1, diff.Modified.Count(static change => change.NodeKind == ChangeNodeKind.Type));
        Assert.AreEqual(1, diff.Modified.Count(static change => change.NodeKind == ChangeNodeKind.Table));
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
