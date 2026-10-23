using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-71: pg_query keeps <c>GENERATED ALWAYS AS (expr) STORED</c> in the column's <c>constraints</c> as a <c>CONSTR_GENERATED</c> entry, and the
/// parser looked for it on the column itself, so a generated column reached the model as an ordinary one and was deployed without its
/// expression. The clause is now captured, with the expression as the author wrote it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class GeneratedColumnParsingTests
{
    [TestMethod]
    [DataRow("a * 2", "a * 2")]
    [DataRow("(a + b) * 2", "(a + b) * 2")]
    [DataRow("coalesce(a, 0) + 1", "coalesce(a, 0) + 1")]
    [DataRow("case when a > 3 then 'big' else 'small' end", "case when a > 3 then 'big' else 'small' end")]
    [DataRow("n || ')' || ','", "n || ')' || ','")]
    [DataRow("  a*2  ", "a*2")]
    [DataRow("lower(\"Mixed Col\")", "lower(\"Mixed Col\")")]
    [DataRow("a::text", "a::text")]
    public void TheExpressionIsCapturedAsWritten(string expression, string expected)
    {
        var column = Column($"CREATE TABLE t (a integer, b integer, n text, \"Mixed Col\" text, g text GENERATED ALWAYS AS ({expression}) STORED);", "g");

        Assert.IsTrue(column.IsGenerated);
        Assert.AreEqual(expected, column.GeneratedExpression);
        Assert.IsNotNull(column.GeneratedExpressionAst);
    }

    [TestMethod]
    public void TheClauseMayBeFollowedByOtherColumnOptions()
    {
        var column = Column("CREATE TABLE t (a integer, g integer GENERATED ALWAYS AS (a * 2) STORED NOT NULL);", "g");

        Assert.IsTrue(column.IsGenerated);
        Assert.AreEqual("a * 2", column.GeneratedExpression);
        Assert.IsFalse(column.IsNullable ?? true);
    }

    [TestMethod]
    public void EachGeneratedColumnKeepsItsOwnExpression()
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE t (a integer, g1 integer GENERATED ALWAYS AS (a + 1) STORED, plain integer DEFAULT 5, g2 integer GENERATED ALWAYS AS (a * (a + 2)) STORED);");
        var columns = tree.Tables.Single().Columns.ToDictionary(static column => column.Name!);

        Assert.AreEqual("a + 1", columns["g1"].GeneratedExpression);
        Assert.AreEqual("a * (a + 2)", columns["g2"].GeneratedExpression);
        Assert.IsFalse(columns["a"].IsGenerated);
        Assert.IsFalse(columns["plain"].IsGenerated);
        Assert.IsNull(columns["plain"].GeneratedExpression);
        Assert.AreEqual("5", columns["plain"].RawDefaultExpression);
    }

    [TestMethod]
    public void AGeneratedColumnHasNoDefaultAndAnIdentityColumnIsNotGenerated()
    {
        var generated = Column("CREATE TABLE t (a integer, g integer GENERATED ALWAYS AS (a * 2) STORED);", "g");
        var identity = Column("CREATE TABLE t (id integer GENERATED ALWAYS AS IDENTITY, a integer);", "id");

        Assert.IsNull(generated.RawDefaultExpression);
        Assert.IsFalse(identity.IsGenerated);
        Assert.IsNull(identity.GeneratedExpression);
        Assert.AreEqual("ALWAYS", identity.IdentityGeneration, ignoreCase: true);
    }

    [TestMethod]
    public void AGeneratedColumnAddedByAlterTableIsCaptured()
    {
        var add = new PgQueryParser().Parse("ALTER TABLE t ADD COLUMN g integer GENERATED ALWAYS AS (a * 2) STORED, ADD COLUMN h text GENERATED ALWAYS AS (n || 'x') STORED;")
            .Nodes.OfType<AlterTableAddColumnNode>().ToDictionary(static node => node.Column.Name!);

        Assert.AreEqual("a * 2", add["g"].Column.GeneratedExpression);
        Assert.AreEqual("n || 'x'", add["h"].Column.GeneratedExpression);
        Assert.IsTrue(add["g"].Column.IsGenerated && add["h"].Column.IsGenerated);
    }

    [TestMethod]
    public void AnOrdinaryColumnIsNotGeneratedAndTheConstraintIsNotAConstraint()
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE t (a integer, g integer GENERATED ALWAYS AS (a * 2) STORED);");

        Assert.IsFalse(tree.Tables.Single().Columns.Single(static column => column.Name == "a").IsGenerated);
        Assert.IsEmpty(tree.Constraints.ToArray());
    }

    private static ColumnNode Column(string sql, string name)
        => new PgQueryParser().Parse(sql).Tables.Single().Columns.Single(column => column.Name == name);
}
