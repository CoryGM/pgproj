using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-257: a trigger's or rule's firing mode (<c>DISABLE</c>, <c>ENABLE REPLICA</c>, <c>ENABLE ALWAYS</c>) is kept. <c>ALTER TABLE ... DISABLE TRIGGER</c> was reported as not modeled and ignored, so
/// a trigger the source database had disabled was created enabled. It is deployed after the trigger or rule, changed in place, and compared only when the project states one.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TriggerAndRuleStateTests
{
    private const string Setup = """
        CREATE TABLE public.t (id integer);
        CREATE FUNCTION public.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER trg BEFORE INSERT ON public.t FOR EACH ROW EXECUTE FUNCTION public.f();
        CREATE RULE r AS ON INSERT TO public.t DO ALSO NOTHING;
        """;

    [TestMethod]
    [DataRow("DISABLE", "DISABLE")]
    [DataRow("ENABLE REPLICA", "ENABLE REPLICA")]
    [DataRow("ENABLE ALWAYS", "ENABLE ALWAYS")]
    public void ATriggerAndARuleAreGivenTheirStateAfterTheyAreCreated(string written, string expected)
    {
        var script = Script(DatabaseModel.Empty, Build(Setup + $"ALTER TABLE public.t {written} TRIGGER trg; ALTER TABLE public.t {written} RULE r;"));

        StringAssert.Contains(script, $"ALTER TABLE \"public\".\"t\" {expected} TRIGGER \"trg\";");
        StringAssert.Contains(script, $"ALTER TABLE \"public\".\"t\" {expected} RULE \"r\";");
        Assert.IsTrue(script.IndexOf("CREATE TRIGGER", StringComparison.Ordinal) < script.IndexOf("TRIGGER \"trg\";", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AnEnabledTriggerNeedsNoStatement()
    {
        var script = Script(DatabaseModel.Empty, Build(Setup + "ALTER TABLE public.t ENABLE TRIGGER trg;"));

        Assert.IsFalse(script.Contains("ENABLE TRIGGER", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AChangedStateIsMadeInPlaceWithoutDroppingTheTriggerOrTheRule()
    {
        var script = Script(
            Build(Setup + "ALTER TABLE public.t DISABLE TRIGGER trg; ALTER TABLE public.t DISABLE RULE r;"),
            Build(Setup + "ALTER TABLE public.t ENABLE REPLICA TRIGGER trg; ALTER TABLE public.t ENABLE TRIGGER r;".Replace("ENABLE TRIGGER r", "ENABLE RULE r", StringComparison.Ordinal)));

        StringAssert.Contains(script, "ENABLE REPLICA TRIGGER \"trg\";");
        StringAssert.Contains(script, "ENABLE RULE \"r\";");
        Assert.IsFalse(script.Contains("DROP", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("CREATE", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ADisabledTriggerInTheDatabaseIsLeftAloneWhenTheProjectStatesNothing()
    {
        Assert.AreEqual(string.Empty, Script(
            Build(Setup + "ALTER TABLE public.t DISABLE TRIGGER trg;"),
            Build(Setup)));
    }

    [TestMethod]
    public void AnEnabledTriggerInTheDatabaseIsEnabledWhenTheProjectSaysDisableIsNotWritten()
    {
        // The project states DISABLE; the database has the trigger enabled (no state): it is disabled in place.
        var script = Script(Build(Setup), Build(Setup + "ALTER TABLE public.t DISABLE TRIGGER trg;"));

        Assert.AreEqual("ALTER TABLE \"public\".\"t\" DISABLE TRIGGER \"trg\";", script);
    }

    [TestMethod]
    public void TheSameStateOnBothSidesIsNoChange()
    {
        var model = Build(Setup + "ALTER TABLE public.t DISABLE TRIGGER trg; ALTER TABLE public.t ENABLE ALWAYS RULE r;");

        Assert.AreEqual(string.Empty, Script(model, model));
    }

    [TestMethod]
    public void AnActionForATriggerTheProjectDoesNotDeclareIsAWarning()
    {
        var model = Build("CREATE TABLE public.t (id integer); ALTER TABLE public.t DISABLE TRIGGER nope;");

        Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("does not declare that trigger", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    private const string TwoTriggers = Setup + "CREATE TRIGGER trg2 BEFORE UPDATE ON public.t FOR EACH ROW EXECUTE FUNCTION public.f();";

    [TestMethod]
    public void TriggerUserDisablesEveryTriggerTheProjectDeclaresOnTheTable()
    {
        var script = Script(DatabaseModel.Empty, Build(TwoTriggers + "ALTER TABLE public.t DISABLE TRIGGER USER;"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" DISABLE TRIGGER \"trg\";");
        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" DISABLE TRIGGER \"trg2\";");
    }

    [TestMethod]
    public void ATriggerDeclaredAfterTheUserStatementOrInAnotherFileIsCovered()
    {
        var model = new SemanticPipeline().Execute(
        [
            new PgQueryParser().Parse("ALTER TABLE public.t DISABLE TRIGGER USER;"),
            new PgQueryParser().Parse(TwoTriggers),
        ]);

        var script = Script(DatabaseModel.Empty, model);

        StringAssert.Contains(script, "DISABLE TRIGGER \"trg\";");
        StringAssert.Contains(script, "DISABLE TRIGGER \"trg2\";");
    }

    [TestMethod]
    public void AStatementNamingOneTriggerOverridesTriggerUserInEitherOrder()
    {
        foreach (var sql in new[]
        {
            "ALTER TABLE public.t DISABLE TRIGGER USER; ALTER TABLE public.t ENABLE ALWAYS TRIGGER trg;",
            "ALTER TABLE public.t ENABLE ALWAYS TRIGGER trg; ALTER TABLE public.t DISABLE TRIGGER USER;",
        })
        {
            var script = Script(DatabaseModel.Empty, Build(TwoTriggers + sql));

            StringAssert.Contains(script, "ENABLE ALWAYS TRIGGER \"trg\";");
            StringAssert.Contains(script, "DISABLE TRIGGER \"trg2\";");
            Assert.IsFalse(script.Contains("DISABLE TRIGGER \"trg\";", StringComparison.Ordinal), script);
        }
    }

    [TestMethod]
    public void TriggerUserDoesNotTouchATriggerOnAnotherTableOrARule()
    {
        var script = Script(DatabaseModel.Empty, Build(TwoTriggers + "CREATE TABLE public.u (id integer); CREATE TRIGGER other BEFORE INSERT ON public.u FOR EACH ROW EXECUTE FUNCTION public.f(); ALTER TABLE public.t DISABLE TRIGGER USER;"));

        Assert.IsFalse(script.Contains("TRIGGER \"other\";", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("DISABLE RULE", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void TriggerUserOnATableWithNoTriggersIsAWarning()
    {
        var model = Build("CREATE TABLE public.t (id integer); ALTER TABLE public.t DISABLE TRIGGER USER;");

        Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("declares no trigger", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TriggerUserIsNoLongerReportedAsNotModeled()
    {
        var model = Build(TwoTriggers + "ALTER TABLE public.t DISABLE TRIGGER USER;");

        Assert.IsFalse(model.Diagnostics.Any(static d => d.Message.Contains("not modeled", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
