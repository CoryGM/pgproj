namespace PgProj.Parser.Tests;

/// <summary>
/// G-308: a diagnostic that has a start (line and column) also has an end, so an editor can underline the range. The end is exclusive: the
/// line and column just past the last character, so a one-character error at 3:30 ends at 3:31 and a zero-width one ends where it starts.
/// </summary>
[TestClass]
public sealed class DiagnosticRangeTests
{
    private static SqlDiagnostic[] Errors(string sql)
        => new PgQueryParser().Parse(sql).Diagnostics.Where(static d => d.Severity == SqlDiagnosticSeverity.Error).ToArray();

    private static void AssertRange(SqlDiagnostic diagnostic, int line, int column, int endLine, int endColumn)
    {
        Assert.AreEqual((line, column, endLine, endColumn), (diagnostic.LineNumber, diagnostic.Column, diagnostic.EndLine, diagnostic.EndColumn));
    }

    [TestMethod]
    public void ASyntaxErrorEndsWhereItsTokenEnds()
    {
        // Arrange
        const string sql = "-- b\nCREATE TABLE public.a (x int);\nCREATE TABLE public.b (y int,, z int);\n";

        // Act
        var error = Errors(sql).Single();

        // Assert: the second comma, one character wide.
        AssertRange(error, 3, 30, 3, 31);
    }

    [TestMethod]
    public void AWordIsCoveredWhole()
    {
        var error = Errors("SELEKT 1;").Single();

        StringAssert.Contains(error.Message, "SELEKT");
        AssertRange(error, 1, 1, 1, 7);
    }

    [TestMethod]
    public void AQuotedNameIsCoveredWithItsQuotes()
    {
        var error = Errors("CREATE TABLE a (\"x y\" int \"z w\");").Single();

        AssertRange(error, 1, 27, 1, 32);
    }

    [TestMethod]
    public void AnErrorAtTheEndOfTheTextIsZeroWidth()
    {
        var error = Errors("CREATE TABLE public.a (\n  x int").Single();

        AssertRange(error, 2, 8, 2, 8);
    }

    [TestMethod]
    public void AnUnterminatedStringRunsToTheEndOfTheText()
    {
        // The lexer cannot read the text, and a string with no closing quote runs to the end of it.
        var error = Errors("SELECT 'abc\nmore").Single();

        StringAssert.Contains(error.Message, "unterminated");
        AssertRange(error, 1, 8, 2, 5);
    }

    [TestMethod]
    public void AColumnAfterACharacterBeyondTheBasicPlaneCountsCharacters()
    {
        // Arrange: the emoji is one character and two UTF-16 units, and PostgreSQL and the editor count characters.
        const string sql = "SELECT '😀', , 1;";

        // Act
        var error = Errors(sql).Single();

        // Assert: the second comma is the 13th character.
        AssertRange(error, 1, 13, 1, 14);
    }

    [TestMethod]
    public void AnUnmodeledStatementsWarningCoversTheStatementWithoutItsSemicolon()
    {
        // Arrange: the statement runs over two lines, and a statement follows it.
        const string sql = "CREATE TABLE public.d (x int);\n\n  INSERT INTO public.d\n    VALUES (1);\nCREATE TABLE public.e (y int);\n";

        // Act
        var warning = new PgQueryParser().Parse(sql).Diagnostics.Single();

        // Assert
        AssertRange(warning, 3, 3, 4, 15);
    }

    [TestMethod]
    public void ALastStatementWithNoSemicolonEndsAtItsLastCharacter()
    {
        var warning = new PgQueryParser().Parse("INSERT INTO public.d VALUES (1)  \n").Diagnostics.Single();

        AssertRange(warning, 1, 1, 1, 32);
    }

    [TestMethod]
    public void ALastStatementWithASemicolonEndsBeforeIt()
    {
        var warning = new PgQueryParser().Parse("INSERT INTO public.d VALUES (1);\n").Diagnostics.Single();

        AssertRange(warning, 1, 1, 1, 32);
    }

    [TestMethod]
    public void AnUnreadClauseWarningCoversItsStatement()
    {
        // The warning for a clause of a modeled statement that pgproj does not read (G-227) is placed like the unmodeled statement's.
        var warnings = new PgQueryParser().Parse("CREATE TABLE public.t (a int) ON COMMIT DROP;\n").Diagnostics.ToArray();

        var warning = warnings.Single();
        Assert.AreEqual(1, warning.LineNumber);
        Assert.AreEqual(1, warning.Column);
        Assert.AreEqual(1, warning.EndLine);
        Assert.AreEqual(45, warning.EndColumn);
    }

    [TestMethod]
    [DataRow(1, 0)]
    [DataRow(2, 1)]
    [DataRow(3, 3)]
    [DataRow(4, 4)]
    [DataRow(7, 7)]
    [DataRow(8, 8)]
    public void ACharacterPositionIsAUtf16Index(int position, int index)
    {
        // Arrange: "a😀b\nxyz" is seven characters and eight UTF-16 units; the emoji (character 2) takes the units 1 and 2.
        const string sql = "a😀b\nxyz";

        // Act
        var found = SqlTextPosition.IndexOfCharacter(sql, position);

        // Assert
        Assert.AreEqual(index, found);
    }

    [TestMethod]
    public void TheEndOfTheTextIsAPositionAndAnythingPastItIsNot()
    {
        Assert.AreEqual(3, SqlTextPosition.IndexOfCharacter("abc", 4));
        Assert.IsNull(SqlTextPosition.IndexOfCharacter("abc", 5));
        Assert.IsNull(SqlTextPosition.IndexOfCharacter("abc", 0));
    }

    [TestMethod]
    [DataRow("SELECT 1;", 0, 9, 8)]
    [DataRow("SELECT 1 ;  \n", 0, 13, 8)]
    [DataRow("SELECT 1", 0, 8, 8)]
    [DataRow("SELECT 1;;", 0, 10, 9)]
    [DataRow("  ", 2, 2, 2)]
    public void AStatementEndsBeforeItsSemicolonAndTheWhitespaceAroundIt(string sql, int start, int end, int expected)
    {
        Assert.AreEqual(expected, UnmodeledStatementReader.TrimStatementEnd(sql, start, end));
    }
}
