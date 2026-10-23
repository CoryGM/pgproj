namespace PgProj.Parser.Tests;

/// <summary>
/// G-127: <c>CREATE EVENT TRIGGER</c> and <c>ALTER EVENT TRIGGER ... DISABLE|ENABLE</c> are read instead of being warned about and ignored (G-106).
/// An event trigger belongs to no schema; only its function is qualified.
/// </summary>
[TestClass]
public sealed class EventTriggerParsingTests
{
    [TestMethod]
    public void AnEventTriggerIsReadWithItsEventAndFunction()
    {
        // Act
        var tree = new PgQueryParser().Parse("CREATE EVENT TRIGGER audit_ddl ON ddl_command_end EXECUTE FUNCTION public.log_ddl();");
        var trigger = tree.EventTriggers.Single();

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual("audit_ddl", trigger.Name);
        Assert.IsNull(trigger.SchemaName);
        Assert.AreEqual("ddl_command_end", trigger.EventName);
        Assert.AreEqual("public.log_ddl", trigger.FunctionName);
        Assert.AreEqual(0, trigger.Tags.Count);
        Assert.AreEqual("O", trigger.EnabledMode);
    }

    [TestMethod]
    public void TheTagFilterIsReadAsWritten()
    {
        // Act
        var trigger = new PgQueryParser().Parse("CREATE EVENT TRIGGER t ON ddl_command_end WHEN TAG IN ('CREATE TABLE', 'alter table') EXECUTE PROCEDURE g();").EventTriggers.Single();

        // Assert
        CollectionAssert.AreEqual(new[] { "CREATE TABLE", "alter table" }, trigger.Tags.ToArray());
        Assert.AreEqual("g", trigger.FunctionName);
    }

    [TestMethod]
    [DataRow("ddl_command_start")]
    [DataRow("ddl_command_end")]
    [DataRow("sql_drop")]
    [DataRow("table_rewrite")]
    [DataRow("login")]
    public void EveryEventIsRead(string eventName)
    {
        // Act
        var trigger = new PgQueryParser().Parse($"CREATE EVENT TRIGGER t ON {eventName} EXECUTE FUNCTION f();").EventTriggers.Single();

        // Assert
        Assert.AreEqual(eventName, trigger.EventName);
    }

    [TestMethod]
    public void AMixedCaseNameAndFunctionKeepTheirCase()
    {
        // Act
        var trigger = new PgQueryParser().Parse("CREATE EVENT TRIGGER \"Mixed Case\" ON sql_drop EXECUTE FUNCTION \"S\".\"Fn\"();").EventTriggers.Single();

        // Assert
        Assert.AreEqual("Mixed Case", trigger.Name);
        Assert.AreEqual("S.Fn", trigger.FunctionName);
    }

    [TestMethod]
    [DataRow("DISABLE", "D")]
    [DataRow("ENABLE", "O")]
    [DataRow("ENABLE REPLICA", "R")]
    [DataRow("ENABLE ALWAYS", "A")]
    public void AnAlterEventTriggerIsReadAsItsMode(string clause, string mode)
    {
        // Act
        var tree = new PgQueryParser().Parse($"ALTER EVENT TRIGGER t {clause};");
        var state = tree.EventTriggerStates.Single();

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual("t", state.Name);
        Assert.AreEqual(mode, state.EnabledMode);
    }

    [TestMethod]
    public void TheCommentOfAnEventTriggerIsStillReportedAsNotModeledAndItsOwnerIsRead()
    {
        // Act
        var tree = new PgQueryParser().Parse("ALTER EVENT TRIGGER t OWNER TO bob; COMMENT ON EVENT TRIGGER t IS 'x';");

        // Assert: the comment is not modeled; the owner is (G-146).
        Assert.AreEqual(1, tree.Diagnostics.Count(static d => d.Severity == SqlDiagnosticSeverity.Warning && d.Message.Contains("is not modeled", StringComparison.Ordinal)));
        var owner = tree.Owners.Single();
        Assert.AreEqual("EventTrigger", owner.ObjectKind);
        Assert.AreEqual("t", owner.ObjectName);
        Assert.AreEqual("bob", owner.OwnerRole);
    }
}
