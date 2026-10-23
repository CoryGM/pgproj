using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-127: a live database's event triggers are written to the snapshot and to <c>init</c>'s scaffold as <c>CREATE EVENT TRIGGER</c> (with an
/// <c>ALTER EVENT TRIGGER</c> unless it is enabled the default way), in a file of their own at the top, and read back as event triggers.
/// </summary>
[TestClass]
public sealed class LiveEventTriggerTests
{
    private static readonly LiveFunctionSnapshot Function = new(
        "public",
        "log_ddl",
        "CREATE OR REPLACE FUNCTION public.log_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$",
        IdentityArguments: string.Empty);

    [TestMethod]
    public void AnEventTriggerIsWrittenWithItsEventFilterAndFunction()
    {
        // Act
        var sql = LiveDatabaseModelLoader.BuildCreateEventTriggerStatement(
            new LiveEventTriggerSnapshot("audit", "ddl_command_end", ["ALTER TABLE", "CREATE TABLE"], "public", "log_ddl", "O"));

        // Assert
        Assert.AreEqual("CREATE EVENT TRIGGER audit ON ddl_command_end WHEN TAG IN ('ALTER TABLE', 'CREATE TABLE') EXECUTE FUNCTION public.log_ddl();", sql);
    }

    [TestMethod]
    [DataRow("D", "DISABLE")]
    [DataRow("R", "ENABLE REPLICA")]
    [DataRow("A", "ENABLE ALWAYS")]
    public void ATriggerThatIsNotEnabledTheDefaultWayGetsAnAlter(string mode, string clause)
    {
        // Act
        var sql = LiveDatabaseModelLoader.BuildCreateEventTriggerStatement(new LiveEventTriggerSnapshot("audit", "sql_drop", [], "s", "f", mode));

        // Assert
        StringAssert.EndsWith(sql, $"ALTER EVENT TRIGGER audit {clause};");
    }

    [TestMethod]
    public void AnEventTriggerIsReadBackWithEveryPartItWasWrittenWith()
    {
        // Arrange
        var snapshot = new LiveEventTriggerSnapshot("Audit Trail", "ddl_command_end", ["CREATE TABLE"], "public", "log_ddl", "D");

        // Act
        var model = Load(snapshot);

        // Assert
        var trigger = model.EventTriggers["event trigger:Audit Trail"];
        Assert.AreEqual("ddl_command_end", trigger.EventName);
        CollectionAssert.AreEqual(new[] { "CREATE TABLE" }, trigger.NormalizedTags.ToArray());
        Assert.AreEqual("public.log_ddl", trigger.FunctionName);
        Assert.AreEqual("D", trigger.EnabledMode);
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheScaffoldPutsEachEventTriggerInAFileOfItsOwnAtTheTop()
    {
        // Act
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles(
            [new LiveSchemaSnapshot("public")], [], [], [], [], [Function], [], [], [], [], [],
            [new LiveEventTriggerSnapshot("audit", "ddl_command_end", [], "public", "log_ddl", "O")]);

        // Assert
        var file = files.Single(static f => f.RelativePath == "EventTrigger/audit.sql");
        StringAssert.Contains(file.Content, "CREATE EVENT TRIGGER audit ON ddl_command_end EXECUTE FUNCTION public.log_ddl();");
        Assert.IsEmpty(new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).FindUnparseableFiles([file]));
    }

    [TestMethod]
    public void ADatabaseWithoutEventTriggersScaffoldsNoEventTriggerFile()
    {
        // Act
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [], [], [], [], [Function], [], [], [], [], []);

        // Assert
        Assert.IsFalse(files.Any(static f => f.RelativePath.StartsWith("EventTrigger", StringComparison.Ordinal)));
    }

    private static DatabaseModel Load(LiveEventTriggerSnapshot eventTrigger)
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")], [], [], [], functions: [Function], eventTriggers: [eventTrigger]);
        return new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);
    }
}
