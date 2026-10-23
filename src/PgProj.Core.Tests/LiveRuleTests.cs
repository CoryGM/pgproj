using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-128: a live database's rules are written to the snapshot and to <c>init</c>'s scaffold as <c>pg_get_ruledef</c> prints them, in the file of the table or view
/// they are on, and read back as rules that carry that rendering.
/// </summary>
[TestClass]
public sealed class LiveRuleTests
{
    private const string Definition = "CREATE RULE t_log AS\n    ON UPDATE TO public.t\n   WHERE (new.n <> old.n) DO INSERT INTO public.log (id)\n  VALUES (old.id);";

    private static readonly LiveRuleSnapshot Log = new("public", "t", "t_log", Definition);

    private static readonly LiveTableSnapshot Table = new("public", "t", [new LiveColumnSnapshot("id", "integer", true, null, false, null, null), new LiveColumnSnapshot("n", "integer", true, null, false, null, null)]);

    [TestMethod]
    public void TheStatementIsThePostgreSqlRenderingWithOneSemicolon()
    {
        Assert.AreEqual(Definition, LiveDatabaseModelLoader.BuildCreateRuleStatement(Log));
        Assert.AreEqual(Definition, LiveDatabaseModelLoader.BuildCreateRuleStatement(Log with { Definition = Definition.TrimEnd(';') + "\n;;" }));
    }

    [TestMethod]
    public void ARuleIsReadBackWithItsRenderingAndItsRelation()
    {
        var model = Load(Log);
        var rule = model.Rules["rule:public.t.t_log"];

        Assert.AreEqual("update", rule.Event);
        Assert.IsFalse(rule.IsInstead);
        Assert.AreEqual(Definition.Trim(), rule.ServerRenderedDefinition);
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheScaffoldPutsARuleInTheFileOfItsTable()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles(
            [new LiveSchemaSnapshot("public")], [Table], [], [], [], [], [], [], [], [], [],
            rules: [Log]);

        var file = files.Single(static f => f.RelativePath == "Schema/public/Table/t.sql");
        StringAssert.Contains(file.Content, "CREATE RULE t_log AS");
        Assert.IsEmpty(new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).FindUnparseableFiles([file]));
    }

    [TestMethod]
    public void ADatabaseWithoutRulesScaffoldsNoRule()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [Table], [], [], [], [], [], [], [], [], []);

        Assert.IsFalse(files.Any(static f => f.Content.Contains("CREATE RULE", StringComparison.Ordinal)));
    }

    private static DatabaseModel Load(LiveRuleSnapshot rule)
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [Table], [], [], rules: [rule]);
        var model = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);
        ServerRuleRenderer.AttachLiveRenderings(model, [rule]);
        return model;
    }
}
