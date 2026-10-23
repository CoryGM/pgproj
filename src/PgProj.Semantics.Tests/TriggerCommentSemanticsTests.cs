using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>G-163: a trigger's comment is on its definition, and it is stated only by a <c>COMMENT ON TRIGGER</c>.</summary>
[TestClass]
public sealed class TriggerCommentSemanticsTests
{
    private static DatabaseModel Build(params string[] files) => new SemanticPipeline().Execute(files.Select(static sql => new PgQueryParser().Parse(sql)).ToArray());

    private const string Setup = "CREATE TABLE app.orders (id int PRIMARY KEY); CREATE FUNCTION app.audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$; " +
        "CREATE TRIGGER audit_orders AFTER INSERT ON app.orders FOR EACH ROW EXECUTE FUNCTION app.audit(); " +
        "CREATE TRIGGER audit_two AFTER UPDATE ON app.orders FOR EACH ROW EXECUTE FUNCTION app.audit();";

    [TestMethod]
    public void AStatedCommentIsOnTheTriggerAndMarkedStated()
    {
        var model = Build(Setup + " COMMENT ON TRIGGER audit_orders ON app.orders IS 'Writes the audit row.';");

        var trigger = model.Triggers.Values.Single(static t => t.Name == "audit_orders");
        Assert.AreEqual("Writes the audit row.", trigger.Comment);
        Assert.IsTrue(trigger.CommentStated);
        Assert.IsFalse(model.Triggers.Values.Single(static t => t.Name == "audit_two").CommentStated, "A trigger with no statement states nothing.");
    }

    [TestMethod]
    public void NullAndAnEmptyCommentStateNone()
    {
        var model = Build(Setup + " COMMENT ON TRIGGER audit_orders ON app.orders IS NULL; COMMENT ON TRIGGER audit_two ON app.orders IS '';");

        foreach (var trigger in model.Triggers.Values)
        {
            Assert.IsNull(trigger.Comment, trigger.Name);
            Assert.IsTrue(trigger.CommentStated, trigger.Name);
        }
    }

    [TestMethod]
    public void TheCommentMayBeInAnotherFileAndTheLastStatementWins()
    {
        var model = Build(Setup, "COMMENT ON TRIGGER audit_orders ON app.orders IS 'first';", "COMMENT ON TRIGGER audit_orders ON app.orders IS 'second';");

        Assert.AreEqual("second", model.Triggers.Values.Single(static t => t.Name == "audit_orders").Comment);
    }

    [TestMethod]
    public void ACommentOnATriggerTheProjectDoesNotDeclareChangesNothing()
    {
        var model = Build(Setup + " COMMENT ON TRIGGER nope ON app.orders IS 'x';");

        Assert.IsTrue(model.Triggers.Values.All(static t => !t.CommentStated));
    }
}
