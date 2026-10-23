using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-293: a row trigger of a partitioned table is cloned onto each partition, and a partition's copy has a firing mode of its own. The statement that sets it
/// (<c>ALTER TABLE partition DISABLE TRIGGER name</c>) was warned about and ignored.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PartitionTriggerStateTests
{
    private const string Tables = """
        CREATE FUNCTION public.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TABLE public.parted (a int) PARTITION BY RANGE (a);
        CREATE TABLE public.p1 PARTITION OF public.parted FOR VALUES FROM (1) TO (10);
        CREATE TABLE public.p2 PARTITION OF public.parted FOR VALUES FROM (10) TO (20) PARTITION BY RANGE (a);
        CREATE TABLE public.p2_2 PARTITION OF public.p2 FOR VALUES FROM (10) TO (15);
        CREATE TRIGGER aft_row AFTER INSERT ON public.parted FOR EACH ROW EXECUTE FUNCTION public.f();
        """;

    [TestMethod]
    public void AStateOnAPartitionAndASubPartitionIsKeptOnTheTriggerParentFirst()
    {
        var model = Build(Tables + """
            ALTER TABLE public.p2_2 ENABLE ALWAYS TRIGGER aft_row;
            ALTER TABLE public.p2 DISABLE TRIGGER aft_row;
            """);

        Assert.AreEqual(0, model.Diagnostics.Count(static d => d.Message.Contains("does not declare", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
        var trigger = model.Triggers.Values.Single();
        CollectionAssert.AreEqual(new[] { "public.p2", "public.p2_2" }, trigger.PartitionStates.Keys.ToArray(), "a table's state is written before its partitions'");
        Assert.AreEqual("DISABLE", trigger.PartitionStates["public.p2"]);
        Assert.AreEqual("ENABLE ALWAYS", trigger.PartitionStates["public.p2_2"]);
        Assert.IsNull(trigger.EnabledState);
    }

    [TestMethod]
    public void APartitionAttachedLaterIsFoundToo()
    {
        var model = Build("""
            CREATE FUNCTION public.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
            CREATE TABLE public.parted (a int) PARTITION BY RANGE (a);
            CREATE TABLE public.p1 (a int);
            ALTER TABLE public.p1 DISABLE TRIGGER aft_row;
            ALTER TABLE ONLY public.parted ATTACH PARTITION public.p1 FOR VALUES FROM (1) TO (10);
            CREATE TRIGGER aft_row AFTER INSERT ON public.parted FOR EACH ROW EXECUTE FUNCTION public.f();
            """);

        Assert.AreEqual(0, model.Diagnostics.Count(static d => d.Message.Contains("does not declare", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
        Assert.AreEqual("DISABLE", model.Triggers.Values.Single().PartitionStates["public.p1"]);
    }

    [TestMethod]
    public void ATriggerNoAncestorDeclaresIsStillAWarning()
    {
        var model = Build(Tables + """
            CREATE TABLE public.plain (a int);
            ALTER TABLE public.plain DISABLE TRIGGER aft_row;
            ALTER TABLE public.p1 DISABLE TRIGGER other;
            """);

        Assert.AreEqual(2, model.Diagnostics.Count(static d => d.Message.Contains("does not declare that trigger", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void ANewTriggerIsFollowedByTheStatesAndCreatedAfterItsPartitions()
    {
        var script = Script(DatabaseModel.Empty, Build(Tables + """
            ALTER TABLE public.p2_2 ENABLE ALWAYS TRIGGER aft_row;
            ALTER TABLE public.p2 DISABLE TRIGGER aft_row;
            """));

        var create = script.IndexOf("CREATE TRIGGER", StringComparison.Ordinal);
        var disable = script.IndexOf("ALTER TABLE \"public\".\"p2\" DISABLE TRIGGER \"aft_row\";", StringComparison.Ordinal);
        var always = script.IndexOf("ALTER TABLE \"public\".\"p2_2\" ENABLE ALWAYS TRIGGER \"aft_row\";", StringComparison.Ordinal);
        Assert.IsGreaterThan(create, disable, script);
        Assert.IsGreaterThan(disable, always, "p2 is written before p2_2, since PostgreSQL sets the partitions below a table too\n" + script);
        Assert.IsGreaterThan(script.LastIndexOf("PARTITION OF", StringComparison.Ordinal), create, "every partition exists first\n" + script);
    }

    [TestMethod]
    public void OnlyAChangedStateIsWrittenAndTheTriggerIsNotRecreated()
    {
        var live = Build(Tables + """
            ALTER TABLE public.p2 DISABLE TRIGGER aft_row;
            ALTER TABLE public.p1 ENABLE REPLICA TRIGGER aft_row;
            """);
        var project = Build(Tables + """
            ALTER TABLE public.p2 DISABLE TRIGGER aft_row;
            ALTER TABLE public.p1 DISABLE TRIGGER aft_row;
            """);

        var script = Script(live, project);

        Assert.AreEqual("ALTER TABLE \"public\".\"p1\" DISABLE TRIGGER \"aft_row\";", script);
    }

    [TestMethod]
    public void ACopyTheProjectSaysNothingAboutKeepsWhatTheDatabaseHas()
    {
        var live = Build(Tables + "ALTER TABLE public.p2 DISABLE TRIGGER aft_row;");
        var project = Build(Tables);

        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(live, project)));
    }

    [TestMethod]
    public void TheSameModelIsNoChangeAndAnExplicitEnableOfAnEnabledCopyIsNone()
    {
        var one = Build(Tables + "ALTER TABLE public.p1 DISABLE TRIGGER aft_row;");
        var enabledStated = Build(Tables + "ALTER TABLE public.p1 ENABLE TRIGGER aft_row;");

        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(one, one)));
        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(Build(Tables), enabledStated)));
        Assert.AreEqual(1, ChangeCount(new DiffEngine().Diff(Build(Tables), one)));
        Assert.AreEqual(1, ChangeCount(new DiffEngine().Diff(one, enabledStated)));
    }

    private static int ChangeCount(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static string Script(DatabaseModel live, DatabaseModel project)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }
}
