using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>G-164: a policy's comment is on its definition, and it is stated only by a <c>COMMENT ON POLICY</c>.</summary>
[TestClass]
public sealed class PolicyCommentSemanticsTests
{
    private static DatabaseModel Build(params string[] files) => new SemanticPipeline().Execute(files.Select(static sql => new PgQueryParser().Parse(sql)).ToArray());

    private const string Setup = "CREATE TABLE app.orders (id int PRIMARY KEY, owner_name text); ALTER TABLE app.orders ENABLE ROW LEVEL SECURITY; " +
        "CREATE POLICY own_rows ON app.orders FOR SELECT USING (owner_name = current_user); CREATE POLICY all_rows ON app.orders USING (true);";

    [TestMethod]
    public void AStatedCommentIsOnThePolicyAndMarkedStated()
    {
        var model = Build(Setup + " COMMENT ON POLICY own_rows ON app.orders IS 'Users see their own rows.';");

        var policy = model.Policies.Values.Single(static p => p.Name == "own_rows");
        Assert.AreEqual("Users see their own rows.", policy.Comment);
        Assert.IsTrue(policy.CommentStated);
        Assert.IsFalse(model.Policies.Values.Single(static p => p.Name == "all_rows").CommentStated, "A policy with no statement states nothing.");
    }

    [TestMethod]
    public void NullAndAnEmptyCommentStateNone()
    {
        var model = Build(Setup + " COMMENT ON POLICY own_rows ON app.orders IS NULL; COMMENT ON POLICY all_rows ON app.orders IS '';");

        foreach (var policy in model.Policies.Values)
        {
            Assert.IsNull(policy.Comment, policy.Name);
            Assert.IsTrue(policy.CommentStated, policy.Name);
        }
    }

    [TestMethod]
    public void TheCommentMayBeInAnotherFileAndTheLastStatementWins()
    {
        var model = Build(Setup, "COMMENT ON POLICY own_rows ON app.orders IS 'first';", "COMMENT ON POLICY own_rows ON app.orders IS 'second';");

        Assert.AreEqual("second", model.Policies.Values.Single(static p => p.Name == "own_rows").Comment);
    }

    [TestMethod]
    public void ACommentOnAPolicyTheProjectDoesNotDeclareChangesNothing()
    {
        var model = Build(Setup + " COMMENT ON POLICY nope ON app.orders IS 'x';");

        Assert.IsTrue(model.Policies.Values.All(static p => !p.CommentStated));
    }
}
