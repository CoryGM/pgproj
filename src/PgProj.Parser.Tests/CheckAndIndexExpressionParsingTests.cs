using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-73: a CHECK constraint, an index predicate, and an index key expression are captured as the author wrote them. They used to be
/// re-rendered by a renderer that knew a few node kinds and dropped the rest (<c>a BETWEEN 1 AND 9</c> became <c>a BETWEEN 1, 9</c>,
/// <c>a IN (1, 2)</c> became <c>a = 1, 2</c>, a <c>CASE</c> became its first column), which changed what was deployed.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CheckAndIndexExpressionParsingTests
{
    [TestMethod]
    [DataRow("a BETWEEN 1 AND 9")]
    [DataRow("a IN (1, 2)")]
    [DataRow("a = ANY (ARRAY[1, 2])")]
    [DataRow("a > 0 AND a < 9")]
    [DataRow("(a > 0) AND (a < 9)")]
    [DataRow("CASE WHEN a > 0 THEN true ELSE false END")]
    [DataRow("(a > 0) IS TRUE")]
    [DataRow("coalesce(a, 0) > 0")]
    [DataRow("arr[1] > 0")]
    [DataRow("a IS DISTINCT FROM b")]
    [DataRow("n COLLATE \"C\" > 'a'")]
    [DataRow("n <> 'a)b,c'")]
    [DataRow("a>0   AND\n   b>0")]
    public void ACheckConditionIsKeptAsWritten(string condition)
    {
        var tree = new PgQueryParser().Parse($"CREATE TABLE t (a integer, b integer, n text, arr integer[], CONSTRAINT c CHECK ({condition}));");

        Assert.AreEqual(condition, tree.Constraints.Single().RawCheckExpression);
    }

    [TestMethod]
    public void EachCheckOfATableReadsItsOwnText()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE TABLE t (a integer CHECK (a BETWEEN 1 AND 9), b integer, CHECK (b IN (1, 2)), CONSTRAINT named CHECK (a > b AND b > 0) NO INHERIT);");

        CollectionAssert.AreEquivalent(
            new[] { "a BETWEEN 1 AND 9", "b IN (1, 2)", "a > b AND b > 0" },
            tree.Constraints.Select(static constraint => constraint.RawCheckExpression).ToArray());
    }

    [TestMethod]
    public void ACheckInALaterStatementIsReadFromItsOwnStatement()
    {
        // pg_query counts a position from the start of the whole input, so a statement after the first needs its offset removed.
        var tree = new PgQueryParser().Parse(
            "CREATE SCHEMA s;\n\n-- a comment with CHECK (x)\nCREATE TABLE s.t (a integer, CONSTRAINT c CHECK (a BETWEEN 1 AND 9));\nCREATE TABLE s.u (b integer, CONSTRAINT d CHECK (b IN (1, 2)));");

        Assert.AreEqual("a BETWEEN 1 AND 9", tree.Constraints.Single(static constraint => constraint.Name == "c").RawCheckExpression);
        Assert.AreEqual("b IN (1, 2)", tree.Constraints.Single(static constraint => constraint.Name == "d").RawCheckExpression);
    }

    [TestMethod]
    public void ACheckAddedByAlterTableIsKeptAsWritten()
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE t (a integer);\nALTER TABLE t ADD CONSTRAINT c CHECK (a BETWEEN 1 AND 9) NOT VALID, ADD CHECK (a IN (1, 2));");

        CollectionAssert.AreEquivalent(
            new[] { "a BETWEEN 1 AND 9", "a IN (1, 2)" },
            tree.Constraints.Select(static constraint => constraint.RawCheckExpression).ToArray());
    }

    [TestMethod]
    public void TheTreeOfACheckIsKeptForTheComparison()
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE t (a integer, CHECK (a > 0));");

        StringAssert.Contains(tree.Constraints.Single().CheckExpressionAst, "AEXPR_OP");
    }

    [TestMethod]
    [DataRow("CREATE INDEX i ON t (a) WHERE a > 0 AND b IS NOT NULL", "a > 0 AND b IS NOT NULL")]
    [DataRow("CREATE INDEX i ON t (a) WHERE a IN (1, 2);", "a IN (1, 2)")]
    [DataRow("CREATE INDEX i ON t (a) WITH (fillfactor = 70) WHERE n LIKE 'x%' OR a IS NULL", "n LIKE 'x%' OR a IS NULL")]
    [DataRow("CREATE UNIQUE INDEX i ON t USING btree (a, (b + 1)) INCLUDE (n) WHERE (a BETWEEN 1 AND 9)", "(a BETWEEN 1 AND 9)")]
    [DataRow("CREATE INDEX i ON t (a) WHERE n <> 'where (x)'", "n <> 'where (x)'")]
    public void AnIndexPredicateIsKeptAsWritten(string sql, string expected)
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE t (a integer, b integer, n text);\n" + sql);

        Assert.AreEqual(expected, tree.Indexes.Single().RawPredicateExpression);
        Assert.IsNotNull(tree.Indexes.Single().PredicateAst);
    }

    [TestMethod]
    public void AnIndexWithoutAPredicateHasNone()
    {
        var tree = new PgQueryParser().Parse("CREATE INDEX i ON t (a, (b + 1));");

        Assert.IsNull(tree.Indexes.Single().RawPredicateExpression);
        Assert.IsNull(tree.Indexes.Single().PredicateAst);
    }

    [TestMethod]
    public void IndexKeyExpressionsAreKeptAsWrittenAndColumnsHaveNone()
    {
        var tree = new PgQueryParser().Parse("CREATE INDEX i ON t (lower(n) text_pattern_ops, a, (b + 1) DESC, (CASE WHEN a > 0 THEN 1 ELSE 2 END), public.f(a, 'x,y'), (a BETWEEN 1 AND 2));");

        var elements = tree.Indexes.Single().KeyElements;
        CollectionAssert.AreEqual(
            new string?[] { "lower(n)", null, "b + 1", "CASE WHEN a > 0 THEN 1 ELSE 2 END", "public.f(a, 'x,y')", "a BETWEEN 1 AND 2" },
            elements.Select(static element => element.RawExpression).ToArray());
        Assert.IsNull(elements[1].ExpressionAst);
        Assert.IsNotNull(elements[0].ExpressionAst);
    }
}
