using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-127: an event trigger is a model object of its own, keyed by its name alone; its tag filter is a set in upper case, an
/// <c>ALTER EVENT TRIGGER</c> sets its enabled mode from any file, and the function it runs is one it depends on.
/// </summary>
[TestClass]
public sealed class EventTriggerSemanticsTests
{
    private const string Function = "CREATE FUNCTION public.log_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;";

    [TestMethod]
    public void AnEventTriggerIsInTheModelKeyedByItsName()
    {
        // Act
        var model = Build(Function + " CREATE EVENT TRIGGER audit_ddl ON ddl_command_end EXECUTE FUNCTION public.log_ddl();");

        // Assert
        var trigger = model.EventTriggers["event trigger:audit_ddl"];
        Assert.AreEqual("audit_ddl", trigger.Name);
        Assert.AreEqual("ddl_command_end", trigger.EventName);
        Assert.AreEqual("public.log_ddl", trigger.FunctionName);
        Assert.AreEqual("O", trigger.EnabledMode);
        CollectionAssert.AreEqual(new[] { "public.log_ddl" }, trigger.ReferencedFunctions.ToArray());
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnUnqualifiedFunctionInPublicStaysAsWrittenAsATableTriggersDoes()
    {
        // Act
        var model = Build(Function + " CREATE EVENT TRIGGER audit_ddl ON ddl_command_end EXECUTE FUNCTION log_ddl();");

        // Assert
        // Qualified only when it is found outside public along a configured searchPath (G-93); the diff compares without a leading public.
        Assert.AreEqual("log_ddl", model.EventTriggers["event trigger:audit_ddl"].FunctionName);
    }

    [TestMethod]
    public void TheTagFilterIsUpperCaseDistinctAndSortedInItsComparisonForm()
    {
        // Act
        var trigger = Build(Function + " CREATE EVENT TRIGGER t ON ddl_command_end WHEN TAG IN ('drop table', 'CREATE TABLE', 'Create Table') EXECUTE FUNCTION public.log_ddl();")
            .EventTriggers["event trigger:t"];

        // Assert
        CollectionAssert.AreEqual(new[] { "CREATE TABLE", "DROP TABLE" }, trigger.NormalizedTags.ToArray());
        CollectionAssert.AreEqual(new[] { "drop table", "CREATE TABLE", "Create Table" }, trigger.Tags.ToArray());
    }

    [TestMethod]
    public void AnAlterEventTriggerSetsTheModeEvenFromAnotherFile()
    {
        // Act
        var model = new SemanticPipeline().Execute([
            new PgQueryParser().Parse(Function + " CREATE EVENT TRIGGER t ON ddl_command_end EXECUTE FUNCTION public.log_ddl();"),
            new PgQueryParser().Parse("ALTER EVENT TRIGGER t DISABLE;"),
        ]);

        // Assert
        Assert.AreEqual("D", model.EventTriggers["event trigger:t"].EnabledMode);
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheLastAlterEventTriggerWins()
    {
        // Act
        var model = Build(Function + " CREATE EVENT TRIGGER t ON ddl_command_end EXECUTE FUNCTION public.log_ddl(); ALTER EVENT TRIGGER t DISABLE; ALTER EVENT TRIGGER t ENABLE ALWAYS;");

        // Assert
        Assert.AreEqual("A", model.EventTriggers["event trigger:t"].EnabledMode);
    }

    [TestMethod]
    public void AnAlterOfAnEventTriggerTheProjectDoesNotDeclareIsAWarning()
    {
        // Act
        var model = Build("ALTER EVENT TRIGGER nope DISABLE;");

        // Assert
        Assert.IsEmpty(model.EventTriggers);
        Assert.AreEqual(1, model.Diagnostics.Count(static d => d.Severity == SemanticDiagnosticSeverity.Warning && d.Message.Contains("nope", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TwoEventTriggersOfOneNameAreAnError()
    {
        // Act
        var model = Build(Function + " CREATE EVENT TRIGGER t ON ddl_command_end EXECUTE FUNCTION public.log_ddl(); CREATE EVENT TRIGGER t ON sql_drop EXECUTE FUNCTION public.log_ddl();");

        // Assert
        Assert.AreEqual(1, model.Diagnostics.Count(static d => d.Severity == SemanticDiagnosticSeverity.Error && d.Message.Contains("Duplicate EventTrigger", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AnEventTriggerIsAModelNodeSoTheGraphSeesIt()
    {
        // Act
        var model = Build(Function + " CREATE EVENT TRIGGER t ON ddl_command_end EXECUTE FUNCTION public.log_ddl();");

        // Assert
        Assert.IsTrue(model.GetAllNodes().Any(static node => node is EventTriggerDefinition { Name: "t" }));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
