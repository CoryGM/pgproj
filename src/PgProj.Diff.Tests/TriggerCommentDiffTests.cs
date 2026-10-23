using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>G-163: a trigger comment the project states is compared with the database's; one the project does not state is not.</summary>
[TestClass]
[DoNotParallelize]
public sealed class TriggerCommentDiffTests
{
    private const string Setup = "CREATE TABLE app.orders (id int PRIMARY KEY); CREATE FUNCTION app.audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$; " +
        "CREATE TRIGGER audit_orders AFTER INSERT ON app.orders FOR EACH ROW EXECUTE FUNCTION app.audit(); ";

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static ChangeSet Diff(string live, string project) => new DiffEngine().Diff(Build(live), Build(project));

    private static string Comment(string text) => $"COMMENT ON TRIGGER audit_orders ON app.orders IS {text};";

    [TestMethod]
    public void TheSameCommentIsNoChange()
    {
        var sql = Setup + Comment("'x'");

        var changes = Diff(sql, sql);

        Assert.AreEqual(0, changes.Added.Count + changes.Modified.Count + changes.Removed.Count);
    }

    [TestMethod]
    public void AStatedCommentOnATriggerWithNoneIsAModificationOfTheCommentOnly()
    {
        var changes = Diff(Setup, Setup + Comment("'Writes the audit row.'"));

        var modified = changes.Modified.Single();
        Assert.AreEqual(ChangeNodeKind.Trigger, modified.NodeKind);
        Assert.AreEqual(nameof(TriggerDefinition.Comment), modified.Details.Single().PropertyName);
    }

    [TestMethod]
    public void ADifferentCommentAndAStatedNullAreModifications()
    {
        Assert.AreEqual(1, Diff(Setup + Comment("'Old.'"), Setup + Comment("'New.'")).Modified.Count);
        Assert.AreEqual(1, Diff(Setup + Comment("'Old.'"), Setup + Comment("NULL")).Modified.Count);
    }

    [TestMethod]
    public void ACommentTheProjectDoesNotStateIsLeftAlone()
    {
        var changes = Diff(Setup + Comment("'Old.'"), Setup);

        Assert.AreEqual(0, changes.Added.Count + changes.Modified.Count + changes.Removed.Count);
    }

    [TestMethod]
    public void AStatedNullForATriggerWithNoCommentIsNoChange()
    {
        Assert.AreEqual(0, Diff(Setup, Setup + Comment("NULL")).Modified.Count);
    }
}
