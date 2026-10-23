using System.CommandLine;
using System.Text.Json;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-107: <c>pgproj diff</c> used to print only the three counts. The user chose to list each change by default, keeping the counts
/// as the last lines (so a script reading them still works), and to add <c>--format json</c> for CI.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DiffOutputWriterTests
{
    private const string Before = "CREATE SCHEMA s; CREATE TABLE s.t (id int, note text); CREATE INDEX t_id_idx ON s.t (id);";
    private const string After = "CREATE SCHEMA s; CREATE TABLE s.t (id int NOT NULL, note text, extra int); COMMENT ON TABLE s.t IS 'orders'; CREATE TABLE s.u (id int);";

    [TestMethod]
    public void TextListsEachChangeThenEndsWithTheCounts()
    {
        var lines = Text(Diff(Before, After));

        CollectionAssert.Contains(lines, "  + Table s.u");
        CollectionAssert.Contains(lines, "  + Column s.t.extra");
        CollectionAssert.Contains(lines, "  - Index s.t_id_idx");
        CollectionAssert.Contains(lines, "  ~ Table s.t");
        CollectionAssert.Contains(lines, "      Comment: (none) -> 'orders'");
        CollectionAssert.Contains(lines, "  ~ Column s.t.id");
        CollectionAssert.AreEqual(new[] { "Added: 3", "Removed: 1", "Modified: 2" }, lines.TakeLast(3).ToArray());
    }

    [TestMethod]
    public void TextForNoChangesIsExactlyTheCountsDiffAlwaysPrinted()
    {
        CollectionAssert.AreEqual(new[] { "Added: 0", "Removed: 0", "Modified: 0" }, Text(Diff(After, After)));
    }

    [TestMethod]
    public void ALongOrMultiLineValueIsShortenedToOneLineInTextOnly()
    {
        var longText = new string('x', 400);
        var changeSet = Diff(
            "CREATE TABLE public.t (id int); COMMENT ON TABLE public.t IS 'short';",
            $"CREATE TABLE public.t (id int); COMMENT ON TABLE public.t IS 'line one\nline two {longText}';");

        var commentLine = Text(changeSet).Single(static line => line.TrimStart().StartsWith("Comment:", StringComparison.Ordinal));
        StringAssert.Contains(commentLine, "'line one line two xxx");
        StringAssert.EndsWith(commentLine, "...'");

        using var json = Json(changeSet);
        var newValue = json.RootElement.GetProperty("modified")[0].GetProperty("changes").EnumerateArray()
            .Single(static change => change.GetProperty("property").GetString() == "Comment").GetProperty("new").GetString();
        StringAssert.EndsWith(newValue, longText, "JSON carries the value whole.");
    }

    [TestMethod]
    public void JsonCarriesTheSameChangeSetAndTheCounts()
    {
        using var json = Json(Diff(Before, After));
        var root = json.RootElement;

        Assert.AreEqual(3, root.GetProperty("added").GetArrayLength());
        Assert.AreEqual("Index", root.GetProperty("removed")[0].GetProperty("kind").GetString());
        Assert.AreEqual("s.t_id_idx", root.GetProperty("removed")[0].GetProperty("id").GetString());
        var table = root.GetProperty("modified").EnumerateArray().Single(static change => change.GetProperty("id").GetString() == "s.t");
        var comment = table.GetProperty("changes").EnumerateArray().Single(static change => change.GetProperty("property").GetString() == "Comment");
        Assert.AreEqual(JsonValueKind.Null, comment.GetProperty("old").ValueKind);
        Assert.AreEqual("orders", comment.GetProperty("new").GetString());
        Assert.AreEqual(3, root.GetProperty("summary").GetProperty("added").GetInt32());
        Assert.AreEqual(1, root.GetProperty("summary").GetProperty("removed").GetInt32());
        Assert.AreEqual(2, root.GetProperty("summary").GetProperty("modified").GetInt32());
        Assert.AreEqual(0, root.GetProperty("diagnostics").GetArrayLength());
    }

    [TestMethod]
    public void JsonCarriesTheDiagnostics()
    {
        var diagnostics = new[] { SemanticDiagnostics.Warning("something to look at", source: "test") };
        var writer = new StringWriter();

        CompareJson.Write(Diff(After, After), diagnostics, writer);

        using var json = JsonDocument.Parse(writer.ToString());
        var diagnostic = json.RootElement.GetProperty("diagnostics")[0];
        Assert.AreEqual("Warning", diagnostic.GetProperty("severity").GetString());
        Assert.AreEqual("something to look at", diagnostic.GetProperty("message").GetString());
    }

    [TestMethod]
    [DataRow("text", true)]
    [DataRow("json", true)]
    [DataRow("xml", false)]
    public void FormatAcceptsOnlyTextOrJson(string format, bool accepted)
    {
        var root = new RootCommand("pgproj") { DiffCommand.Create(new CliContext()) };

        var result = root.Parse(["diff", "--format", format]);

        Assert.AreEqual(accepted, result.Errors.Count == 0, string.Join("; ", result.Errors.Select(static error => error.Message)));
    }

    private static ChangeSet Diff(string oldSql, string newSql)
        => new DiffEngine().Diff(Build(oldSql), Build(newSql));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static string[] Text(ChangeSet changeSet)
    {
        var writer = new StringWriter();
        DiffOutputWriter.WriteText(changeSet, writer);
        return writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static JsonDocument Json(ChangeSet changeSet)
    {
        var writer = new StringWriter();
        CompareJson.Write(changeSet, [], writer);
        return JsonDocument.Parse(writer.ToString());
    }
}
