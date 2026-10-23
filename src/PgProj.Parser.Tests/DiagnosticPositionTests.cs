namespace PgProj.Parser.Tests;

/// <summary>
/// G-123: a syntax error is reported once, at the line and column of the parsed text it is on, taken from libpg_query's cursor
/// position. It used to say <c>line 1262</c> (the line of libpg_query's own C source), twice. An unmodeled statement's warning
/// carries the column of its first word too.
/// </summary>
[TestClass]
public sealed class DiagnosticPositionTests
{
    [TestMethod]
    public void ASyntaxErrorIsReportedOnceAtItsLineAndColumn()
    {
        // Arrange
        const string sql = "-- b\nCREATE TABLE public.a (x int);\nCREATE TABLE public.b (y int,, z int);\n";

        // Act
        var errors = new PgQueryParser().Parse(sql).Diagnostics.Where(static d => d.Severity == SqlDiagnosticSeverity.Error).ToArray();

        // Assert
        var error = errors.Single();
        Assert.AreEqual("syntax error at or near \",\"", error.Message);
        Assert.AreEqual(3, error.LineNumber);
        Assert.AreEqual(30, error.Column);
        Assert.AreEqual(",", sql[(error.CursorPosition!.Value - 1)..error.CursorPosition.Value]);
    }

    [TestMethod]
    public void AnErrorAtTheEndOfTheTextIsPlacedJustPastIt()
    {
        // Arrange
        const string sql = "CREATE TABLE public.a (\n  x int";

        // Act
        var error = new PgQueryParser().Parse(sql).Diagnostics.Single(static d => d.Severity == SqlDiagnosticSeverity.Error);

        // Assert
        Assert.AreEqual("syntax error at end of input", error.Message);
        Assert.AreEqual(2, error.LineNumber);
        Assert.AreEqual(8, error.Column);
    }

    [TestMethod]
    public void AnUnmodeledStatementsWarningCarriesItsColumn()
    {
        // Arrange
        const string sql = "CREATE TABLE public.d (x int);\n\n  INSERT INTO public.d VALUES (1);\n";

        // Act
        var warning = new PgQueryParser().Parse(sql).Diagnostics.Single();

        // Assert
        Assert.AreEqual(3, warning.LineNumber);
        Assert.AreEqual(3, warning.Column);
    }

    [TestMethod]
    [DataRow(1, 1, 1)]
    [DataRow(3, 1, 3)]
    [DataRow(4, 1, 4)]
    [DataRow(5, 2, 1)]
    [DataRow(7, 2, 3)]
    public void ACharacterPositionIsALineAndColumn(int position, int line, int column)
    {
        // Arrange: "a😀b" is three characters, the middle one two UTF-16 units; PostgreSQL counts characters.
        const string sql = "a😀b\nxyz";

        // Act
        var located = SqlTextPosition.OfCharacter(sql, position);

        // Assert
        Assert.AreEqual((line, column), located);
    }

    [TestMethod]
    public void APositionOutsideTheTextHasNoLine()
    {
        Assert.IsNull(SqlTextPosition.OfCharacter("abc", 0));
        Assert.IsNull(SqlTextPosition.OfCharacter("abc", 5));
    }

    [TestMethod]
    public void AnIndexIsALineAndColumnInCharacters()
    {
        // Arrange: index 6 is 'x', after "a😀b\n" (five UTF-16 units, four characters).
        const string sql = "a😀b\nxyz";

        // Act
        var located = SqlTextPosition.OfIndex(sql, sql.IndexOf('y', StringComparison.Ordinal));

        // Assert
        Assert.AreEqual((2, 2), located);
    }
}
