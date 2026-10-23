namespace PgProj.Parser.Tests;

/// <summary>
/// G-194: the parents of <c>CREATE TABLE ... INHERITS (...)</c> are read, in order, with their schemas; an unqualified one is marked as defaulted for the search
/// path. A partition's parent is read as its parent, not as a table it inherits from.
/// </summary>
[TestClass]
public sealed class TableInheritsParsingTests
{
    [TestMethod]
    public void TheParentsAreReadInOrder()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE c (z int) INHERITS (s.a, b);").Tables.Single();

        CollectionAssert.AreEqual(new[] { "s.a", "public.b" }, table.InheritsFrom.Select(static parent => $"{parent.SchemaName}.{parent.Name}").ToArray());
    }

    [TestMethod]
    public void AnUnqualifiedParentIsGivenTheDefaultSchema()
    {
        var table = new PgQueryParser().Parse("""CREATE TABLE c () INHERITS ("Base");""").Tables.Single();

        var parent = table.InheritsFrom.Single();
        Assert.AreEqual("public", parent.SchemaName);
        Assert.AreEqual("Base", parent.Name);
        Assert.IsTrue(parent.IsSchemaDefaulted);
    }

    [TestMethod]
    public void APartitionDoesNotInherit()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE p1 PARTITION OF p FOR VALUES IN (1);").Tables.Single();

        Assert.IsEmpty(table.InheritsFrom);
        Assert.AreEqual("p", table.PartitionOfTableName);
    }

    [TestMethod]
    public void ATableWithoutInheritsHasNoParents()
    {
        Assert.IsEmpty(new PgQueryParser().Parse("CREATE TABLE t (a int);").Tables.Single().InheritsFrom);
    }
}
