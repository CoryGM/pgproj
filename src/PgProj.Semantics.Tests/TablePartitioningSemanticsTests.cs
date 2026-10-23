using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-24c (Phase 2): a partitioned parent's strategy/key and a partition's parent link/bound reach the semantic model, and a
/// partition's columns (which its own DDL never lists) are inherited from the parent so every table has real columns, the same
/// as PostgreSQL's own catalog (each partition physically has its own attribute rows, verified on a real server).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TablePartitioningSemanticsTests
{
    [TestMethod]
    public void APartitionedParentsStrategyAndKeyReachTheModel()
    {
        var model = Build("CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);");

        var table = model.Tables["public.p"];
        Assert.AreEqual("RANGE", table.PartitionStrategy);
        Assert.AreEqual("d", table.PartitionKeyElements.Single().Name);
    }

    [TestMethod]
    public void APartitionsParentLinkAndBoundReachTheModel()
    {
        var model = Build(
            "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');");

        var child = model.Tables["public.p_2024"];
        Assert.AreEqual("public", child.PartitionOfSchemaName);
        Assert.AreEqual("p", child.PartitionOfTableName);
        Assert.AreEqual("FOR VALUES FROM ('2024-01-01') TO ('2025-01-01')", child.PartitionBoundRaw);
    }

    [TestMethod]
    public void APartitionsColumnsAreInheritedFromItsParent()
    {
        var model = Build(
            "CREATE TABLE public.p (id integer, d date, name text) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');");

        var child = model.Tables["public.p_2024"];
        CollectionAssert.AreEquivalent(new[] { "id", "d", "name" }, child.Columns.Select(static c => c.Name).ToArray());
        Assert.IsTrue(child.Columns.All(static c => string.Equals(c.TableName, "p_2024", StringComparison.Ordinal)));
        Assert.IsTrue(child.Columns.All(static c => c.Key.StartsWith("public.p_2024.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void EachPartitionGetsItsOwnColumnKeysNotTheParents()
    {
        var model = Build(
            "CREATE TABLE public.p (id integer) PARTITION BY RANGE (id);"
            + " CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM (1) TO (100);"
            + " CREATE TABLE public.p2 PARTITION OF public.p FOR VALUES FROM (100) TO (200);");

        var keys = model.Tables.Values.SelectMany(static t => t.Columns.Select(static c => c.Key)).ToArray();
        CollectionAssert.AreEquivalent(new[] { "public.p.id", "public.p1.id", "public.p2.id" }, keys);
    }

    [TestMethod]
    public void ADefaultPartitionInheritsColumnsToo()
    {
        var model = Build(
            "CREATE TABLE public.p (id integer, cat text) PARTITION BY LIST (cat);"
            + " CREATE TABLE public.p_default PARTITION OF public.p DEFAULT;");

        var child = model.Tables["public.p_default"];
        Assert.AreEqual("DEFAULT", child.PartitionBoundRaw);
        CollectionAssert.AreEquivalent(new[] { "id", "cat" }, child.Columns.Select(static c => c.Name).ToArray());
    }

    [TestMethod]
    public void AColumnsTypeAndNullabilityAreInheritedAsWrittenOnTheParent()
    {
        var model = Build(
            "CREATE TABLE public.p (id integer NOT NULL, note text) PARTITION BY RANGE (id);"
            + " CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM (1) TO (100);");

        var parentId = model.Tables["public.p"].Columns.Single(static c => c.Name == "id");
        var childId = model.Tables["public.p1"].Columns.Single(static c => c.Name == "id");
        Assert.AreEqual(parentId.RawType, childId.RawType);
        Assert.AreEqual(parentId.IsNullable, childId.IsNullable);
    }

    [TestMethod]
    public void AnOrdinaryTableHasNoPartitionDetails()
    {
        var table = Build("CREATE TABLE public.t (a integer);").Tables["public.t"];

        Assert.IsNull(table.PartitionStrategy);
        Assert.IsEmpty(table.PartitionKeyElements);
        Assert.IsNull(table.PartitionOfSchemaName);
        Assert.IsNull(table.PartitionOfTableName);
        Assert.IsNull(table.PartitionBoundRaw);
    }

    [TestMethod]
    public void ThePipelineReportsNoErrorsForAPartitionedHierarchy()
    {
        var model = Build(
            "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');"
            + " CREATE TABLE public.p_default PARTITION OF public.p DEFAULT;");

        Assert.IsFalse(model.HasErrors, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
