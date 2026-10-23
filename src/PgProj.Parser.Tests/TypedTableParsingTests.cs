using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-136 (which reversed G-118's warn-and-ignore): a typed table (<c>CREATE TABLE t OF some_type</c>) is read with the type it is of, and with the columns it gives a default
/// or NOT NULL (<c>WITH OPTIONS</c>); the rest of its columns are its type's fields, which a later step reads from the type. A temporary typed table is still a temporary object.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypedTableParsingTests
{
    [TestMethod]
    public void ATypedTableIsReadWithItsTypeAndItsWithOptionsColumns()
    {
        var tree = Parse("CREATE TABLE app.typed OF app.r (k WITH OPTIONS PRIMARY KEY, v WITH OPTIONS DEFAULT 'x' NOT NULL);");

        Assert.IsEmpty(tree.Diagnostics);
        var table = tree.Tables.Single();
        Assert.AreEqual("typed", table.Name);
        Assert.AreEqual("app", table.OfTypeSchemaName);
        Assert.AreEqual("r", table.OfTypeName);
        Assert.IsFalse(table.IsOfTypeSchemaDefaulted);
        CollectionAssert.AreEqual(new[] { "k", "v" }, table.Columns.Select(static column => column.Name).ToArray());
        Assert.IsNull(table.Columns[0].DataType, "The column's type is its field's, which is not written here.");
        Assert.AreEqual("'x'", table.Columns[1].RawDefaultExpression);
        Assert.AreEqual(false, table.Columns[1].IsNullable);
    }

    [TestMethod]
    public void ATypedTableWithNoOptionsHasNoColumnsOfItsOwn()
    {
        var table = Parse("CREATE TABLE typed OF r;").Tables.Single();

        Assert.IsEmpty(table.Columns);
        Assert.AreEqual("public", table.OfTypeSchemaName);
        Assert.IsTrue(table.IsOfTypeSchemaDefaulted);
    }

    [TestMethod]
    public void ATypedTablesConstraintsAreRead()
    {
        var tree = Parse("CREATE TABLE public.typed OF public.r (k WITH OPTIONS PRIMARY KEY, CONSTRAINT v_check CHECK (v > 0), UNIQUE (v));");

        Assert.AreEqual(3, tree.Constraints.Count());
    }

    [TestMethod]
    public void ATemporaryTypedTableIsReportedOnceAsTemporary()
    {
        var diagnostic = Parse("CREATE TEMP TABLE typed OF public.r;").Diagnostics.Single();

        StringAssert.StartsWith(diagnostic.Message, "CREATE TEMPORARY TABLE is not modeled");
    }

    [TestMethod]
    public void AnOrdinaryTableIsNotTyped()
    {
        var tree = Parse("CREATE TYPE public.r AS (k int); CREATE TABLE public.t (a public.r);");

        Assert.IsEmpty(tree.Diagnostics);
        Assert.IsNull(tree.Tables.Single().OfTypeName);
    }

    private static SqlSyntaxTree Parse(string sql) => new PgQueryParser().Parse(sql);
}
