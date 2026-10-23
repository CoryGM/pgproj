using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-261: <c>ALTER TABLE t { ENABLE | DISABLE } TRIGGER ALL</c>. The declared triggers take the state as with <c>USER</c> (G-259); the table's internal (foreign-key) triggers are a setting of the table,
/// compared only when the project states it, written at the end of the script (after every foreign key exists), and a statement only a superuser can run.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class InternalTriggerStateTests
{
    private const string Setup = """
        CREATE TABLE public.p (id integer PRIMARY KEY);
        CREATE TABLE public.c (id integer, p_id integer REFERENCES public.p (id));
        CREATE FUNCTION public.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER trg BEFORE INSERT ON public.c FOR EACH ROW EXECUTE FUNCTION public.f();
        CREATE TRIGGER trg2 BEFORE UPDATE ON public.c FOR EACH ROW EXECUTE FUNCTION public.f();
        """;

    [TestMethod]
    public void ATableIsGivenDisabledInternalTriggersAfterEveryKeyExists()
    {
        var model = Build(Setup + "ALTER TABLE public.c DISABLE TRIGGER ALL;");

        Assert.AreEqual("DISABLE", model.Tables.Values.Single(static table => table.Name == "c").InternalTriggerState);
        var script = Script(DatabaseModel.Empty, model);
        var statement = "ALTER TABLE \"public\".\"c\" DISABLE TRIGGER ALL;";
        StringAssert.Contains(script, statement);
        Assert.AreEqual(script.LastIndexOf(statement, StringComparison.Ordinal), script.IndexOf(statement, StringComparison.Ordinal), "written once");
        Assert.IsTrue(script.IndexOf("FOREIGN KEY", StringComparison.Ordinal) < script.IndexOf(statement, StringComparison.Ordinal), script);
        Assert.IsTrue(script.IndexOf("CREATE TRIGGER", StringComparison.Ordinal) < script.IndexOf(statement, StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void TheDeclaredTriggersTakeTheStateAsWithUserAndANamedStatementOverridesIt()
    {
        var model = Build(Setup + "ALTER TABLE public.c ENABLE ALWAYS TRIGGER trg2; ALTER TABLE public.c DISABLE TRIGGER ALL;");

        Assert.AreEqual("DISABLE", model.Triggers.Values.Single(static trigger => trigger.Name == "trg").EnabledState);
        Assert.AreEqual("ENABLE ALWAYS", model.Triggers.Values.Single(static trigger => trigger.Name == "trg2").EnabledState);
        var script = Script(DatabaseModel.Empty, model);

        // ALL also reaches trg2, so its own state is written again after it.
        var all = script.IndexOf("DISABLE TRIGGER ALL;", StringComparison.Ordinal);
        Assert.IsTrue(all >= 0, script);
        Assert.IsTrue(script.IndexOf("ENABLE ALWAYS TRIGGER \"trg2\";", all, StringComparison.Ordinal) > all, script);
    }

    [TestMethod]
    public void EnablingTheInternalTriggersOfANewTableNeedsNoStatement()
    {
        Assert.IsFalse(Script(DatabaseModel.Empty, Build(Setup + "ALTER TABLE public.c ENABLE TRIGGER ALL;")).Contains("TRIGGER ALL", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AChangedStateIsOnlyTheStatementAtTheEndOfTheScript()
    {
        var script = Script(Build(Setup + "ALTER TABLE public.c DISABLE TRIGGER ALL;"), Build(Setup + "ALTER TABLE public.c ENABLE TRIGGER ALL;"));

        // The declared triggers' own states change too (ALL reaches them), in place; the internal triggers' statement comes last.
        Assert.IsTrue(script.EndsWith("ENABLE TRIGGER ALL;", StringComparison.Ordinal), script);
        StringAssert.Contains(script, "ALTER TABLE \"public\".\"c\" ENABLE TRIGGER \"trg\";");
        Assert.IsFalse(script.Contains("DROP", StringComparison.Ordinal) || script.Contains("CREATE", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("No column-level changes", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ADisabledStateInTheDatabaseIsLeftAloneWhenTheProjectStatesNothing()
    {
        Assert.AreEqual(string.Empty, Script(Build(Setup + "ALTER TABLE public.c DISABLE TRIGGER ALL;"), Build(Setup)));
    }

    [TestMethod]
    public void TheSameModelAgainstItselfAndAnUnchangedStateReportNothing()
    {
        var model = Build(Setup + "ALTER TABLE public.c DISABLE TRIGGER ALL;");

        Assert.AreEqual(0, new DiffEngine().Diff(model, model).Modified.Count);
        Assert.AreEqual(0, new DiffEngine().Diff(model, Build(Setup + "ALTER TABLE public.c DISABLE TRIGGER ALL;")).Modified.Count);
    }

    [TestMethod]
    public void AStateThatDiffersIsReportedAndAStateTheProjectLeavesOutIsNot()
    {
        var disabled = Build(Setup + "ALTER TABLE public.c DISABLE TRIGGER ALL;");
        var enabled = Build(Setup + "ALTER TABLE public.c ENABLE TRIGGER ALL;");

        var modified = new DiffEngine().Diff(enabled, disabled).Modified;
        Assert.IsTrue(modified.Any(static change => change.Details.Any(static detail => detail.PropertyName == nameof(TableDefinition.InternalTriggerState))));
        Assert.IsFalse(new DiffEngine().Diff(disabled, Build(Setup)).Modified.Any(static change => change.NewNode is TableDefinition), "a table the project says nothing about keeps what the database has");
    }

    [TestMethod]
    public void TriggerAllIsNoLongerReportedAsNotModeled()
    {
        var model = Build(Setup + "ALTER TABLE public.c DISABLE TRIGGER ALL;");

        Assert.IsFalse(model.Diagnostics.Any(static d => d.Message.Contains("not modeled", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TriggerAllOnATableWithNoTriggersIsStatedWithoutAWarning()
    {
        var model = Build("CREATE TABLE public.t (id integer); ALTER TABLE public.t DISABLE TRIGGER ALL;");

        Assert.AreEqual(0, model.Diagnostics.Count(static d => d.Severity == SemanticDiagnosticSeverity.Warning), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
        Assert.AreEqual("DISABLE", model.Tables.Values.Single().InternalTriggerState);
    }

    [TestMethod]
    public void TriggerAllOnATheProjectDoesNotDeclareIsAWarning()
    {
        var model = Build("ALTER TABLE public.missing DISABLE TRIGGER ALL;");

        Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("does not declare that table", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheStatementIsInTheScriptsOwnWordsEvenForAnotherSchemaAndAQuotedName()
    {
        var script = Script(DatabaseModel.Empty, Build("CREATE SCHEMA \"S\"; CREATE TABLE \"S\".\"My T\" (id integer); ALTER TABLE \"S\".\"My T\" DISABLE TRIGGER ALL;"));

        StringAssert.Contains(script, "ALTER TABLE \"S\".\"My T\" DISABLE TRIGGER ALL;");
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
