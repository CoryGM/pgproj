using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-127: an event trigger is created after the function it runs and dropped before it; it is written with its event, its tag filter, and
/// (when it is not enabled the default way) an <c>ALTER EVENT TRIGGER</c>; a change is a drop and a create; and one that only disappears is a
/// destructive change, since it stops guarding every DDL command it ran on.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class EventTriggerDeployTests
{
    private const string Function = "CREATE FUNCTION public.log_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$; ";

    [TestMethod]
    public void AnEventTriggerIsCreatedAfterItsFunction()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Function + "CREATE EVENT TRIGGER audit ON ddl_command_end EXECUTE FUNCTION public.log_ddl();"));

        var functionAt = statements.FindIndex(static s => s.Contains("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal) || s.Contains("CREATE FUNCTION", StringComparison.Ordinal));
        var triggerAt = statements.FindIndex(static s => s.StartsWith("CREATE EVENT TRIGGER", StringComparison.Ordinal));
        Assert.IsTrue(functionAt >= 0 && triggerAt > functionAt, string.Join("\n---\n", statements));
        Assert.AreEqual("CREATE EVENT TRIGGER \"audit\"\n    ON ddl_command_end\n    EXECUTE FUNCTION \"public\".\"log_ddl\"();", statements[triggerAt]);
    }

    [TestMethod]
    public void TheTagFilterIsWrittenAsTheProjectGaveIt()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Function + "CREATE EVENT TRIGGER audit ON ddl_command_end WHEN TAG IN ('CREATE TABLE', 'alter table') EXECUTE FUNCTION public.log_ddl();"));

        StringAssert.Contains(string.Join("\n", statements), "WHEN TAG IN ('CREATE TABLE', 'alter table')");
    }

    [TestMethod]
    [DataRow("DISABLE", "ALTER EVENT TRIGGER \"audit\" DISABLE;")]
    [DataRow("ENABLE REPLICA", "ALTER EVENT TRIGGER \"audit\" ENABLE REPLICA;")]
    [DataRow("ENABLE ALWAYS", "ALTER EVENT TRIGGER \"audit\" ENABLE ALWAYS;")]
    public void ATriggerThatIsNotEnabledTheDefaultWayIsAlteredRightAfterItIsCreated(string clause, string expected)
    {
        var statements = Statements(DatabaseModel.Empty, Build(Function + $"CREATE EVENT TRIGGER audit ON sql_drop EXECUTE FUNCTION public.log_ddl(); ALTER EVENT TRIGGER audit {clause};"));

        var trigger = statements.Single(static s => s.StartsWith("CREATE EVENT TRIGGER", StringComparison.Ordinal));
        StringAssert.EndsWith(trigger, expected);
    }

    [TestMethod]
    public void AnEnabledTriggerHasNoAlter()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Function + "CREATE EVENT TRIGGER audit ON sql_drop EXECUTE FUNCTION public.log_ddl(); ALTER EVENT TRIGGER audit ENABLE;"));

        Assert.IsFalse(string.Join("\n", statements).Contains("ALTER EVENT TRIGGER", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AChangedEventTriggerIsDroppedAndCreatedAgainAndIsNotDestructive()
    {
        var oldModel = Build(Function + "CREATE EVENT TRIGGER audit ON ddl_command_end EXECUTE FUNCTION public.log_ddl();");
        var newModel = Build(Function + "CREATE EVENT TRIGGER audit ON ddl_command_start EXECUTE FUNCTION public.log_ddl();");
        var batch = Generate(oldModel, newModel);

        var script = string.Join("\n", Sql(batch));
        StringAssert.Contains(script, "DROP EVENT TRIGGER \"audit\";");
        StringAssert.Contains(script, "ON ddl_command_start");
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void ARemovedEventTriggerIsDroppedBeforeItsFunctionAndIsDestructive()
    {
        var oldModel = Build(Function + "CREATE EVENT TRIGGER audit ON ddl_command_end EXECUTE FUNCTION public.log_ddl();");
        var newModel = Build("CREATE TABLE public.keep (a integer);");
        var batch = Generate(oldModel, newModel);

        var statements = Sql(batch);
        var triggerAt = statements.FindIndex(static s => s.StartsWith("DROP EVENT TRIGGER", StringComparison.Ordinal));
        var functionAt = statements.FindIndex(static s => s.StartsWith("DROP FUNCTION", StringComparison.Ordinal));
        Assert.IsTrue(triggerAt >= 0 && functionAt > triggerAt, string.Join("\n---\n", statements));
        var destructive = batch.GetDestructiveOperations().Single();
        StringAssert.Contains(destructive.Reasons.Single(), "event trigger dropped");
        StringAssert.Contains(destructive.Reasons.Single(), "ddl_command_end");
    }

    [TestMethod]
    public void TheSameEventTriggerWrittenTwoWaysDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build(Function + "CREATE EVENT TRIGGER audit ON ddl_command_end WHEN TAG IN ('CREATE TABLE') EXECUTE FUNCTION public.log_ddl();"),
            Build(Function + "CREATE EVENT TRIGGER audit ON ddl_command_end WHEN TAG IN ('create table') EXECUTE PROCEDURE log_ddl();")));
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static List<string> Sql(OperationBatch batch)
        => batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel) => Sql(Generate(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
