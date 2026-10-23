using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-194: a child of <c>INHERITS</c> has its parents' columns first, in parent order, merged by name across parents, each marked with the parent it comes from; a
/// column the child declares with a parent's name is PostgreSQL's merge (the child's own, with the parent's default when it states none and NOT NULL when either
/// has it). A column's comment and identity are not inherited. A chain of parents is followed.
/// </summary>
[TestClass]
public sealed class TableInheritancePassTests
{
    [TestMethod]
    public void AChildHasItsParentsColumnsFirst()
    {
        var child = Table("CREATE TABLE p (id int NOT NULL, name text DEFAULT 'x'); CREATE TABLE c (extra int) INHERITS (p);", "public.c");

        CollectionAssert.AreEqual(new[] { "id", "name", "extra" }, child.Columns.Select(static column => column.Name).ToArray());
        CollectionAssert.AreEqual(new string?[] { "public.p", "public.p", null }, child.Columns.Select(static column => column.InheritedFromTable).ToArray());
        Assert.AreEqual("public.c.id", child.Columns[0].Key);
        Assert.IsFalse(child.Columns[0].AllowsNull);
        Assert.AreEqual("'x'", child.Columns[1].RawDefaultExpression);
        CollectionAssert.Contains(child.ReferencedTables.ToArray(), "public.p");
        CollectionAssert.AreEqual(new[] { "public.p" }, child.InheritsFrom.ToArray());
    }

    [TestMethod]
    public void TwoParentsColumnsAreMergedByName()
    {
        var child = Table("CREATE TABLE a (x int); CREATE TABLE b (y int, x int); CREATE TABLE c (z int) INHERITS (a, b);", "public.c");

        CollectionAssert.AreEqual(new[] { "x", "y", "z" }, child.Columns.Select(static column => column.Name).ToArray());
        CollectionAssert.AreEqual(new string?[] { "public.a", "public.b", null }, child.Columns.Select(static column => column.InheritedFromTable).ToArray());
    }

    [TestMethod]
    public void ARedeclaredColumnIsTheChildsOwn_WithTheParentsDefaultAndNotNull()
    {
        var child = Table("CREATE TABLE p (name text NOT NULL DEFAULT 'x'); CREATE TABLE c (name text) INHERITS (p);", "public.c");

        var name = child.Columns.Single();
        Assert.IsNull(name.InheritedFromTable);
        Assert.AreEqual("'x'", name.RawDefaultExpression);
        Assert.IsFalse(name.AllowsNull);
    }

    [TestMethod]
    public void ARedeclaredColumnKeepsItsOwnDefault()
    {
        var child = Table("CREATE TABLE p (name text DEFAULT 'x'); CREATE TABLE c (name text DEFAULT 'y') INHERITS (p);", "public.c");

        Assert.AreEqual("'y'", child.Columns.Single().RawDefaultExpression);
    }

    [TestMethod]
    public void ACommentAndAnIdentityAreNotInherited()
    {
        var child = Table("CREATE TABLE p (id int GENERATED ALWAYS AS IDENTITY); COMMENT ON COLUMN p.id IS 'key'; CREATE TABLE c () INHERITS (p);", "public.c");

        Assert.IsNull(child.Columns.Single().IdentityGeneration);
        Assert.IsNull(child.Columns.Single().Comment);
    }

    [TestMethod]
    public void AChainOfParentsIsFollowedWhateverTheOrderOfTheStatements()
    {
        var child = Table("CREATE TABLE c (d int) INHERITS (p); CREATE TABLE p (b int) INHERITS (gp); CREATE TABLE gp (a int);", "public.c");

        CollectionAssert.AreEqual(new[] { "a", "b", "d" }, child.Columns.Select(static column => column.Name).ToArray());
        CollectionAssert.AreEqual(new string?[] { "public.p", "public.p", null }, child.Columns.Select(static column => column.InheritedFromTable).ToArray());
    }

    private static TableDefinition Table(string sql, string key)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]).Tables[key];
}
