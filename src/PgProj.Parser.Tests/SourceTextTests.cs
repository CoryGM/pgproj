using System.Text.Json;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-207: <see cref="SourceText"/> finds the author's own text for an expression in the parse tree, confirmed by parsing it again, so that
/// what is deployed is what the project wrote. These cover the expression shapes the old parse-tree renderer rebuilt wrongly (G-202,
/// G-208, G-209, G-210), and text the slice must survive: quotes, comments, parentheses, and characters outside ASCII.
/// </summary>
[TestClass]
public sealed class SourceTextTests
{
    [TestMethod]
    [DataRow("CASE WHEN a > 0 THEN 1 ELSE 2 END")]
    [DataRow("ARRAY[1,2]")]
    [DataRow("'{a,b}'::text[]")]
    [DataRow("current_user")]
    [DataRow("CURRENT_TIMESTAMP")]
    [DataRow("1.50::numeric(5,2)")]
    [DataRow("'{}'")]
    [DataRow("-1")]
    [DataRow("interval '1 day'")]
    [DataRow("coalesce(a, 'x')")]
    [DataRow("(a + 1) * 2")]
    [DataRow("a + (1 * 2)")]
    [DataRow("a IS NOT NULL")]
    [DataRow("b IS TRUE")]
    [DataRow("a BETWEEN 1 AND 2")]
    [DataRow("a = ANY (ARRAY[1, 2])")]
    [DataRow("lower(b) COLLATE \"C\"")]
    [DataRow("now() AT TIME ZONE 'UTC'")]
    [DataRow("EXISTS (SELECT 1 FROM pg_class)")]
    [DataRow("'it''s'")]
    [DataRow("$x$dollar ) quoted$x$")]
    [DataRow("'x'::\"My S\".\"My T\"")]
    [DataRow("ROW(1, 'a')")]
    [DataRow("a::double precision")]
    [DataRow("'é' || 'ü'")]
    public void AnExpressionIsReadAsWritten(string expression)
    {
        var sql = $"CREATE TABLE t (a int, b text, d int, CHECK ({expression}));";

        Assert.AreEqual(expression, Read(sql, "raw_expr", occurrence: 0));
    }

    [TestMethod]
    public void TheCommentsAndSpacingInsideAnExpressionAreKept()
    {
        const string expression = "CASE  WHEN a > 0 /* positive */ THEN 1\n    ELSE 2 END";

        Assert.AreEqual(expression, Read($"CREATE TABLE t (a int, c int DEFAULT {expression});", "raw_expr", occurrence: 0));
    }

    [TestMethod]
    public void TheParenthesesAroundAWholeExpressionAreLeftWithTheStatement()
    {
        Assert.AreEqual("a > 0", Read("CREATE TABLE t (a int, CHECK ((a > 0)));", "raw_expr", occurrence: 0));
    }

    [TestMethod]
    public void AnExpressionAfterTextOutsideAsciiIsFoundAtTheRightPlace()
    {
        Assert.AreEqual("CASE WHEN \"größe\" > 0 THEN 'ä' END", Read("CREATE TABLE \"tëst\" (\"größe\" int, c text DEFAULT CASE WHEN \"größe\" > 0 THEN 'ä' END);", "raw_expr", occurrence: 0));
    }

    [TestMethod]
    public void ARoutineParameterDefaultIsReadAsWritten()
    {
        Assert.AreEqual("ARRAY[1,2]", Read("CREATE FUNCTION f(a int[] DEFAULT ARRAY[1,2], b int DEFAULT 3) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;", "defexpr", occurrence: 0));
        Assert.AreEqual("3", Read("CREATE FUNCTION f(a int[] DEFAULT ARRAY[1,2], b int DEFAULT 3) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;", "defexpr", occurrence: 1));
    }

    [TestMethod]
    public void APublicationRowFilterIsReadAsWritten()
    {
        Assert.AreEqual(
            "CASE WHEN a > 0 THEN true ELSE false END AND b <> current_user",
            Read("CREATE PUBLICATION p FOR TABLE t WHERE (CASE WHEN a > 0 THEN true ELSE false END AND b <> current_user);", "whereClause", occurrence: 0));
    }

    [TestMethod]
    public void AStatisticsExpressionIsReadAsWritten()
    {
        Assert.AreEqual("CASE WHEN a > 0 THEN 1 ELSE 2 END", Read("CREATE STATISTICS s ON (CASE WHEN a > 0 THEN 1 ELSE 2 END), b FROM t;", "expr", occurrence: 0));
    }

    [TestMethod]
    public void AnExpressionInALaterStatementIsFoundFromItsStatementsOffset()
    {
        const string sql = "CREATE TABLE x (y int);\nCREATE TABLE t (a int, c int DEFAULT a + 1);";

        using var document = JsonDocument.Parse(new PgQueryParser().Parse(sql).RawJsonAst!);
        var statement = document.RootElement.GetProperty("stmts")[1];
        var offset = statement.GetProperty("stmt_location").GetInt32() + 1; // the statement's text starts after the newline
        var expression = Find(statement, "raw_expr").First();

        Assert.AreEqual("a + 1", SourceText.Read(sql[offset..], offset, expression));
    }

    [TestMethod]
    public void TextThatDoesNotMatchTheTreeIsNull()
    {
        using var document = JsonDocument.Parse(new PgQueryParser().Parse("CREATE TABLE t (a int, c int DEFAULT a + 1);").RawJsonAst!);
        var expression = Find(document.RootElement, "raw_expr").First();

        // The same positions in a different text: no candidate parses to the same tree.
        Assert.IsNull(SourceText.Read("CREATE TABLE t (a int, c int DEFAULT a - 9);", 0, expression));
    }

    [TestMethod]
    [DataRow("INT")]
    [DataRow("VARCHAR(10)")]
    [DataRow("character varying ( 10 )")]
    [DataRow("double precision")]
    [DataRow("timestamp(3) with time zone")]
    [DataRow("interval day to second(2)")]
    [DataRow("numeric(5,2)[]")]
    [DataRow("int ARRAY")]
    [DataRow("\"My S\".\"My T\"[]")]
    [DataRow("\"char\"")]
    [DataRow("public.mood")]
    [DataRow("bit varying(5)")]
    public void AColumnTypeIsReadAsWritten(string type)
    {
        var sql = $"CREATE TABLE t (a {type} NOT NULL, b int);";

        using var document = JsonDocument.Parse(new PgQueryParser().Parse(sql).RawJsonAst!);
        Assert.AreEqual(type, SourceText.ReadTypeName(sql, 0, Find(document.RootElement, "typeName").First()));
    }

    [TestMethod]
    [DataRow("RETURNS SETOF \"char\" LANGUAGE", "\"char\"")]
    [DataRow("RETURNS \"My S\".\"My T\" LANGUAGE", "\"My S\".\"My T\"")]
    [DataRow("RETURNS INTEGER LANGUAGE", "INTEGER")]
    public void AReturnTypeIsReadAsWrittenWithoutSetof(string returns, string type)
    {
        var sql = $"CREATE FUNCTION f() {returns} sql AS $$ SELECT 1 $$;";

        using var document = JsonDocument.Parse(new PgQueryParser().Parse(sql).RawJsonAst!);
        Assert.AreEqual(type, SourceText.ReadTypeName(sql, 0, Find(document.RootElement, "returnType").First()));
    }

    [TestMethod]
    public void TheLexerGivesTokenRangesInCharacters()
    {
        var tokens = SourceText.Scan("SELECT 'ä', $$b$$")!;
        var text = "SELECT 'ä', $$b$$";

        CollectionAssert.AreEqual(new[] { "SELECT", "'ä'", ",", "$$b$$" }, tokens.Select(token => text[token.Start..token.End]).ToArray());
    }

    private static string? Read(string sql, string propertyName, int occurrence)
    {
        using var document = JsonDocument.Parse(new PgQueryParser().Parse(sql).RawJsonAst!);
        var expression = Find(document.RootElement, propertyName).ElementAt(occurrence);
        return SourceText.Read(sql, 0, expression);
    }

    private static IEnumerable<JsonElement> Find(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == propertyName)
                {
                    yield return property.Value;
                }

                foreach (var found in Find(property.Value, propertyName))
                {
                    yield return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var found in Find(item, propertyName))
                {
                    yield return found;
                }
            }
        }
    }
}
