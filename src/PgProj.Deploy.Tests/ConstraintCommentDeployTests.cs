using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-161: a constraint's comment is deployed with <c>COMMENT ON CONSTRAINT</c> after the constraint exists; changing only the comment does not drop the constraint; a constraint that is dropped and added again
/// keeps its comment (the project's, or the database's when the project states none).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ConstraintCommentDeployTests
{
    private const string Table = "CREATE TABLE app.orders (id int, total int, CONSTRAINT orders_pkey PRIMARY KEY (id)); ";
    private const string Check = "ALTER TABLE app.orders ADD CONSTRAINT orders_total_ck CHECK (total >= 0); ";

    private static string Comment(string text) => $"COMMENT ON CONSTRAINT orders_total_ck ON app.orders IS {text};";

    private static List<string> Statements(string live, string project)
    {
        var oldModel = Build(live);
        var newModel = Build(project);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal)).ToList();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    [TestMethod]
    public void ACommentOnlyChangeIsOneCommentOnConstraintAndNothingIsDropped()
    {
        var statements = Statements(Table + Check + Comment("'Old.'"), Table + Check + Comment("'New.'"));

        CollectionAssert.AreEqual(new[] { "COMMENT ON CONSTRAINT \"orders_total_ck\" ON \"app\".\"orders\" IS 'New.';" }, statements);
    }

    [TestMethod]
    public void AStatedNullClearsTheComment()
    {
        var statements = Statements(Table + Check + Comment("'Old.'"), Table + Check + Comment("NULL"));

        CollectionAssert.AreEqual(new[] { "COMMENT ON CONSTRAINT \"orders_total_ck\" ON \"app\".\"orders\" IS NULL;" }, statements);
    }

    [TestMethod]
    public void ANewConstraintIsAddedBeforeItsCommentIsSet()
    {
        var statements = Statements(Table, Table + Check + Comment("'It''s new.'"));

        var addAt = statements.FindIndex(static s => s.Contains("ADD CONSTRAINT \"orders_total_ck\"", StringComparison.Ordinal));
        var commentAt = statements.FindIndex(static s => s.StartsWith("COMMENT ON CONSTRAINT", StringComparison.Ordinal));
        Assert.IsTrue(addAt >= 0 && addAt < commentAt, string.Join("\n", statements));
        StringAssert.Contains(statements[commentAt], "IS 'It''s new.'");
    }

    [TestMethod]
    public void ANewConstraintWithNoStatementGetsNoComment()
    {
        var statements = Statements(Table, Table + Check);

        Assert.IsFalse(statements.Any(static s => s.StartsWith("COMMENT ON CONSTRAINT", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void AConstraintRecreatedForAStructuralChangeKeepsTheProjectsComment()
    {
        var statements = Statements(Table + Check + Comment("'Old.'"), Table + Check.Replace(">= 0", ">= 1", StringComparison.Ordinal) + Comment("'New.'"));

        var dropAt = statements.FindIndex(static s => s.Contains("DROP CONSTRAINT", StringComparison.Ordinal));
        var addAt = statements.FindIndex(static s => s.Contains("ADD CONSTRAINT \"orders_total_ck\"", StringComparison.Ordinal));
        var commentAt = statements.FindIndex(static s => s.StartsWith("COMMENT ON CONSTRAINT", StringComparison.Ordinal));
        Assert.IsTrue(dropAt >= 0 && dropAt < addAt && addAt < commentAt, string.Join("\n", statements));
        StringAssert.Contains(statements[commentAt], "IS 'New.'");
    }

    [TestMethod]
    public void AConstraintRecreatedWhenTheProjectStatesNoCommentPutsTheDatabasesBack()
    {
        var statements = Statements(Table + Check + Comment("'Kept.'"), Table + Check.Replace(">= 0", ">= 1", StringComparison.Ordinal));

        var commentAt = statements.FindIndex(static s => s.StartsWith("COMMENT ON CONSTRAINT", StringComparison.Ordinal));
        Assert.IsTrue(commentAt > statements.FindIndex(static s => s.Contains("ADD CONSTRAINT", StringComparison.Ordinal)), string.Join("\n", statements));
        StringAssert.Contains(statements[commentAt], "IS 'Kept.'");
    }

    [TestMethod]
    public void AConstraintRecreatedWithAStatedNullDoesNotGetTheOldCommentBack()
    {
        var statements = Statements(Table + Check + Comment("'Gone.'"), Table + Check.Replace(">= 0", ">= 1", StringComparison.Ordinal) + Comment("NULL"));

        Assert.IsFalse(statements.Any(static s => s.StartsWith("COMMENT ON CONSTRAINT", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void ACommentOnlyTheDatabaseHasIsNeverCleared()
    {
        Assert.AreEqual(0, Statements(Table + Check + Comment("'x'"), Table + Check).Count);
    }
}
