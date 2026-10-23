using System.Globalization;
using System.Reflection;
using System.Text;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-272: a statement whose parse tree nests deeper than 64 levels (a sum of about 23 terms) made the JSON reader throw, and the whole file
/// was dropped with a warning. Trees are now read at any depth libpg_query returns, on a large stack; a statement deeper than pgproj reads,
/// or a tree that cannot be read, is an error.
/// </summary>
[TestClass]
public sealed class DeepStatementParsingTests
{
    private static string Sum(int terms) => string.Join(" + ", Enumerable.Repeat("a", terms));

    [TestMethod]
    public void AViewNestedPastSixtyFourLevelsIsReadWithTheRestOfItsFile()
    {
        var tree = new PgQueryParser().Parse(
            $"CREATE TABLE public.t (a int);\nCREATE VIEW public.v AS SELECT {Sum(100)} AS x FROM public.t;\nCREATE TABLE public.u (b int);");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsEmpty(tree.Diagnostics);
        Assert.AreEqual(2, tree.Tables.Count());
        Assert.AreEqual("v", tree.Views.Single().Name);
    }

    [TestMethod]
    public void DeepChecksDefaultsAndIndexExpressionsAreRead()
    {
        var tree = new PgQueryParser().Parse(
            $"CREATE TABLE public.t (a int, b int DEFAULT ({string.Join(" + ", Enumerable.Repeat("1", 100))}), CONSTRAINT c CHECK (({Sum(100)}) > 0));\nCREATE INDEX i ON public.t (({Sum(100)}));");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(1, tree.Tables.Count());
        Assert.AreEqual("i", tree.Indexes.Single().Name);
    }

    [TestMethod]
    public void AStatementTooDeepForADefaultStackIsReadFromAThreadWithOne()
    {
        // A 300-term sum overflowed a 1 MB stack (the main thread's on Windows) and ended the process before the parser ran on a large stack.
        SqlSyntaxTree? tree = null;
        var thread = new Thread(() => tree = new PgQueryParser().Parse($"CREATE TABLE public.t (a int);\nCREATE VIEW public.v AS SELECT {Sum(300)} AS x FROM public.t;"), 1024 * 1024);
        thread.Start();
        thread.Join();

        Assert.IsNotNull(tree);
        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual("v", tree.Views.Single().Name);
    }

    [TestMethod]
    public void AStatementLibpgQueryRefusesIsItsError()
    {
        var tree = new PgQueryParser().Parse($"CREATE TABLE public.t (a int);\nCREATE VIEW public.v AS SELECT {Sum(5000)} AS x FROM public.t;");

        var messages = string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message));
        Assert.IsTrue(tree.HasErrors, messages);
        // libpg_query's own refusal: on Windows its parser runs out of stack; on Linux and macOS it parses the statement but will not serialize a tree that deep.
        Assert.IsTrue(
            tree.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("stack depth limit exceeded", StringComparison.Ordinal)
                || diagnostic.Message.Contains("parse tree is nested too deeply to serialize to protobuf", StringComparison.Ordinal)),
            messages);
    }

    [TestMethod]
    public void AStatementDeeperThanPgprojReadsIsAnErrorNamingItAndTheOthersAreRead()
    {
        // libpg_query refuses a statement long before MaxStatementDepth, so the tree is made by hand: a real first statement, and a second
        // whose body is nested past the limit.
        const string sql = "CREATE TABLE public.t (a int);\nSELECT 1;";
        var json = new PgQueryParser().Parse(sql).RawJsonAst!;
        string selectBody;
        using (var document = System.Text.Json.JsonDocument.Parse(json))
        {
            selectBody = document.RootElement.GetProperty("stmts")[1].GetProperty("stmt").GetRawText();
        }

        var depth = PgQueryJson.MaxStatementDepth + 10;
        var deep = new StringBuilder().Insert(0, "{\"x\":", depth).Append('1').Append('}', depth).ToString();
        var tooDeep = json.Replace(selectBody, deep, StringComparison.Ordinal);
        Assert.AreNotEqual(json, tooDeep);

        var (nodes, diagnostics) = MapNodes(sql, tooDeep);

        var error = diagnostics.Single();
        Assert.AreEqual(SqlDiagnosticSeverity.Error, error.Severity);
        StringAssert.Contains(error.Message, $"nested more than {PgQueryJson.MaxStatementDepth.ToString("N0", CultureInfo.CurrentCulture)} levels deep");
        StringAssert.Contains(error.Message, "SELECT 1");
        Assert.AreEqual(2, error.LineNumber);
        Assert.IsTrue(nodes.OfType<TableNode>().Any(static table => table.Name == "t"));
    }

    [TestMethod]
    public void ATreeThatCannotBeReadIsAnErrorNotAWarning()
    {
        var (nodes, diagnostics) = MapNodes("SELECT 1;", "{\"stmts\": [ not json");

        Assert.IsEmpty(nodes);
        var error = diagnostics.Single();
        Assert.AreEqual(SqlDiagnosticSeverity.Error, error.Severity);
        StringAssert.Contains(error.Message, "none of its statements were read");
    }

    [TestMethod]
    public void TheDepthIsCountedWithoutRecursion()
    {
        Assert.AreEqual(0, PgQueryJson.DepthOf("1"));
        Assert.AreEqual(3, PgQueryJson.DepthOf("{\"a\":[{\"b\":1}]}"));
        var deep = new string('[', 100_000) + new string(']', 100_000);
        Assert.AreEqual(99_999, PgQueryJson.DepthOf(deep));
        using var document = PgQueryJson.Parse(deep);
        Assert.AreEqual(System.Text.Json.JsonValueKind.Array, document.RootElement.ValueKind);
    }

    private static (IReadOnlyList<SqlNode> Nodes, List<SqlDiagnostic> Diagnostics) MapNodes(string sql, string json)
    {
        var method = typeof(PgQueryParser).GetMethod("MapNodes", BindingFlags.NonPublic | BindingFlags.Static)!;
        var diagnostics = new List<SqlDiagnostic>();
        var nodes = (IReadOnlyList<SqlNode>)method.Invoke(null, [sql, json, diagnostics])!;
        return (nodes, diagnostics);
    }
}
