using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>G-164: a policy comment the project states is compared with the database's; one the project does not state is not.</summary>
[TestClass]
[DoNotParallelize]
public sealed class PolicyCommentDiffTests
{
    private const string Setup = "CREATE TABLE app.orders (id int PRIMARY KEY, owner_name text); ALTER TABLE app.orders ENABLE ROW LEVEL SECURITY; " +
        "CREATE POLICY own_rows ON app.orders FOR SELECT USING (owner_name = current_user); ";

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static ChangeSet Diff(string live, string project) => new DiffEngine().Diff(Build(live), Build(project));

    private static string Comment(string text) => $"COMMENT ON POLICY own_rows ON app.orders IS {text};";

    [TestMethod]
    public void TheSameCommentIsNoChange()
    {
        var sql = Setup + Comment("'x'");

        var changes = Diff(sql, sql);

        Assert.AreEqual(0, changes.Added.Count + changes.Modified.Count + changes.Removed.Count);
    }

    [TestMethod]
    public void AStatedCommentOnAPolicyWithNoneIsAModificationOfTheCommentOnly()
    {
        var changes = Diff(Setup, Setup + Comment("'Users see their own rows.'"));

        var modified = changes.Modified.Single();
        Assert.AreEqual(ChangeNodeKind.Policy, modified.NodeKind);
        Assert.AreEqual(nameof(PolicyDefinition.Comment), modified.Details.Single().PropertyName);
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
    public void AStatedNullForAPolicyWithNoCommentIsNoChange()
    {
        Assert.AreEqual(0, Diff(Setup, Setup + Comment("NULL")).Modified.Count);
    }
}
