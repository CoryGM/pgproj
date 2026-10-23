using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-160: a schema's comment is deployed with <c>COMMENT ON SCHEMA</c>: set, changed, and cleared; a new schema is created first; a comment on a schema the database already has needs no create to be stated.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SchemaCommentDeployTests
{
    private static List<string> Statements(string live, string project)
    {
        var oldModel = Build(live);
        var newModel = Build(project);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations.Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal)).ToList();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    [TestMethod]
    public void AChangedCommentIsOneCommentOnSchemaStatement()
    {
        var statements = Statements("COMMENT ON SCHEMA app IS 'Old.';", "COMMENT ON SCHEMA app IS 'New.';");

        CollectionAssert.AreEqual(new[] { "COMMENT ON SCHEMA \"app\" IS 'New.';" }, statements);
    }

    [TestMethod]
    public void AQuoteInTheCommentIsDoubledAndALineBreakIsKept()
    {
        var statements = Statements("CREATE SCHEMA app;", "CREATE SCHEMA app; COMMENT ON SCHEMA app IS $$It's here.\n\n@owner: Sales$$;");

        Assert.AreEqual("COMMENT ON SCHEMA \"app\" IS 'It''s here.\n\n@owner: Sales';", statements[^1]);
        Assert.IsTrue(statements.Count <= 2, "At most the harmless CREATE SCHEMA IF NOT EXISTS before it: " + string.Join("\n", statements));
    }

    [TestMethod]
    public void AStatedNullClearsTheComment()
    {
        var statements = Statements("COMMENT ON SCHEMA app IS 'Old.';", "COMMENT ON SCHEMA app IS NULL;");

        CollectionAssert.AreEqual(new[] { "COMMENT ON SCHEMA \"app\" IS NULL;" }, statements);
    }

    [TestMethod]
    public void ANewSchemaIsCreatedBeforeItsCommentIsSet()
    {
        var statements = Statements(string.Empty, "COMMENT ON SCHEMA app IS 'The app.'; CREATE TABLE app.t (id int);");

        var createAt = statements.FindIndex(static s => s.StartsWith("CREATE SCHEMA", StringComparison.Ordinal));
        var commentAt = statements.FindIndex(static s => s.StartsWith("COMMENT ON SCHEMA", StringComparison.Ordinal));
        Assert.IsTrue(createAt >= 0 && createAt < commentAt, string.Join("\n", statements));
    }

    [TestMethod]
    public void ACommentOnASchemaWithNoObjectsStillCreatesTheSchema()
    {
        var statements = Statements(string.Empty, "COMMENT ON SCHEMA reports IS 'Nothing here yet.';");

        Assert.IsTrue(statements.Any(static s => s.StartsWith("CREATE SCHEMA IF NOT EXISTS \"reports\"", StringComparison.Ordinal)), string.Join("\n", statements));
        CollectionAssert.Contains(statements, "COMMENT ON SCHEMA \"reports\" IS 'Nothing here yet.';");
    }

    [TestMethod]
    public void TheSameCommentGeneratesNothing()
    {
        var sql = "CREATE SCHEMA app; COMMENT ON SCHEMA app IS 'x';";

        Assert.AreEqual(0, Statements(sql, sql).Count);
    }

    [TestMethod]
    public void ACommentOnlyTheDatabaseHasIsNeverCleared()
    {
        Assert.AreEqual(0, Statements("CREATE SCHEMA app; COMMENT ON SCHEMA app IS 'x';", "CREATE SCHEMA app;").Count);
    }
}
