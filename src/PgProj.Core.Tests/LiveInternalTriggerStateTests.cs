using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-261: a live table's disabled internal (foreign-key) triggers are written to the snapshot and to <c>init</c>'s scaffold as <c>ALTER TABLE t DISABLE TRIGGER ALL</c>, with
/// <c>ENABLE TRIGGER USER</c> after it when some declared triggers are not disabled, and read back as the table's <c>InternalTriggerState</c>.
/// </summary>
[TestClass]
public sealed class LiveInternalTriggerStateTests
{
    private static readonly LiveColumnSnapshot[] Columns = [new LiveColumnSnapshot("id", "integer", true, null, false, null, null)];

    private static LiveTableSnapshot Table(params string[] states) => new("public", "t", Columns) { InternalTriggerStates = states };

    private static LiveTriggerSnapshot Trigger(string name, string? state = null)
        => new("public", "t", name, $"CREATE TRIGGER {name} BEFORE INSERT ON public.t FOR EACH ROW EXECUTE FUNCTION public.f()", "public", "f", null, state);

    [TestMethod]
    [DataRow(new string[0], null, false)]
    [DataRow(new[] { "O", "O" }, null, false)]
    [DataRow(new[] { "D", "D" }, "DISABLE", false)]
    [DataRow(new[] { "D", "D", "O" }, "DISABLE", true)]
    [DataRow(new[] { "D", "O", "O" }, null, true)]
    [DataRow(new[] { "D", "O" }, null, true)]
    [DataRow(new[] { "R", "R" }, null, true)]
    [DataRow(new[] { "A" }, null, true)]
    public void TheMostCommonStateIsWrittenAndAMixOfStatesIsSaid(string[] codes, string? expected, bool mixed)
    {
        var table = Table(codes);

        Assert.AreEqual(expected, table.InternalTriggerState);
        Assert.AreEqual(mixed, table.InternalTriggerStatesAreMixed);
    }

    [TestMethod]
    public void DisabledInternalTriggersAreWrittenAfterTheTriggersAndAreReadBack()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [Table("D", "D")], [], [], triggers: [Trigger("a"), Trigger("b", "DISABLE")]);

        StringAssert.Contains(sql, "ALTER TABLE public.t DISABLE TRIGGER ALL;");
        StringAssert.Contains(sql, "ALTER TABLE public.t ENABLE TRIGGER USER;");
        Assert.IsTrue(sql.IndexOf("CREATE TRIGGER a", StringComparison.Ordinal) < sql.IndexOf("DISABLE TRIGGER ALL", StringComparison.Ordinal), sql);

        var model = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);
        Assert.AreEqual("DISABLE", model.Tables.Values.Single().InternalTriggerState);
        Assert.AreEqual("ENABLE", model.Triggers.Values.Single(static trigger => trigger.Name == "a").EnabledState);
        Assert.AreEqual("DISABLE", model.Triggers.Values.Single(static trigger => trigger.Name == "b").EnabledState);
    }

    [TestMethod]
    public void TheUserStatementIsLeftOutWhenEveryDeclaredTriggerIsDisabledOrThereIsNone()
    {
        Assert.IsFalse(LiveDatabaseModelLoader.InternalTriggerStatements(Table("D"), [Trigger("a", "DISABLE")]).Contains("USER", StringComparison.Ordinal));
        Assert.IsFalse(LiveDatabaseModelLoader.InternalTriggerStatements(Table("D"), []).Contains("USER", StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, LiveDatabaseModelLoader.InternalTriggerStatements(Table("O", "O"), [Trigger("a")]));
    }

    [TestMethod]
    public void EnabledInternalTriggersWriteNothing()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [Table("O")], [], [], triggers: [Trigger("a")]);

        Assert.IsFalse(sql.Contains("TRIGGER ALL", StringComparison.Ordinal), sql);
    }

    [TestMethod]
    public void TheScaffoldPutsTheStatementInTheFileOfItsTableAndItParses()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles(
            [new LiveSchemaSnapshot("public")], [Table("D")], [], [], [], [], [], [], [], [Trigger("a")], []);

        var file = files.Single(static f => f.RelativePath == "Schema/public/Table/t.sql");
        StringAssert.Contains(file.Content, "DISABLE TRIGGER ALL;");
        StringAssert.Contains(file.Content, "ENABLE TRIGGER USER;");
        Assert.IsEmpty(new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).FindUnparseableFiles([file]));
    }
}
