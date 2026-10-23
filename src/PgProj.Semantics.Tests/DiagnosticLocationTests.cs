using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>G-123: a parser diagnostic keeps the file it came from, and its line and column, in the model. G-308: and where it ends.</summary>
[TestClass]
public sealed class DiagnosticLocationTests
{
    [TestMethod]
    public void AParserDiagnosticKeepsItsFileLineColumnAndEnd()
    {
        // Arrange
        var tree = new PgQueryParser().Parse("CREATE TABLE public.b (y int,, z int);") with { FilePath = "/project/sql/b.sql" };

        // Act
        var model = new SemanticBuilder().Build([tree]);

        // Assert: the second comma, one character wide, so the end is the next column.
        var error = model.Diagnostics.Single(static d => d.Severity == SemanticDiagnosticSeverity.Error);
        Assert.AreEqual(new SemanticLocation("/project/sql/b.sql", 1) { Column = 30, EndLine = 1, EndColumn = 31 }, error.Location);
    }

    [TestMethod]
    public void ATreeFromNoFileHasNoFile()
    {
        var model = new SemanticBuilder().Build([new PgQueryParser().Parse("CREATE TABLE public.b (y int,, z int);")]);

        Assert.IsNull(model.Diagnostics.Single(static d => d.Severity == SemanticDiagnosticSeverity.Error).Location?.FilePath);
    }
}
