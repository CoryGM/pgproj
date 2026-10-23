using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>G-161: a constraint's comment is on its definition, and it is stated only by a <c>COMMENT ON CONSTRAINT</c>.</summary>
[TestClass]
public sealed class ConstraintCommentSemanticsTests
{
    private static DatabaseModel Build(params string[] files) => new SemanticPipeline().Execute(files.Select(static sql => new PgQueryParser().Parse(sql)).ToArray());

    private const string Orders = "CREATE TABLE app.orders (id int, total int, CONSTRAINT orders_pkey PRIMARY KEY (id), CONSTRAINT orders_total_ck CHECK (total >= 0));";

    [TestMethod]
    public void AStatedCommentIsOnTheConstraintAndMarkedStated()
    {
        var model = Build(Orders + " COMMENT ON CONSTRAINT orders_total_ck ON app.orders IS 'Never negative.';");

        var constraint = model.Constraints.Values.Single(static c => c.Name == "orders_total_ck");
        Assert.AreEqual("Never negative.", constraint.Comment);
        Assert.IsTrue(constraint.CommentStated);
        var other = model.Constraints.Values.Single(static c => c.Name == "orders_pkey");
        Assert.IsNull(other.Comment);
        Assert.IsFalse(other.CommentStated, "A constraint with no statement states nothing.");
    }

    [TestMethod]
    public void NullAndAnEmptyCommentStateNone()
    {
        var model = Build(Orders + " COMMENT ON CONSTRAINT orders_pkey ON app.orders IS NULL; COMMENT ON CONSTRAINT orders_total_ck ON app.orders IS '';");

        foreach (var constraint in model.Constraints.Values)
        {
            Assert.IsNull(constraint.Comment, constraint.Name);
            Assert.IsTrue(constraint.CommentStated, constraint.Name);
        }
    }

    [TestMethod]
    public void TheCommentMayBeInAnotherFileAndTheLastStatementWins()
    {
        var model = Build(Orders, "COMMENT ON CONSTRAINT orders_pkey ON app.orders IS 'first';", "COMMENT ON CONSTRAINT orders_pkey ON app.orders IS 'second';");

        Assert.AreEqual("second", model.Constraints.Values.Single(static c => c.Name == "orders_pkey").Comment);
    }

    [TestMethod]
    public void AnAddedConstraintAndAGeneratedNameTakeAComment()
    {
        var model = Build("CREATE TABLE app.t (id int PRIMARY KEY); ALTER TABLE app.t ADD CONSTRAINT t_id_pos CHECK (id > 0); COMMENT ON CONSTRAINT t_id_pos ON app.t IS 'Added later.'; COMMENT ON CONSTRAINT t_pkey ON app.t IS 'The generated name.';");

        Assert.AreEqual("Added later.", model.Constraints.Values.Single(static c => c.Name == "t_id_pos").Comment);
        Assert.AreEqual("The generated name.", model.Constraints.Values.Single(static c => c.Name == "t_pkey").Comment);
    }

    [TestMethod]
    public void ACommentOnAConstraintTheProjectDoesNotDeclareChangesNothing()
    {
        var model = Build(Orders + " COMMENT ON CONSTRAINT nope ON app.orders IS 'x';");

        Assert.IsTrue(model.Constraints.Values.All(static c => !c.CommentStated));
    }
}
