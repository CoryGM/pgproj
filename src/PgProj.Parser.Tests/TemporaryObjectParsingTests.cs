using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-117: a temporary table, view, or sequence lasts only for the session that creates it, but it was modeled as a permanent
/// object in the default schema, which <c>publish</c> would create. It is now G-106's warning, and nothing is read from it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TemporaryObjectParsingTests
{
    [TestMethod]
    [DataRow("CREATE TEMP TABLE tmp (a int);", "TABLE")]
    [DataRow("CREATE TEMPORARY TABLE tmp (a int, b text);", "TABLE")]
    [DataRow("CREATE GLOBAL TEMPORARY TABLE tmp (a int);", "TABLE")]
    [DataRow("CREATE LOCAL TEMP TABLE tmp (a int) ON COMMIT DROP;", "TABLE")]
    [DataRow("CREATE TEMP VIEW tv AS SELECT 1 AS one;", "VIEW")]
    [DataRow("CREATE TEMPORARY SEQUENCE ts;", "SEQUENCE")]
    public void ATemporaryObjectIsAWarningAndNotModeled(string sql, string kind)
    {
        var tree = Parse(sql);

        var diagnostic = tree.Diagnostics.Single();
        Assert.AreEqual(SqlDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.AreEqual("UnmodeledStatement", diagnostic.Source);
        Assert.AreEqual(
            $"CREATE TEMPORARY {kind} is not modeled (a temporary object lasts only for the session that creates it); the statement is ignored: {sql.TrimEnd(';')}",
            diagnostic.Message);
        Assert.IsEmpty(tree.Nodes);
    }

    [TestMethod]
    [DataRow("CREATE TABLE public.t (a int);")]
    [DataRow("CREATE UNLOGGED TABLE public.t (a int);")]
    [DataRow("CREATE VIEW public.v AS SELECT 1 AS one;")]
    [DataRow("CREATE SEQUENCE public.s;")]
    public void APermanentObjectIsStillModeled(string sql)
    {
        var tree = Parse(sql);

        Assert.IsEmpty(tree.Diagnostics);
        Assert.IsNotEmpty(tree.Nodes);
    }

    private static SqlSyntaxTree Parse(string sql) => new PgQueryParser().Parse(sql);
}
