using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-326: a type the project does not define was reported by both <c>TypeResolutionPass</c> and <c>SemanticBinder</c>, with the same words, and for a
/// column's type by its table as well: four warnings for one column. Each owner that names a missing type now reports it once, and a table does not
/// repeat its columns' types.
/// </summary>
[TestClass]
public sealed class MissingTypeReportTests
{
    [TestMethod]
    [DataRow("CREATE TABLE app.t (id int, kind app.missing_type);", "Column 'app.t.kind'", DisplayName = "column")]
    [DataRow("CREATE TABLE app.t (id int, kinds app.missing_type[]);", "Column 'app.t.kinds'", DisplayName = "array column")]
    [DataRow("CREATE FUNCTION app.f(x app.missing_type) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;", "Function 'app.f(app.missing_type)'", DisplayName = "function parameter")]
    [DataRow("CREATE DOMAIN app.d AS app.missing_type;", "Type 'app.d'", DisplayName = "domain")]
    [DataRow("CREATE TYPE app.c AS (a app.missing_type);", "Type 'app.c'", DisplayName = "composite field")]
    [DataRow("CREATE VIEW app.v AS SELECT NULL::app.missing_type AS a;", "View 'app.v'", DisplayName = "view")]
    public void AMissingTypeIsReportedOnceByWhatNamesIt(string sql, string owner)
    {
        var reported = Reports(Build(sql));

        Assert.HasCount(1, reported, string.Join("\n", reported.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual($"{owner} references missing type 'app.missing_type'.", reported[0].Message);
    }

    [TestMethod]
    public void TwoColumnsOfAMissingTypeAreTwoReportsAndTheTableNone()
    {
        var reported = Reports(Build("CREATE TABLE app.t (a app.missing_type, b app.missing_type);"));

        CollectionAssert.AreEqual(
            new[] { "Column 'app.t.a' references missing type 'app.missing_type'.", "Column 'app.t.b' references missing type 'app.missing_type'." },
            reported.Select(static diagnostic => diagnostic.Message).ToArray());
    }

    [TestMethod]
    public void ATypedTablesOwnTypeIsTheTables()
    {
        // A typed table names its type itself, not through a column, so the table reports it (once), beside the error that its columns cannot be known.
        var model = Build("CREATE TABLE app.t OF app.missing_type;");

        var reported = Reports(model);
        Assert.HasCount(1, reported);
        Assert.AreEqual("Table 'app.t' references missing type 'app.missing_type'.", reported[0].Message);
        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error && diagnostic.Message.StartsWith("Typed table 'app.t'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void WithSchemasItIsOneError()
    {
        var model = new SemanticBuilder().Build([new PgQueryParser().Parse("CREATE TABLE app.t (id int, kind app.missing_type);")], ["app"]);

        var reported = Reports(model);
        Assert.HasCount(1, reported);
        Assert.AreEqual(SemanticDiagnosticSeverity.Error, reported[0].Severity);
    }

    private static SemanticDiagnostic[] Reports(DatabaseModel model)
        => [.. model.Diagnostics.Where(static diagnostic => diagnostic.Message.Contains(" references missing type ", StringComparison.Ordinal))];

    private static DatabaseModel Build(string sql) => new SemanticBuilder().Build([new PgQueryParser().Parse(sql)]);
}
