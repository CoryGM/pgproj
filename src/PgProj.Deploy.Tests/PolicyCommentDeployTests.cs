using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-164: a policy's comment is deployed with <c>COMMENT ON POLICY</c> after the policy exists; changing only the comment does not drop the policy; a policy that is dropped and created again keeps its comment
/// (the project's, or the database's when the project states none).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PolicyCommentDeployTests
{
    private const string Setup = "CREATE TABLE app.orders (id int PRIMARY KEY, owner_name text); ALTER TABLE app.orders ENABLE ROW LEVEL SECURITY; ";
    private const string Select = "CREATE POLICY own_rows ON app.orders FOR SELECT USING (owner_name = current_user); ";
    private const string Update = "CREATE POLICY own_rows ON app.orders FOR UPDATE USING (owner_name = current_user); ";

    private static string Comment(string text) => $"COMMENT ON POLICY own_rows ON app.orders IS {text};";

    private static List<string> Statements(string live, string project)
    {
        var oldModel = Build(live);
        var newModel = Build(project);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal)).ToList();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    [TestMethod]
    public void ACommentOnlyChangeIsOneCommentOnPolicyAndNothingIsDropped()
    {
        var statements = Statements(Setup + Select + Comment("'Old.'"), Setup + Select + Comment("'New.'"));

        CollectionAssert.AreEqual(new[] { "COMMENT ON POLICY \"own_rows\" ON \"app\".\"orders\" IS 'New.';" }, statements);
    }

    [TestMethod]
    public void AStatedNullClearsTheComment()
    {
        var statements = Statements(Setup + Select + Comment("'Old.'"), Setup + Select + Comment("NULL"));

        CollectionAssert.AreEqual(new[] { "COMMENT ON POLICY \"own_rows\" ON \"app\".\"orders\" IS NULL;" }, statements);
    }

    [TestMethod]
    public void ANewPolicyIsCreatedBeforeItsCommentIsSet()
    {
        var statements = Statements(Setup, Setup + Select + Comment("'It''s new.'"));

        var createAt = statements.FindIndex(static s => s.StartsWith("CREATE POLICY", StringComparison.Ordinal));
        var commentAt = statements.FindIndex(static s => s.StartsWith("COMMENT ON POLICY", StringComparison.Ordinal));
        Assert.IsTrue(createAt >= 0 && createAt < commentAt, string.Join("\n", statements));
        StringAssert.Contains(statements[commentAt], "IS 'It''s new.'");
    }

    [TestMethod]
    public void ANewPolicyWithNoStatementGetsNoComment()
    {
        var statements = Statements(Setup, Setup + Select);

        Assert.IsFalse(statements.Any(static s => s.StartsWith("COMMENT ON POLICY", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void APolicyRecreatedForAStructuralChangeKeepsTheProjectsComment()
    {
        var statements = Statements(Setup + Select + Comment("'Old.'"), Setup + Update + Comment("'New.'"));

        var dropAt = statements.FindIndex(static s => s.StartsWith("DROP POLICY", StringComparison.Ordinal));
        var createAt = statements.FindIndex(static s => s.StartsWith("CREATE POLICY", StringComparison.Ordinal));
        var commentAt = statements.FindIndex(static s => s.StartsWith("COMMENT ON POLICY", StringComparison.Ordinal));
        Assert.IsTrue(dropAt >= 0 && dropAt < createAt && createAt < commentAt, string.Join("\n", statements));
        StringAssert.Contains(statements[commentAt], "IS 'New.'");
    }

    [TestMethod]
    public void APolicyRecreatedWhenTheProjectStatesNoCommentPutsTheDatabasesBack()
    {
        var statements = Statements(Setup + Select + Comment("'Kept.'"), Setup + Update);

        var commentAt = statements.FindIndex(static s => s.StartsWith("COMMENT ON POLICY", StringComparison.Ordinal));
        Assert.IsTrue(commentAt > statements.FindIndex(static s => s.StartsWith("CREATE POLICY", StringComparison.Ordinal)), string.Join("\n", statements));
        StringAssert.Contains(statements[commentAt], "IS 'Kept.'");
    }

    [TestMethod]
    public void APolicyRecreatedWithAStatedNullDoesNotGetTheOldCommentBack()
    {
        var statements = Statements(Setup + Select + Comment("'Gone.'"), Setup + Update + Comment("NULL"));

        Assert.IsFalse(statements.Any(static s => s.StartsWith("COMMENT ON POLICY", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void ACommentOnlyTheDatabaseHasIsNeverCleared()
    {
        Assert.AreEqual(0, Statements(Setup + Select + Comment("'x'"), Setup + Select).Count);
    }
}
