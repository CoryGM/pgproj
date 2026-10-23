using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>G-154: owners and comments of collations, extended statistics, and text search dictionaries and configurations are in the model.</summary>
[TestClass]
public sealed class NewKindOwnerAndCommentSemanticsTests
{
    private const string Objects =
        "CREATE SCHEMA app; CREATE TABLE app.t (a int, b int); "
        + "CREATE COLLATION app.ci (provider = icu, locale = 'und'); "
        + "CREATE STATISTICS app.st ON a, b FROM app.t; "
        + "CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = simple); "
        + "CREATE TEXT SEARCH CONFIGURATION app.c (PARSER = pg_catalog.\"default\"); ";

    [TestMethod]
    public void OwnersAreInTheModelAgainstTheirObjects()
    {
        var model = Build(Objects
            + "ALTER COLLATION app.ci OWNER TO bob; ALTER STATISTICS app.st OWNER TO bob; ALTER TEXT SEARCH DICTIONARY app.d OWNER TO amy; ALTER TEXT SEARCH CONFIGURATION app.c OWNER TO amy;");

        Assert.AreEqual(4, model.Owners.Count);
        var kinds = model.Owners.Values.OrderBy(static o => o.ObjectKind, StringComparer.Ordinal).Select(static o => $"{o.ObjectKind}:{o.OwnerRole}").ToArray();
        CollectionAssert.AreEqual(new[] { "collation:bob", "statistics:bob", "text search configuration:amy", "text search dictionary:amy" }, kinds);
        Assert.AreEqual(0, model.Diagnostics.Count(static d => d.Severity != SemanticDiagnosticSeverity.Info), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnOwnerOfAnObjectTheProjectDoesNotDeclareIsAWarning()
    {
        var model = Build("CREATE SCHEMA app; ALTER COLLATION app.nope OWNER TO bob;");

        Assert.AreEqual(0, model.Owners.Count);
        Assert.IsTrue(model.Diagnostics.Any(static d => d.Severity == SemanticDiagnosticSeverity.Warning && d.Message.Contains("ALTER COLLATION app.nope", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CommentsAreAppliedToTheirObjects()
    {
        var model = Build(Objects
            + "COMMENT ON COLLATION app.ci IS 'c1'; COMMENT ON STATISTICS app.st IS 's1'; COMMENT ON TEXT SEARCH DICTIONARY app.d IS 'd1'; COMMENT ON TEXT SEARCH CONFIGURATION app.c IS 'f1';");

        Assert.AreEqual("c1", model.Collations["collation:app.ci"].Comment);
        Assert.AreEqual("s1", model.Statistics["statistics:app.st"].Comment);
        Assert.AreEqual("d1", model.TextSearchDictionaries["tsdictionary:app.d"].Comment);
        Assert.AreEqual("f1", model.TextSearchConfigurations["tsconfiguration:app.c"].Comment);
    }

    [TestMethod]
    public void TheOwnerNoticeCountsTheseKindsToo()
    {
        var model = Build(Objects + "ALTER COLLATION app.ci OWNER TO bob;");

        var notice = OwnerCoverage.Describe(model);

        Assert.IsNotNull(notice);
        StringAssert.Contains(notice.Message, "state no owner");
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
