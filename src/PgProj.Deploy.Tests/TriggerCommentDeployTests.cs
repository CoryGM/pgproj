using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-163: a trigger's comment is deployed with <c>COMMENT ON TRIGGER</c> after the trigger exists; changing only the comment does not drop the trigger; a trigger that is dropped and created again keeps its
/// comment (the project's, or the database's when the project states none).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TriggerCommentDeployTests
{
    private const string Setup = "CREATE TABLE app.orders (id int PRIMARY KEY); CREATE FUNCTION app.audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$; ";
    private const string Insert = "CREATE TRIGGER audit_orders AFTER INSERT ON app.orders FOR EACH ROW EXECUTE FUNCTION app.audit(); ";
    private const string Update = "CREATE TRIGGER audit_orders AFTER UPDATE ON app.orders FOR EACH ROW EXECUTE FUNCTION app.audit(); ";

    private static string Comment(string text) => $"COMMENT ON TRIGGER audit_orders ON app.orders IS {text};";

    private static List<string> Statements(string live, string project)
    {
        var oldModel = Build(live);
        var newModel = Build(project);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal)).ToList();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    [TestMethod]
    public void ACommentOnlyChangeIsOneCommentOnTriggerAndNothingIsDropped()
    {
        var statements = Statements(Setup + Insert + Comment("'Old.'"), Setup + Insert + Comment("'New.'"));

        CollectionAssert.AreEqual(new[] { "COMMENT ON TRIGGER \"audit_orders\" ON \"app\".\"orders\" IS 'New.';" }, statements);
    }

    [TestMethod]
    public void AStatedNullClearsTheComment()
    {
        var statements = Statements(Setup + Insert + Comment("'Old.'"), Setup + Insert + Comment("NULL"));

        CollectionAssert.AreEqual(new[] { "COMMENT ON TRIGGER \"audit_orders\" ON \"app\".\"orders\" IS NULL;" }, statements);
    }

    [TestMethod]
    public void ANewTriggerIsCreatedBeforeItsCommentIsSet()
    {
        var statements = Statements(Setup, Setup + Insert + Comment("'It''s new.'"));

        var createAt = statements.FindIndex(static s => s.StartsWith("CREATE TRIGGER", StringComparison.Ordinal));
        var commentAt = statements.FindIndex(static s => s.StartsWith("COMMENT ON TRIGGER", StringComparison.Ordinal));
        Assert.IsTrue(createAt >= 0 && createAt < commentAt, string.Join("\n", statements));
        StringAssert.Contains(statements[commentAt], "IS 'It''s new.'");
    }

    [TestMethod]
    public void ANewTriggerWithNoStatementGetsNoComment()
    {
        var statements = Statements(Setup, Setup + Insert);

        Assert.IsFalse(statements.Any(static s => s.StartsWith("COMMENT ON TRIGGER", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void ATriggerRecreatedForAStructuralChangeKeepsTheProjectsComment()
    {
        var statements = Statements(Setup + Insert + Comment("'Old.'"), Setup + Update + Comment("'New.'"));

        var dropAt = statements.FindIndex(static s => s.StartsWith("DROP TRIGGER", StringComparison.Ordinal));
        var createAt = statements.FindIndex(static s => s.StartsWith("CREATE TRIGGER", StringComparison.Ordinal));
        var commentAt = statements.FindIndex(static s => s.StartsWith("COMMENT ON TRIGGER", StringComparison.Ordinal));
        Assert.IsTrue(dropAt >= 0 && dropAt < createAt && createAt < commentAt, string.Join("\n", statements));
        StringAssert.Contains(statements[commentAt], "IS 'New.'");
    }

    [TestMethod]
    public void ATriggerRecreatedWhenTheProjectStatesNoCommentPutsTheDatabasesBack()
    {
        var statements = Statements(Setup + Insert + Comment("'Kept.'"), Setup + Update);

        var commentAt = statements.FindIndex(static s => s.StartsWith("COMMENT ON TRIGGER", StringComparison.Ordinal));
        Assert.IsTrue(commentAt > statements.FindIndex(static s => s.StartsWith("CREATE TRIGGER", StringComparison.Ordinal)), string.Join("\n", statements));
        StringAssert.Contains(statements[commentAt], "IS 'Kept.'");
    }

    [TestMethod]
    public void ATriggerRecreatedWithAStatedNullDoesNotGetTheOldCommentBack()
    {
        var statements = Statements(Setup + Insert + Comment("'Gone.'"), Setup + Update + Comment("NULL"));

        Assert.IsFalse(statements.Any(static s => s.StartsWith("COMMENT ON TRIGGER", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void ACommentOnlyTheDatabaseHasIsNeverCleared()
    {
        Assert.AreEqual(0, Statements(Setup + Insert + Comment("'x'"), Setup + Insert).Count);
    }
}
