using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-127: an event trigger is compared by its event, its tag filter (a set, upper case), the function it runs (without a leading
/// <c>public.</c>), and when it fires; the same trigger written another way is not a change, and a real change is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class EventTriggerDiffTests
{
    private const string Function = "CREATE FUNCTION public.log_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$; CREATE FUNCTION public.other() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$; ";

    private static string Trigger(string body) => Function + "CREATE EVENT TRIGGER t " + body;

    [TestMethod]
    public void AnEventTriggerDiffedWithItselfIsEmpty()
    {
        var model = Build(Trigger("ON ddl_command_end WHEN TAG IN ('CREATE TABLE') EXECUTE FUNCTION public.log_ddl();"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    [DataRow("ON ddl_command_end WHEN TAG IN ('CREATE TABLE', 'DROP TABLE') EXECUTE FUNCTION public.log_ddl();", "ON ddl_command_end WHEN TAG IN ('drop table', 'create table') EXECUTE FUNCTION log_ddl();")]
    [DataRow("ON ddl_command_end EXECUTE FUNCTION public.log_ddl();", "ON ddl_command_end EXECUTE PROCEDURE log_ddl();")]
    [DataRow("ON sql_drop WHEN TAG IN ('DROP TABLE', 'DROP TABLE') EXECUTE FUNCTION log_ddl();", "ON sql_drop WHEN TAG IN ('DROP TABLE') EXECUTE FUNCTION public.log_ddl();")]
    public void TheSameEventTriggerWrittenTwoWaysReportsNothing(string first, string second)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Trigger(first)), Build(Trigger(second)))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Trigger(second)), Build(Trigger(first)))));
    }

    [TestMethod]
    [DataRow("ON ddl_command_end EXECUTE FUNCTION log_ddl();", "ON ddl_command_start EXECUTE FUNCTION log_ddl();", nameof(EventTriggerDefinition.EventName))]
    [DataRow("ON ddl_command_end EXECUTE FUNCTION log_ddl();", "ON ddl_command_end EXECUTE FUNCTION other();", nameof(EventTriggerDefinition.FunctionName))]
    [DataRow("ON ddl_command_end EXECUTE FUNCTION log_ddl();", "ON ddl_command_end WHEN TAG IN ('CREATE TABLE') EXECUTE FUNCTION log_ddl();", nameof(EventTriggerDefinition.Tags))]
    [DataRow("ON ddl_command_end WHEN TAG IN ('CREATE TABLE') EXECUTE FUNCTION log_ddl();", "ON ddl_command_end WHEN TAG IN ('CREATE TABLE', 'ALTER TABLE') EXECUTE FUNCTION log_ddl();", nameof(EventTriggerDefinition.Tags))]
    public void AChangeToTheEventTheFilterOrTheFunctionIsReported(string before, string after, string property)
    {
        var diff = new DiffEngine().Diff(Build(Trigger(before)), Build(Trigger(after)));

        var detail = diff.Modified.Single(static change => change.NodeKind == ChangeNodeKind.EventTrigger).Details.Single();
        Assert.AreEqual(property, detail.PropertyName);
    }

    [TestMethod]
    [DataRow("DISABLE", "D")]
    [DataRow("ENABLE REPLICA", "R")]
    [DataRow("ENABLE ALWAYS", "A")]
    public void AChangeInWhenItFiresIsReported(string clause, string mode)
    {
        var diff = new DiffEngine().Diff(
            Build(Trigger("ON ddl_command_end EXECUTE FUNCTION log_ddl();")),
            Build(Trigger("ON ddl_command_end EXECUTE FUNCTION log_ddl(); ALTER EVENT TRIGGER t " + clause + ";")));

        var detail = diff.Modified.Single(static change => change.NodeKind == ChangeNodeKind.EventTrigger).Details.Single();
        Assert.AreEqual(nameof(EventTriggerDefinition.EnabledMode), detail.PropertyName);
        Assert.AreEqual("O", detail.OldValue?.ToString());
        Assert.AreEqual(mode, detail.NewValue?.ToString());
    }

    [TestMethod]
    public void AnAddedAndARemovedEventTriggerAreAddedAndRemoved()
    {
        var with = Build(Trigger("ON ddl_command_end EXECUTE FUNCTION log_ddl();"));
        var without = Build(Function);

        Assert.AreEqual(1, new DiffEngine().Diff(without, with).Added.Count(static c => c.NodeKind == ChangeNodeKind.EventTrigger));
        Assert.AreEqual(1, new DiffEngine().Diff(with, without).Removed.Count(static c => c.NodeKind == ChangeNodeKind.EventTrigger));
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
