using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>G-160: a schema comment the project states is compared with the database's; one the project does not state is not, and a schema is never dropped.</summary>
[TestClass]
[DoNotParallelize]
public sealed class SchemaCommentDiffTests
{
    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static ChangeSet Diff(string live, string project) => new DiffEngine().Diff(Build(live), Build(project));

    private static IEnumerable<ChangeRecord> SchemaChanges(ChangeSet changes)
        => changes.Added.Cast<ChangeRecord>().Concat(changes.Modified).Concat(changes.Removed).Where(static change => change.NodeKind == ChangeNodeKind.SchemaComment);

    [TestMethod]
    public void TheSameCommentIsNoChange()
    {
        var sql = "CREATE SCHEMA app; COMMENT ON SCHEMA app IS 'The app.';";

        Assert.AreEqual(0, SchemaChanges(Diff(sql, sql)).Count());
    }

    [TestMethod]
    public void ACommentTheProjectStatesAndTheDatabaseLacksIsAdded()
    {
        var changes = Diff("CREATE SCHEMA app;", "CREATE SCHEMA app; COMMENT ON SCHEMA app IS 'The app.';");

        var added = changes.Added.Single(static change => change.NodeKind == ChangeNodeKind.SchemaComment);
        Assert.AreEqual("app", ((SchemaDefinition)added.Node).Name);
    }

    [TestMethod]
    public void ACommentOnASchemaTheDatabaseLacksIsAdded()
    {
        var added = Diff(string.Empty, "COMMENT ON SCHEMA app IS 'The app.';").Added.Single(static change => change.NodeKind == ChangeNodeKind.SchemaComment);

        Assert.AreEqual("app", ((SchemaDefinition)added.Node).Name);
    }

    [TestMethod]
    public void ADifferentCommentIsModifiedWithTheProperty()
    {
        var changes = Diff("COMMENT ON SCHEMA app IS 'Old.';", "COMMENT ON SCHEMA app IS 'New.';");

        var modified = SchemaChanges(changes).Cast<ModifiedChange>().Single();
        Assert.AreEqual(nameof(SchemaDefinition.Comment), modified.Details.Single().PropertyName);
    }

    [TestMethod]
    public void AStatedNullClearsTheDatabasesComment()
    {
        var changes = Diff("COMMENT ON SCHEMA app IS 'Old.';", "COMMENT ON SCHEMA app IS NULL;");

        var modified = SchemaChanges(changes).Cast<ModifiedChange>().Single();
        Assert.IsNull(((SchemaDefinition)modified.NewNode).Comment);
    }

    [TestMethod]
    public void AStatedNullForASchemaTheDatabaseHasNoCommentOnIsNoChange()
    {
        Assert.AreEqual(0, SchemaChanges(Diff("CREATE SCHEMA app;", "CREATE SCHEMA app; COMMENT ON SCHEMA app IS NULL;")).Count());
    }

    [TestMethod]
    public void ACommentOnlyTheDatabaseHasIsNotARemoval()
    {
        var changes = Diff("CREATE SCHEMA app; COMMENT ON SCHEMA app IS 'Old.';", "CREATE SCHEMA app;");

        Assert.AreEqual(0, SchemaChanges(changes).Count(), "A project that does not mention a schema's comment leaves it alone.");
        Assert.AreEqual(0, changes.Removed.Count);
    }

    [TestMethod]
    public void TheModelsNeverReportARemovedSchemaDefinition()
    {
        var result = new DiffEngine().Execute(Build("COMMENT ON SCHEMA app IS 'x';"), Build("CREATE SCHEMA other;"));

        Assert.AreEqual(0, result.RemovedSchemaDefinitions.Count);
    }
}
