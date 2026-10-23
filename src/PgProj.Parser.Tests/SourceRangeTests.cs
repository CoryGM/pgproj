namespace PgProj.Parser.Tests;

/// <summary>
/// G-309e: every node the parser makes knows where it was written, so a semantic diagnostic about it can name the place: its statement without the <c>;</c>,
/// and for a column, a constraint, a column's type, and a foreign key's referenced table, their own text. A place is never part of a node's equality.
/// </summary>
[TestClass]
public sealed class SourceRangeTests
{
    private static SqlSyntaxTree Parse(string sql) => new PgQueryParser().Parse(sql);

    [TestMethod]
    public void AStatementOnOneLineIsPlacedWithoutItsSemicolon()
    {
        var table = Parse("CREATE TABLE app.t (id int);").Tables.Single();

        Assert.AreEqual(new SourceRange(1, 1, 1, 28), table.Range);
    }

    [TestMethod]
    public void AStatementOverSeveralLinesEndsOnItsLastLine()
    {
        var view = Parse("-- a view\nCREATE VIEW app.v AS\n  SELECT 1 AS one\n  ;\n").Views.Single();

        Assert.AreEqual(new SourceRange(2, 1, 3, 18), view.Range);
    }

    [TestMethod]
    public void ColumnsAreCountedInCharactersAfterNonAsciiText()
    {
        var tree = Parse("CREATE TABLE app.\"Käse\" (\"größe\" int, id int);");

        Assert.AreEqual(new SourceRange(1, 1, 1, 46), tree.Tables.Single().Range);
        Assert.AreEqual(new SourceRange(1, 26, 1, 33), tree.Columns.Single(static column => column.Name == "größe").Range);
        Assert.AreEqual(new SourceRange(1, 39, 1, 41), tree.Columns.Single(static column => column.Name == "id").Range);
    }

    [TestMethod]
    public void TheSecondStatementInAFileHasItsOwnPlace()
    {
        var tree = Parse("CREATE TABLE app.a (id int);\n\nCREATE TABLE app.b (\n  id int\n);\n");

        Assert.AreEqual(new SourceRange(1, 1, 1, 28), tree.Tables.Single(static table => table.Name == "a").Range);
        Assert.AreEqual(new SourceRange(3, 1, 5, 2), tree.Tables.Single(static table => table.Name == "b").Range);
        Assert.AreEqual(new SourceRange(4, 3, 4, 5), tree.Columns.Single(static column => column.TableName == "b").Range);
    }

    [TestMethod]
    public void AColumnsTypeAndAForeignKeysTableHaveTheirOwnPlaces()
    {
        var tree = Parse("CREATE TABLE app.orders (\n  id int PRIMARY KEY,\n  kind app.mood,\n  customer_id int REFERENCES app.customer (id)\n);");

        var kind = tree.Columns.Single(static column => column.Name == "kind");
        Assert.AreEqual(new SourceRange(3, 3, 3, 7), kind.Range);
        Assert.AreEqual(new SourceRange(3, 8, 3, 16), kind.TypeRange);
        var foreignKey = tree.Constraints.Single(static constraint => constraint.ConstraintType == "CONSTR_FOREIGN");
        Assert.AreEqual(new SourceRange(4, 19, 4, 29), foreignKey.Range, "the word that starts it");
        Assert.AreEqual(new SourceRange(4, 30, 4, 42), foreignKey.ReferencedTableRange);
    }

    [TestMethod]
    public void ANamedConstraintIsPlacedFromConstraintToItsName()
    {
        var constraint = Parse("ALTER TABLE app.t ADD CONSTRAINT t_fk FOREIGN KEY (a) REFERENCES \"Other\".\"T\" (id);").Constraints.Single();

        Assert.AreEqual(new SourceRange(1, 23, 1, 38), constraint.Range);
        Assert.AreEqual(new SourceRange(1, 66, 1, 77), constraint.ReferencedTableRange);
    }

    [TestMethod]
    public void TwoNodesThatDifferOnlyInWhereTheyWereWrittenAreEqual()
    {
        var first = Parse("CREATE TABLE app.t (id int);").Tables.Single();
        var second = Parse("\n\n   CREATE TABLE app.t (id int);").Tables.Single();

        Assert.AreNotEqual(first.Range, second.Range);
        Assert.AreEqual(first with { Columns = [] }, second with { Columns = [] });
        Assert.AreEqual((first with { Columns = [] }).GetHashCode(), (second with { Columns = [] }).GetHashCode());
    }
}
