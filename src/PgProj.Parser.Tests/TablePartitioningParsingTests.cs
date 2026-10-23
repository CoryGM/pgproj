using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-24c (Phase 1): a partitioned parent's <c>PARTITION BY strategy (...)</c> and a partition's
/// <c>PARTITION OF parent FOR VALUES ...</c>/<c>DEFAULT</c> are captured. A partition's bound is kept verbatim, not re-rendered
/// (the same reasoning as <c>ConstraintIndexTextReader</c>, G-73): PostgreSQL's own renderer only handles a few node kinds.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TablePartitioningParsingTests
{
    [TestMethod]
    public void AnOrdinaryTableHasNoPartitionDetails()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.t (a integer);").Tables.Single();

        Assert.IsNull(table.PartitionStrategy);
        Assert.IsEmpty(table.PartitionKeyElements);
        Assert.IsNull(table.PartitionOfSchemaName);
        Assert.IsNull(table.PartitionOfTableName);
        Assert.IsNull(table.PartitionBoundRaw);
    }

    [TestMethod]
    [DataRow("RANGE")]
    [DataRow("LIST")]
    [DataRow("HASH")]
    public void APartitionedParentsStrategyIsCaptured(string strategy)
    {
        var table = new PgQueryParser().Parse($"CREATE TABLE public.p (id integer) PARTITION BY {strategy} (id);").Tables.Single();

        Assert.AreEqual(strategy, table.PartitionStrategy);
        Assert.AreEqual("id", table.PartitionKeyElements.Single().Name);
        Assert.IsNull(table.PartitionKeyElements.Single().RawExpression);
    }

    [TestMethod]
    public void AMultiColumnKeyIsKeptInOrder()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.p (a int, b int) PARTITION BY RANGE (a, b);").Tables.Single();

        CollectionAssert.AreEqual(new[] { "a", "b" }, table.PartitionKeyElements.Select(static k => k.Name).ToArray());
    }

    [TestMethod]
    public void AnExpressionKeyElementIsKeptVerbatim()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (extract(year from d));").Tables.Single();

        var element = table.PartitionKeyElements.Single();
        Assert.IsNull(element.Name);
        Assert.AreEqual("extract(year from d)", element.RawExpression);
    }

    [TestMethod]
    public void ADefaultPartitionIsCaptured()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.p_default PARTITION OF public.p DEFAULT;").Tables.Single();

        Assert.IsNull(table.PartitionStrategy);
        Assert.AreEqual("public", table.PartitionOfSchemaName);
        Assert.AreEqual("p", table.PartitionOfTableName);
        Assert.AreEqual("DEFAULT", table.PartitionBoundRaw);
    }

    [TestMethod]
    public void ARangeBoundIsCapturedAsWritten()
    {
        var table = new PgQueryParser().Parse(
            "CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');").Tables.Single();

        Assert.AreEqual("FOR VALUES FROM ('2024-01-01') TO ('2025-01-01')", table.PartitionBoundRaw);
    }

    [TestMethod]
    public void ARangeBoundStopsBeforeATrailingTablespaceClause()
    {
        var table = new PgQueryParser().Parse(
            "CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01') TABLESPACE fastspace;").Tables.Single();

        Assert.AreEqual("FOR VALUES FROM ('2024-01-01') TO ('2025-01-01')", table.PartitionBoundRaw);
        Assert.AreEqual("fastspace", table.TablespaceName);
    }

    [TestMethod]
    public void AListBoundIsCapturedAsWritten()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.p_l PARTITION OF public.p FOR VALUES IN ('a', 'b');").Tables.Single();

        Assert.AreEqual("FOR VALUES IN ('a', 'b')", table.PartitionBoundRaw);
    }

    [TestMethod]
    public void AHashBoundIsCapturedAsWritten()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.p_h PARTITION OF public.p FOR VALUES WITH (MODULUS 4, REMAINDER 1);").Tables.Single();

        Assert.AreEqual("FOR VALUES WITH (MODULUS 4, REMAINDER 1)", table.PartitionBoundRaw);
    }

    [TestMethod]
    public void AnUnqualifiedParentIsTheOneInTheDefaultSchema()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE p_l PARTITION OF p FOR VALUES IN ('a');").Tables.Single();

        Assert.AreEqual("public", table.PartitionOfSchemaName, "An unqualified parent is the one in public (G-114).");
        Assert.AreEqual("p", table.PartitionOfTableName);
    }

    [TestMethod]
    public void LegacyInheritanceIsNotReadAsPartitioning()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.child (a int) INHERITS (public.parent);").Tables.Single();

        Assert.IsNull(table.PartitionOfSchemaName);
        Assert.IsNull(table.PartitionOfTableName);
        Assert.IsNull(table.PartitionBoundRaw);
    }

    [TestMethod]
    public void APartitionChildDeclaresNoColumnsOfItsOwn()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.p_l PARTITION OF public.p FOR VALUES IN ('a');").Tables.Single();

        Assert.IsEmpty(table.Columns);
    }

    [TestMethod]
    public void EachTableOfAMultiStatementScriptReadsItsOwnPartitionDetails()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE SCHEMA s;\n\n-- a comment mentioning PARTITION BY RANGE (x)\nCREATE TABLE s.p (id integer, d date) PARTITION BY RANGE (d);\nCREATE TABLE s.p1 PARTITION OF s.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');");

        var parent = tree.Tables.Single(static t => t.Name == "p");
        Assert.AreEqual("RANGE", parent.PartitionStrategy);
        Assert.AreEqual("d", parent.PartitionKeyElements.Single().Name);

        var child = tree.Tables.Single(static t => t.Name == "p1");
        Assert.AreEqual("s", child.PartitionOfSchemaName);
        Assert.AreEqual("p", child.PartitionOfTableName);
        Assert.AreEqual("FOR VALUES FROM ('2024-01-01') TO ('2025-01-01')", child.PartitionBoundRaw);
    }
}
