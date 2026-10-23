using System.Text.Json;
using PgProj.Diff;
using PgProj.Engine;
using PgProj.Semantics;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-123: a diagnostic from a project file is printed compiler style, <c>file:line:column: [Severity] message</c> (the user's choice),
/// with the file relative to the current directory when it is under it; <c>diff --format json</c> gives the same place as
/// <c>file</c>/<c>line</c>/<c>column</c>. G-308: when the end is known the place is a range,
/// <c>file:line:column-endLine:endColumn:</c>, and the JSON adds <c>endLine</c>/<c>endColumn</c>.
/// </summary>
[TestClass]
public sealed class DiagnosticFormatTests
{
    private static readonly ChangeSet Empty = new() { Added = [], Removed = [], Modified = [] };

    [TestMethod]
    public void AFileLineAndColumnComeFirst()
    {
        var file = Path.Combine(Environment.CurrentDirectory, "sql", "b.sql");
        var diagnostic = SemanticDiagnostics.Error("syntax error at or near \",\"", new SemanticLocation(file, 3) { Column = 30 });

        Assert.AreEqual($"{Path.Combine("sql", "b.sql")}:3:30: [Error] syntax error at or near \",\"", CliContext.FormatDiagnostic(diagnostic));
    }

    [TestMethod]
    public void AnEndMakesThePlaceARange()
    {
        // G-308 (the user's choice): file:line:column-endLine:endColumn, the end just past the last character.
        var file = Path.Combine(Environment.CurrentDirectory, "sql", "b.sql");
        var oneLine = SemanticDiagnostics.Error("bad", new SemanticLocation(file, 3) { Column = 30, EndLine = 3, EndColumn = 31 });
        var severalLines = SemanticDiagnostics.Warning("look", new SemanticLocation(file, 12) { Column = 1, EndLine = 14, EndColumn = 2 });

        Assert.AreEqual($"{Path.Combine("sql", "b.sql")}:3:30-3:31: [Error] bad", CliContext.FormatDiagnostic(oneLine));
        Assert.AreEqual($"{Path.Combine("sql", "b.sql")}:12:1-14:2: [Warning] look", CliContext.FormatDiagnostic(severalLines));
    }

    [TestMethod]
    public void AnEndWithoutAStartIsNotShown()
    {
        var file = Path.Combine(Environment.CurrentDirectory, "b.sql");

        Assert.AreEqual("b.sql:7: [Warning] look", CliContext.FormatDiagnostic(SemanticDiagnostics.Warning("look", new SemanticLocation(file, 7) { EndLine = 7, EndColumn = 9 })));
        Assert.AreEqual("b.sql:7:2: [Warning] look", CliContext.FormatDiagnostic(SemanticDiagnostics.Warning("look", new SemanticLocation(file, 7) { Column = 2, EndLine = 7 })));
    }

    [TestMethod]
    public void WithoutAColumnTheLineIsGiven()
    {
        var file = Path.Combine(Environment.CurrentDirectory, "b.sql");
        var diagnostic = SemanticDiagnostics.Warning("look", new SemanticLocation(file, 7));

        Assert.AreEqual("b.sql:7: [Warning] look", CliContext.FormatDiagnostic(diagnostic));
    }

    [TestMethod]
    public void WithoutALineTheFileIsGiven()
    {
        var file = Path.Combine(Environment.CurrentDirectory, "b.sql");
        var diagnostic = SemanticDiagnostics.Warning("look", new SemanticLocation(file, null));

        Assert.AreEqual("b.sql: [Warning] look", CliContext.FormatDiagnostic(diagnostic));
    }

    [TestMethod]
    public void AFileOutsideTheCurrentDirectoryIsGivenInFull()
    {
        var file = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "elsewhere", "b.sql"));
        var diagnostic = SemanticDiagnostics.Warning("look", new SemanticLocation(file, 2) { Column = 1 });

        Assert.AreEqual($"{file}:2:1: [Warning] look", CliContext.FormatDiagnostic(diagnostic));
    }

    [TestMethod]
    public void ADiagnosticFromNoFileKeepsTheEarlierForm()
    {
        Assert.AreEqual("[Warning] look line 4", CliContext.FormatDiagnostic(SemanticDiagnostics.Warning("look", new SemanticLocation(null, 4))));
        Assert.AreEqual("[Error] look", CliContext.FormatDiagnostic(SemanticDiagnostics.Error("look")));
    }

    [TestMethod]
    public void JsonCarriesTheFileLineAndColumn()
    {
        var file = Path.Combine(Environment.CurrentDirectory, "b.sql");
        SemanticDiagnostic[] diagnostics = [SemanticDiagnostics.Error("bad", new SemanticLocation(file, 3) { Column = 30 })];
        var writer = new StringWriter();

        CompareJson.Write(Empty, diagnostics, writer);

        using var json = JsonDocument.Parse(writer.ToString());
        var diagnostic = json.RootElement.GetProperty("diagnostics")[0];
        Assert.AreEqual("b.sql", diagnostic.GetProperty("file").GetString());
        Assert.AreEqual(3, diagnostic.GetProperty("line").GetInt32());
        Assert.AreEqual(30, diagnostic.GetProperty("column").GetInt32());
    }

    [TestMethod]
    public void JsonCarriesTheEndToo()
    {
        var file = Path.Combine(Environment.CurrentDirectory, "b.sql");
        SemanticDiagnostic[] diagnostics = [SemanticDiagnostics.Error("bad", new SemanticLocation(file, 3) { Column = 30, EndLine = 4, EndColumn = 2 })];
        var writer = new StringWriter();

        CompareJson.Write(Empty, diagnostics, writer);

        using var json = JsonDocument.Parse(writer.ToString());
        var diagnostic = json.RootElement.GetProperty("diagnostics")[0];
        Assert.AreEqual(30, diagnostic.GetProperty("column").GetInt32());
        Assert.AreEqual(4, diagnostic.GetProperty("endLine").GetInt32());
        Assert.AreEqual(2, diagnostic.GetProperty("endColumn").GetInt32());
    }

    [TestMethod]
    public void JsonLeavesOutAnEndItDoesNotKnow()
    {
        var file = Path.Combine(Environment.CurrentDirectory, "b.sql");
        var writer = new StringWriter();

        CompareJson.Write(Empty, [SemanticDiagnostics.Error("bad", new SemanticLocation(file, 3) { Column = 30 })], writer);

        using var json = JsonDocument.Parse(writer.ToString());
        var diagnostic = json.RootElement.GetProperty("diagnostics")[0];
        Assert.IsFalse(diagnostic.TryGetProperty("endLine", out _));
        Assert.IsFalse(diagnostic.TryGetProperty("endColumn", out _));
    }

    [TestMethod]
    public void JsonLeavesOutAPlaceItDoesNotKnow()
    {
        var writer = new StringWriter();

        CompareJson.Write(Empty, [SemanticDiagnostics.Warning("look")], writer);

        using var json = JsonDocument.Parse(writer.ToString());
        var diagnostic = json.RootElement.GetProperty("diagnostics")[0];
        Assert.IsFalse(diagnostic.TryGetProperty("file", out _));
        Assert.IsFalse(diagnostic.TryGetProperty("line", out _));
        Assert.IsFalse(diagnostic.TryGetProperty("endLine", out _));
    }

    [TestMethod]
    public async Task AProjectsSyntaxErrorNamesItsFile()
    {
        // Arrange
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g123-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql", "SCHEMA"));
        try
        {
            File.WriteAllText(Path.Combine(projectDirectory, "pgproj.json"), """{ "name": "g123", "version": "1.0", "source": "./sql" }""");
            File.WriteAllText(Path.Combine(projectDirectory, "sql", "a.sql"), "CREATE TABLE public.a (x int);\n");
            File.WriteAllText(Path.Combine(projectDirectory, "sql", "SCHEMA", "b.sql"), "-- b\n\nCREATE TABLE public.b (y int,, z int);\n");
            var context = new CliContext();

            // Act
            var model = (await new PgProjEngine().BuildAsync(new BuildRequest { ProjectFilePath = Path.Combine(projectDirectory, "pgproj.json") })).Model!;
            var output = new StringWriter();
            context.WriteDiagnostics(model, output);

            // Assert
            var error = model.Diagnostics.Single(static d => d.Severity == SemanticDiagnosticSeverity.Error);
            Assert.AreEqual(Path.Combine(projectDirectory, "sql", "SCHEMA", "b.sql"), error.Location?.FilePath);
            Assert.AreEqual(3, error.Location?.Line);
            Assert.AreEqual(30, error.Location?.Column);
            Assert.AreEqual(3, error.Location?.EndLine);
            Assert.AreEqual(31, error.Location?.EndColumn);
            StringAssert.Contains(output.ToString(), $"{Path.Combine("SCHEMA", "b.sql")}:3:30-3:31: [Error] syntax error at or near \",\"");
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }
}
