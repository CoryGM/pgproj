using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-24c (Phase 3): a new or removed partition is an ordinary table add/remove (works once Phase 2's columns and dependency
/// edge are right, with no diff-layer special-casing needed); a changed partition strategy, key, parent, or bound on an
/// existing table is reported as a modification. Deploy's fail-closed placeholder for the structural cases is Phase 4.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TablePartitioningDiffTests
{
    private const string Hierarchy =
        "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
        + " CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');";

    [TestMethod]
    public void SelfDiffOfAPartitionedHierarchyIsEmpty()
    {
        var model = Build(Hierarchy);

        var diff = new DiffEngine().Diff(model, model);
        Assert.AreEqual(0, diff.Added.Count + diff.Removed.Count + diff.Modified.Count);
    }

    [TestMethod]
    public void ANewPartitionIsAnOrdinaryAdd()
    {
        var diff = new DiffEngine().Diff(
            Build("CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"),
            Build(Hierarchy));

        CollectionAssert.Contains(diff.Added.Select(static c => c.NodeId).ToArray(), "public.p_2024");
        Assert.AreEqual(0, diff.Modified.Count);
    }

    [TestMethod]
    public void ARemovedPartitionIsAnOrdinaryRemove()
    {
        var diff = new DiffEngine().Diff(
            Build(Hierarchy),
            Build("CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"));

        CollectionAssert.Contains(diff.Removed.Select(static c => c.NodeId).ToArray(), "public.p_2024");
        Assert.AreEqual(0, diff.Modified.Count);
    }

    [TestMethod]
    public void AChangedPartitionStrategyIsReported()
    {
        var diff = new DiffEngine().Diff(
            Build("CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"),
            Build("CREATE TABLE public.p (id integer, d date) PARTITION BY LIST (d);"));

        var detail = diff.Modified.Single().Details.Single(static d => d.PropertyName == nameof(TableDefinition.PartitionStrategy));
        Assert.AreEqual("RANGE", detail.OldValue);
        Assert.AreEqual("LIST", detail.NewValue);
    }

    [TestMethod]
    public void AReorderedPartitionKeyIsReported()
    {
        var diff = new DiffEngine().Diff(
            Build("CREATE TABLE public.p (a int, b int) PARTITION BY RANGE (a, b);"),
            Build("CREATE TABLE public.p (a int, b int) PARTITION BY RANGE (b, a);"));

        Assert.IsTrue(diff.Modified.Single().Details.Any(static d => d.PropertyName == nameof(TableDefinition.PartitionKeyElements)));
    }

    [TestMethod]
    public void TheSamePartitionKeyInTheSameOrderReportsNothing()
    {
        var diff = new DiffEngine().Diff(
            Build("CREATE TABLE public.p (a int, b int) PARTITION BY RANGE (a, b);"),
            Build("CREATE TABLE public.p (a int, b int) PARTITION BY RANGE (a, b);"));

        Assert.AreEqual(0, diff.Added.Count + diff.Removed.Count + diff.Modified.Count);
    }

    [TestMethod]
    public void AChangedBoundIsReported()
    {
        var diff = new DiffEngine().Diff(
            Build(Hierarchy),
            Build(
                "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
                + " CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2026-01-01');"));

        var change = diff.Modified.Single(static c => c.NodeId == "public.p_2024");
        var detail = change.Details.Single(static d => d.PropertyName == nameof(TableDefinition.PartitionBoundRaw));
        StringAssert.Contains(detail.OldValue, "2025");
        StringAssert.Contains(detail.NewValue, "2026");
    }

    [TestMethod]
    public void ARepartitionedTableIsReportedByItsNewParent()
    {
        var diff = new DiffEngine().Diff(
            Build(Hierarchy),
            Build(
                "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
                + " CREATE TABLE public.q (id integer, d date) PARTITION BY RANGE (d);"
                + " CREATE TABLE public.p_2024 PARTITION OF public.q FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');"));

        var change = diff.Modified.Single(static c => c.NodeId == "public.p_2024");
        var detail = change.Details.Single(static d => d.PropertyName == nameof(TableDefinition.PartitionOfTableName));
        Assert.AreEqual("p", detail.OldValue);
        Assert.AreEqual("q", detail.NewValue);
    }

    [TestMethod]
    public void AnOrdinaryTableDiffedAgainstItselfReportsNothing()
    {
        var diff = new DiffEngine().Diff(Build("CREATE TABLE public.t (a integer);"), Build("CREATE TABLE public.t (a integer);"));

        Assert.AreEqual(0, diff.Added.Count + diff.Removed.Count + diff.Modified.Count);
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
