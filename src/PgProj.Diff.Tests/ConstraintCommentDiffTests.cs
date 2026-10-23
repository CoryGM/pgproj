using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>G-161: a constraint comment the project states is compared with the database's; one the project does not state is not.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ConstraintCommentDiffTests
{
    private const string Orders = "CREATE TABLE app.orders (id int, total int, CONSTRAINT orders_pkey PRIMARY KEY (id), CONSTRAINT orders_total_ck CHECK (total >= 0)); ";

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static ChangeSet Diff(string live, string project) => new DiffEngine().Diff(Build(live), Build(project));

    private static string Comment(string text) => $"COMMENT ON CONSTRAINT orders_total_ck ON app.orders IS {text};";

    [TestMethod]
    public void TheSameCommentIsNoChange()
    {
        var sql = Orders + Comment("'x'");

        var changes = Diff(sql, sql);

        Assert.AreEqual(0, changes.Added.Count + changes.Modified.Count + changes.Removed.Count);
    }

    [TestMethod]
    public void AStatedCommentOnAConstraintWithNoneIsAModificationOfTheCommentOnly()
    {
        var changes = Diff(Orders, Orders + Comment("'Never negative.'"));

        var modified = changes.Modified.Single();
        Assert.AreEqual(ChangeNodeKind.Constraint, modified.NodeKind);
        Assert.AreEqual(nameof(ConstraintDefinition.Comment), modified.Details.Single().PropertyName);
    }

    [TestMethod]
    public void ADifferentCommentAndAStatedNullAreModifications()
    {
        Assert.AreEqual(1, Diff(Orders + Comment("'Old.'"), Orders + Comment("'New.'")).Modified.Count);
        Assert.AreEqual(1, Diff(Orders + Comment("'Old.'"), Orders + Comment("NULL")).Modified.Count);
    }

    [TestMethod]
    public void ACommentTheProjectDoesNotStateIsLeftAlone()
    {
        var changes = Diff(Orders + Comment("'Old.'"), Orders);

        Assert.AreEqual(0, changes.Added.Count + changes.Modified.Count + changes.Removed.Count);
    }

    [TestMethod]
    public void AStatedNullForAConstraintWithNoCommentIsNoChange()
    {
        var changes = Diff(Orders, Orders + Comment("NULL"));

        Assert.AreEqual(0, changes.Modified.Count);
    }

    [TestMethod]
    public void AStructuralChangeAndACommentChangeAreReportedTogether()
    {
        var changes = Diff(Orders + Comment("'Old.'"), Orders.Replace("total >= 0", "total >= 1", StringComparison.Ordinal) + Comment("'New.'"));

        var details = changes.Modified.Single(static change => change.NodeKind == ChangeNodeKind.Constraint).Details.Select(static detail => detail.PropertyName).ToArray();
        CollectionAssert.Contains(details, nameof(ConstraintDefinition.Comment));
        Assert.IsTrue(details.Length > 1, string.Join(", ", details));
    }
}
