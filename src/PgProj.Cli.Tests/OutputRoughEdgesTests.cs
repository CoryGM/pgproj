using PgProj.Engine;
using System.Text.Json;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-285: rough edges in what pgproj prints. <c>diff</c> named properties as the model does (<c>DefaultExpressionNode</c>, <c>SelectExpressionNode</c>,
/// <c>ModifiedColumns</c>), in its text and its JSON; a function many objects call was warned about once per object (AdventureWorks: 87 lines for
/// <c>uuid_generate_v1</c>); and a malformed connection string gave Npgsql's <c>Couldn't set port (Parameter 'port')</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OutputRoughEdgesTests
{
    [TestMethod]
    public void TextAndJsonUseTheLabelsAndNoModelName()
    {
        var changeSet = Diff(
            "CREATE TABLE public.t (id int, status text DEFAULT 'new'); CREATE VIEW public.v AS SELECT id FROM public.t;",
            "CREATE TABLE public.t (id int, status text DEFAULT 'active'); CREATE VIEW public.v AS SELECT id, status FROM public.t;");

        var text = Text(changeSet);
        StringAssert.Contains(text, "      Default: ");
        StringAssert.Contains(text, "      Columns changed: ");
        StringAssert.Contains(text, "      Query: ");
        using var json = Json(changeSet);
        var properties = json.RootElement.GetProperty("modified").EnumerateArray()
            .SelectMany(static change => change.GetProperty("changes").EnumerateArray())
            .Select(static change => change.GetProperty("property").GetString()!)
            .ToArray();
        CollectionAssert.Contains(properties, "Default");
        CollectionAssert.Contains(properties, "Query");
        foreach (var modelName in new[] { "ExpressionNode", "ModifiedColumns", "Normalized", "ServerRendered", "Raw" })
        {
            Assert.DoesNotContain(modelName, text, StringComparison.Ordinal);
            Assert.IsFalse(properties.Any(property => property.Contains(modelName, StringComparison.Ordinal)), modelName);
        }
    }

    [TestMethod]
    public void DiffsHelpSaysHowPropertiesAreNamed()
    {
        StringAssert.Contains(DiffCommand.Create(new CliContext()).Description, "named as they are written: Default, Query, Columns changed");
    }

    [TestMethod]
    public void TheChangeSetKeepsTheModelsNames()
    {
        var changeSet = Diff("CREATE TABLE public.t (a int DEFAULT 1);", "CREATE TABLE public.t (a int DEFAULT 2);");

        Assert.IsTrue(changeSet.Modified.SelectMany(static change => change.Details).Any(static detail => detail.PropertyName == nameof(ColumnDefinition.DefaultExpressionNode)));
    }

    [TestMethod]
    public void AMissingFunctionManyObjectsCallIsOneWarningWithACount()
    {
        var diagnostics = Enumerable.Range(1, 5)
            .Select(static i => SemanticDiagnostics.Warning($"Column 'public.t.c{i}' references missing function 'uuid_generate_v1'."))
            .Append(SemanticDiagnostics.Warning("Column 'public.t.d' references missing function 'other'."))
            .Append(SemanticDiagnostics.Warning("something else"))
            .ToArray();

        var written = DiagnosticGrouping.ForOutput(diagnostics);

        Assert.HasCount(3, written);
        Assert.AreEqual(
            "5 objects reference missing function 'uuid_generate_v1': Column 'public.t.c1', Column 'public.t.c2', Column 'public.t.c3', and 2 more.",
            written[0].Message);
        Assert.AreEqual("Column 'public.t.d' references missing function 'other'.", written[1].Message, "A function one object calls is warned about as before.");
        Assert.AreEqual("something else", written[2].Message);
    }

    [TestMethod]
    public void AMissingFunctionErrorIsNeverMerged()
    {
        var diagnostics = new[]
        {
            SemanticDiagnostics.Error("Column 'public.t.a' references missing function 'f'."),
            SemanticDiagnostics.Error("Column 'public.t.b' references missing function 'f'."),
        };

        Assert.HasCount(2, DiagnosticGrouping.ForOutput(diagnostics));
    }

    [TestMethod]
    public void BuildWritesTheMergedWarning()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(
            "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\"; CREATE TABLE public.t (a uuid DEFAULT uuid_generate_v1(), b uuid DEFAULT uuid_generate_v1(), c uuid DEFAULT uuid_generate_v1());")]);
        var writer = new StringWriter();

        new CliContext().WriteDiagnostics(model, writer);

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(1, lines.Count(static line => line.Contains("uuid_generate_v1", StringComparison.Ordinal)), writer.ToString());
        StringAssert.Contains(writer.ToString(), "4 objects reference missing function 'uuid_generate_v1': Column 'public.t.a', Column 'public.t.b', Column 'public.t.c', and 1 more.");
    }

    [TestMethod]
    public void TheJsonDiagnosticsAreMergedToo()
    {
        var diagnostics = new[]
        {
            SemanticDiagnostics.Warning("Column 'public.t.a' references missing function 'f'."),
            SemanticDiagnostics.Warning("Column 'public.t.b' references missing function 'f'."),
        };
        var writer = new StringWriter();

        CompareJson.Write(Diff(string.Empty, string.Empty), diagnostics, writer);

        using var json = JsonDocument.Parse(writer.ToString());
        Assert.AreEqual(1, json.RootElement.GetProperty("diagnostics").GetArrayLength());
    }

    private static ChangeSet Diff(string oldSql, string newSql)
        => new DiffEngine().Diff(Build(oldSql), Build(newSql));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static string Text(ChangeSet changeSet)
    {
        var writer = new StringWriter();
        DiffOutputWriter.WriteText(changeSet, writer);
        return writer.ToString();
    }

    private static JsonDocument Json(ChangeSet changeSet)
    {
        var writer = new StringWriter();
        CompareJson.Write(changeSet, [], writer);
        return JsonDocument.Parse(writer.ToString());
    }
}
