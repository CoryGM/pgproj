using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-309e: a diagnostic from a semantic pass has the file and range of what it is about, as a parser diagnostic does (G-123, G-308): the reference's own
/// text when it has one (the table a foreign key names, a column's type), else its statement without the <c>;</c>. A model read from a database has no file,
/// so its diagnostics have no place.
/// </summary>
[TestClass]
public sealed class SemanticDiagnosticPlaceTests
{
    private const string File = "/project/sql/a.sql";

    private static DatabaseModel Build(string sql) => new SemanticBuilder().Build([new PgQueryParser().Parse(sql) with { FilePath = File }]);

    private static SemanticLocation At(int line, int column, int endLine, int endColumn) => new(File, line) { Column = column, EndLine = endLine, EndColumn = endColumn };

    private static SemanticDiagnostic Find(DatabaseModel model, string text)
        => model.Diagnostics.First(diagnostic => diagnostic.Message.Contains(text, StringComparison.Ordinal));

    [TestMethod]
    public void AMissingTableAForeignKeyNamesIsPlacedAtTheName()
    {
        var model = Build("CREATE TABLE app.orders (\n  id int PRIMARY KEY,\n  customer_id int REFERENCES app.customer (id)\n);");

        Assert.AreEqual(At(3, 30, 3, 42), Find(model, "references missing foreign key table").Location, Find(model, "references missing foreign key table").Location?.ToString());
    }

    [TestMethod]
    public void AMissingTypeIsPlacedAtTheColumnsType()
    {
        var model = Build("CREATE TABLE app.t (\n  id int,\n  kind app.missing_type\n);");

        Assert.AreEqual(At(3, 8, 3, 24), Find(model, "Column 'app.t.kind' references missing type 'app.missing_type'").Location, Find(model, "Column 'app.t.kind' references missing type 'app.missing_type'").Location?.ToString());

        // G-326: the column reports the type it names; its table no longer reports it again (this assertion replaced the table report's place).
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Message.StartsWith("Table 'app.t' references missing type", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AMissingFunctionInADefaultIsPlacedAtItsColumn()
    {
        var model = Build("CREATE TABLE app.t (\n  id int,\n  total numeric DEFAULT app.missing_fn()\n);");

        Assert.AreEqual(At(3, 3, 3, 8), Find(model, "Column 'app.t.total' references missing function 'app.missing_fn'").Location, Find(model, "Column 'app.t.total' references missing function 'app.missing_fn'").Location?.ToString());
    }

    [TestMethod]
    public void AnIndexOnAMissingTableIsPlacedAtTheIndex()
    {
        var model = Build("CREATE TABLE app.t (id int);\nCREATE INDEX t_x ON app.nope (id);");

        var place = model.Diagnostics.First(static diagnostic => diagnostic.Message.StartsWith("Index 'app.t_x'", StringComparison.Ordinal)).Location;
        Assert.AreEqual(At(2, 1, 2, 34), place, place?.ToString());
    }

    [TestMethod]
    public void AMissingTableInAViewIsPlacedAtTheView()
    {
        var model = Build("CREATE TABLE app.t (id int);\n\nCREATE VIEW app.v AS\n  SELECT * FROM app.nowhere;");

        Assert.AreEqual(At(3, 1, 4, 28), Find(model, "View 'app.v' references missing table 'app.nowhere'").Location, Find(model, "View 'app.v' references missing table 'app.nowhere'").Location?.ToString());
    }

    [TestMethod]
    public void ADuplicateIsPlacedAtTheSecondDefinition()
    {
        var model = Build("CREATE TABLE app.t (id int);\nCREATE TABLE app.t (id int);");

        Assert.AreEqual(At(2, 1, 2, 28), Find(model, "Duplicate Table definition 'app.t'").Location, Find(model, "Duplicate Table definition 'app.t'").Location?.ToString());
    }

    [TestMethod]
    public void ANameFoundOnlyOffTheSearchPathIsPlacedWhereItIsWritten()
    {
        var tree = new PgQueryParser().Parse("CREATE SCHEMA shop;\nCREATE TYPE shop.mood AS ENUM ('ok');\nCREATE TABLE shop.t (id int, m mood);") with { FilePath = File };

        var model = new SemanticPipeline().Execute([tree]);

        var error = model.Diagnostics.Single(static diagnostic => diagnostic.Source == "SearchPathValidationPass");
        Assert.AreEqual(At(3, 32, 3, 36), error.Location, error.Location?.ToString());
    }

    [TestMethod]
    public void ACycleIsPlacedAtItsFirstObject()
    {
        var tree = new PgQueryParser().Parse("""
            CREATE FUNCTION public.a(n int) RETURNS int LANGUAGE sql AS $$ SELECT CASE WHEN n > 0 THEN public.b(n - 1) ELSE 0 END $$;
            CREATE FUNCTION public.b(n int) RETURNS int LANGUAGE sql AS $$ SELECT CASE WHEN n > 0 THEN public.a(n - 1) ELSE 1 END $$;
            """) with { FilePath = File };

        var model = new SemanticPipeline().Execute([tree]);

        var cycles = model.Diagnostics.Where(static diagnostic => diagnostic.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.IsNotEmpty(cycles);
        Assert.IsTrue(cycles.All(static cycle => cycle.Location is { FilePath: File, Column: 1, EndColumn: 121 }), string.Join(" | ", cycles.Select(static cycle => cycle.Location)));
    }

    [TestMethod]
    public void ATemplateCheckIsPlacedAtTheStatement()
    {
        var shared = new PgQueryParser().Parse("CREATE SCHEMA platform;");
        var template = new PgQueryParser("tenant").Parse("CREATE TABLE a (id int);\nCREATE TABLE platform.audit (id int);") with { FilePath = File, SearchPath = ["tenant", "platform", "public"] };

        var error = TenantTemplateCheck.Check([shared], [template], "tenant").Single();

        Assert.AreEqual(At(2, 1, 2, 37), error.Location, error.Location?.ToString());
    }

    [TestMethod]
    public void AStatementAfterAnotherInTheSameFileHasItsOwnLine()
    {
        var model = Build("CREATE TABLE app.ok (id int);\n-- a comment\n\nCREATE VIEW app.v AS SELECT * FROM app.nowhere;");

        Assert.AreEqual(At(4, 1, 4, 47), Find(model, "View 'app.v' references missing table").Location, Find(model, "View 'app.v' references missing table").Location?.ToString());
    }

    [TestMethod]
    public void AModelWithNoFileHasNoPlaces()
    {
        var model = new SemanticBuilder().Build([new PgQueryParser().Parse("CREATE VIEW app.v AS SELECT * FROM app.nowhere;")]);

        Assert.IsNull(Find(model, "references missing table").Location);
    }

    [TestMethod]
    public void ADefinitionsPlaceIsNotPartOfItsEqualityOrItsJson()
    {
        var first = Build("CREATE TABLE app.t (id int);").Tables.Values.Single();
        var second = new SemanticBuilder().Build([new PgQueryParser().Parse("\n\n  CREATE TABLE app.t (id int);") with { FilePath = "/other.sql" }]).Tables.Values.Single();

        Assert.AreNotEqual(first.Location, second.Location);
        Assert.AreEqual(first, first with { Location = second.Location });
        Assert.AreEqual(first.GetHashCode(), (first with { Location = second.Location }).GetHashCode());
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(first).Contains("Location", StringComparison.Ordinal));
    }
}
