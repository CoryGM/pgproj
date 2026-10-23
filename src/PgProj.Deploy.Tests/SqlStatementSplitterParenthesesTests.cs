namespace PgProj.Deploy.Tests;

/// <summary>
/// G-230: as psql does, a semicolon inside parentheses, a quoted identifier, or an escape string is not a statement end. A rule with several actions,
/// <c>DO ALSO (INSERT ...; INSERT ...)</c>, was cut in two, so <c>publish --execute</c> failed with a syntax error.
/// </summary>
[TestClass]
public sealed class SqlStatementSplitterParenthesesTests
{
    [TestMethod]
    public void ARuleWithSeveralActionsIsOneStatement()
    {
        const string rule = "CREATE RULE r AS ON INSERT TO public.t DO ALSO (INSERT INTO public.log VALUES (new.a); INSERT INTO public.log VALUES (new.a + 1))";

        var result = SqlStatementSplitter.Split(rule + ";\nSELECT 1;");

        Assert.HasCount(2, result);
        Assert.AreEqual(rule, result[0]);
        Assert.AreEqual("SELECT 1", result[1]);
    }

    [TestMethod]
    public void ParenthesesInsideStringsAndCommentsAreNotCounted()
    {
        var result = SqlStatementSplitter.Split("SELECT '(' ; SELECT 2 /* ( */; SELECT 3 -- (\n; SELECT 4;");

        Assert.HasCount(4, result);
    }

    [TestMethod]
    public void AnUnbalancedClosingParenthesisDoesNotStopTheNextSplit()
    {
        var result = SqlStatementSplitter.Split("SELECT 1); SELECT 2;");

        Assert.HasCount(2, result);
    }

    [TestMethod]
    public void ASemicolonInAQuotedIdentifierIsPartOfTheName()
    {
        var result = SqlStatementSplitter.Split("CREATE TABLE \"a;b\" (\"x\"\"; y\" int); SELECT 1;");

        Assert.HasCount(2, result);
        Assert.AreEqual("CREATE TABLE \"a;b\" (\"x\"\"; y\" int)", result[0]);
    }

    [TestMethod]
    public void ABackslashEscapesTheQuoteInAnEscapeStringOnly()
    {
        var escaped = SqlStatementSplitter.Split("SELECT E'it\\'s; fine'; SELECT 2;");
        Assert.HasCount(2, escaped);
        Assert.AreEqual("SELECT E'it\\'s; fine'", escaped[0]);

        // In an ordinary string a backslash is a character, and the quote after it ends the string.
        var ordinary = SqlStatementSplitter.Split("SELECT 'a\\'; SELECT 2;");
        Assert.HasCount(2, ordinary);
    }

    [TestMethod]
    public void AWordEndingInEIsNotAnEscapeStringPrefix()
    {
        var result = SqlStatementSplitter.Split("SELECT name'a\\'; SELECT 2;");

        Assert.HasCount(2, result);
    }
}
