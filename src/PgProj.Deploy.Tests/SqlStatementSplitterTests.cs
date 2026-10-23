using PgProj.Deploy;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class SqlStatementSplitterTests
{
    // ── Basic splitting ─────────────────────────────────────────────────────────

    [TestMethod]
    public void SingleStatementWithSemicolonIsReturned()
    {
        var result = SqlStatementSplitter.Split("SELECT 1;");

        Assert.HasCount(1, result);
        Assert.AreEqual("SELECT 1", result[0]);
    }

    [TestMethod]
    public void MultipleStatementsAreSplitOnSemicolon()
    {
        var result = SqlStatementSplitter.Split("SELECT 1; SELECT 2; SELECT 3;");

        Assert.HasCount(3, result);
        Assert.AreEqual("SELECT 1", result[0]);
        Assert.AreEqual("SELECT 2", result[1]);
        Assert.AreEqual("SELECT 3", result[2]);
    }

    [TestMethod]
    public void EmptyInputReturnsEmptyList()
    {
        var result = SqlStatementSplitter.Split(string.Empty);

        Assert.HasCount(0, result);
    }

    [TestMethod]
    public void WhitespaceOnlyInputReturnsEmptyList()
    {
        var result = SqlStatementSplitter.Split("   \n\t  ");

        Assert.HasCount(0, result);
    }

    [TestMethod]
    public void StatementsAreTrimmed()
    {
        var result = SqlStatementSplitter.Split("  CREATE TABLE t (id int);  ");

        Assert.HasCount(1, result);
        Assert.AreEqual("CREATE TABLE t (id int)", result[0]);
    }

    [TestMethod]
    public void TrailingStatementWithoutSemicolonIsIncluded()
    {
        var result = SqlStatementSplitter.Split("SELECT 1; SELECT 2");

        Assert.HasCount(2, result);
        Assert.AreEqual("SELECT 2", result[1]);
    }

    [TestMethod]
    public void EmptyStatementBetweenSemicolonsIsExcluded()
    {
        var result = SqlStatementSplitter.Split("SELECT 1;; SELECT 2;");

        Assert.HasCount(2, result);
    }

    // ── Line comments ───────────────────────────────────────────────────────────

    [TestMethod]
    public void LineCommentDoesNotSplitStatement()
    {
        var sql = "-- this is a comment\nSELECT 1;";
        var result = SqlStatementSplitter.Split(sql);

        Assert.HasCount(1, result);
        Assert.IsTrue(result[0].Contains("SELECT 1"));
    }

    [TestMethod]
    public void SemicolonInsideLineCommentIsIgnored()
    {
        var sql = "SELECT 1 -- this; is a comment\n;";
        var result = SqlStatementSplitter.Split(sql);

        Assert.HasCount(1, result);
    }

    // ── Block comments ──────────────────────────────────────────────────────────

    [TestMethod]
    public void SemicolonInsideBlockCommentIsIgnored()
    {
        var sql = "SELECT 1 /* this; is a comment */ ;";
        var result = SqlStatementSplitter.Split(sql);

        Assert.HasCount(1, result);
    }

    // ── Single-quoted string literals ───────────────────────────────────────────

    [TestMethod]
    public void SemicolonInsideStringLiteralIsIgnored()
    {
        var sql = "SELECT 'hello; world';";
        var result = SqlStatementSplitter.Split(sql);

        Assert.HasCount(1, result);
        Assert.AreEqual("SELECT 'hello; world'", result[0]);
    }

    [TestMethod]
    public void EscapedQuoteInsideStringLiteralIsHandled()
    {
        var sql = "SELECT 'it''s fine';";
        var result = SqlStatementSplitter.Split(sql);

        Assert.HasCount(1, result);
        Assert.AreEqual("SELECT 'it''s fine'", result[0]);
    }

    // ── Dollar quoting ──────────────────────────────────────────────────────────

    [TestMethod]
    public void SemicolonInsideDollarQuotedBodyIsIgnored()
    {
        var sql =
            """
            CREATE FUNCTION f() RETURNS void LANGUAGE plpgsql AS $$
            BEGIN
              RAISE NOTICE 'hello; world';
            END;
            $$;
            """;

        var result = SqlStatementSplitter.Split(sql);

        Assert.HasCount(1, result);
        StringAssert.Contains(result[0], "BEGIN");
        StringAssert.Contains(result[0], "$$");
    }

    [TestMethod]
    public void SemicolonInsideTaggedDollarQuoteIsIgnored()
    {
        var sql =
            """
            CREATE FUNCTION f() RETURNS void LANGUAGE plpgsql AS $body$
            BEGIN
              PERFORM 1; -- internal semicolon
            END;
            $body$;
            """;

        var result = SqlStatementSplitter.Split(sql);

        Assert.HasCount(1, result);
        StringAssert.Contains(result[0], "$body$");
    }

    [TestMethod]
    public void TwoDollarQuotedFunctionsAreSplitCorrectly()
    {
        var sql =
            """
            CREATE FUNCTION f1() RETURNS void LANGUAGE sql AS $$SELECT 1;$$;
            CREATE FUNCTION f2() RETURNS void LANGUAGE sql AS $$SELECT 2;$$;
            """;

        var result = SqlStatementSplitter.Split(sql);

        Assert.HasCount(2, result);
        StringAssert.Contains(result[0], "f1");
        StringAssert.Contains(result[1], "f2");
    }

    [TestMethod]
    public void DollarSignInNonTagContextIsNotMistaken()
    {
        // $1 is a parameter placeholder — not a dollar-quote tag.
        var sql = "SELECT $1::int; SELECT $2::text;";
        var result = SqlStatementSplitter.Split(sql);

        Assert.HasCount(2, result);
    }

    // ── Real DDL shapes ─────────────────────────────────────────────────────────

    [TestMethod]
    public void CreateTableAndCreateIndexAreSplitCorrectly()
    {
        var sql =
            """
            CREATE TABLE public.orders (
                id integer GENERATED ALWAYS AS IDENTITY,
                name text NOT NULL
            );
            CREATE INDEX idx_orders_name ON public.orders (name);
            """;

        var result = SqlStatementSplitter.Split(sql);

        Assert.HasCount(2, result);
        StringAssert.Contains(result[0], "CREATE TABLE");
        StringAssert.Contains(result[1], "CREATE INDEX");
    }

    [TestMethod]
    public void CreateSequenceAndCreateTableAreSplitCorrectly()
    {
        var sql =
            """
            CREATE SEQUENCE public.orders_id_seq OWNED BY public.orders.id;
            CREATE TABLE public.orders (id integer);
            """;

        var result = SqlStatementSplitter.Split(sql);

        Assert.HasCount(2, result);
        StringAssert.Contains(result[0], "CREATE SEQUENCE");
        StringAssert.Contains(result[1], "CREATE TABLE");
    }
}
