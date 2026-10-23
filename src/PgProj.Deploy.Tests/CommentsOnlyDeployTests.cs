using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-169: <c>publish --comments-only</c> keeps only the <c>COMMENT ON</c> statements of the generated operations, each in a <c>DO</c> block that
/// skips it (with a warning) when the object is not in the database, and drops everything else, including the pre- and post-deploy scripts.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CommentsOnlyDeployTests
{
    private const string Table = "CREATE SCHEMA app; CREATE TABLE app.t (id int PRIMARY KEY, n text);";

    [TestMethod]
    public void ACommentOnlyChangeIsTheOnlyStatement()
    {
        var statements = CommentsOnly(Table, Table + " COMMENT ON TABLE app.t IS 'Things.'; COMMENT ON COLUMN app.t.n IS 'The name.';");

        Assert.HasCount(2, statements);
        Assert.IsTrue(statements.Any(static s => s.Contains("COMMENT ON TABLE", StringComparison.Ordinal)), string.Join("\n", statements));
        Assert.IsTrue(statements.Any(static s => s.Contains("COMMENT ON COLUMN", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void OtherChangesAreLeftOut()
    {
        var statements = CommentsOnly(Table, Table + " ALTER TABLE app.t ADD COLUMN extra int; CREATE INDEX t_n ON app.t (n); COMMENT ON TABLE app.t IS 'Things.';");

        Assert.HasCount(1, statements);
        Assert.IsFalse(statements.Any(static s => s.Contains("ADD COLUMN", StringComparison.Ordinal) || s.Contains("CREATE INDEX", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void TheCommentsOfANewTableAreKeptSoTheDatabaseCanSkipThem()
    {
        var statements = CommentsOnly("CREATE SCHEMA app;", Table + " COMMENT ON TABLE app.t IS 'Things.'; COMMENT ON COLUMN app.t.n IS 'The name.';");

        Assert.HasCount(2, statements);
        Assert.IsFalse(statements.Any(static s => s.Contains("CREATE TABLE", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void EachStatementRunsInADoBlockThatSkipsAMissingObjectWithAWarningNamingIt()
    {
        var sql = CommentsOnly(Table, Table + " COMMENT ON TABLE app.t IS 'Things.';").Single();

        Assert.AreEqual(
            "DO $pgproj_comments_only$\nBEGIN\n    COMMENT ON TABLE \"app\".\"t\" IS 'Things.';\nEXCEPTION WHEN undefined_table OR undefined_column OR undefined_object OR undefined_function OR invalid_schema_name THEN\n    RAISE WARNING 'pgproj publish --comments-only skipped a comment: % (%)', 'COMMENT ON TABLE \"app\".\"t\"', SQLERRM;\nEND\n$pgproj_comments_only$;",
            sql);
    }

    [TestMethod]
    public void ACommentWithQuotesAndDollarQuotingSurvivesAndTheWarningNamesTheTarget()
    {
        var sql = CommentsOnly(Table, Table + " COMMENT ON COLUMN app.t.n IS $$It's the name.\n\n@pii: true$$;").Single();

        StringAssert.Contains(sql, "IS 'It''s the name.\n\n@pii: true';");
        StringAssert.Contains(sql, "'COMMENT ON COLUMN \"app\".\"t\".\"n\"'");
        Assert.HasCount(1, SqlStatementSplitter.Split(sql), "The DO block splits as one statement.");
    }

    [TestMethod]
    public void AClearedCommentIsKept()
    {
        var statements = CommentsOnly(Table + " COMMENT ON TABLE app.t IS 'Things.';", Table + " COMMENT ON TABLE app.t IS NULL;");

        Assert.HasCount(1, statements);
        StringAssert.Contains(statements[0], "IS NULL");
    }

    [TestMethod]
    public void NoOperationOrScriptSurvivesWhenThereIsNoComment()
    {
        var batch = Generate(Table, Table + " ALTER TABLE app.t ADD COLUMN extra int;").MergePreDeploymentScript("SELECT 1;").MergePostDeploymentScript("SELECT 2;");

        var commentsOnly = CommentStatementOperation.CommentsOnly(batch);

        Assert.HasCount(0, commentsOnly.Operations);
        Assert.HasCount(0, commentsOnly.PreDeploymentScripts);
        Assert.HasCount(0, commentsOnly.PostDeploymentScripts);
        Assert.AreEqual(string.Empty, new ScriptGenerator().GenerateSqlScript(commentsOnly));
    }

    [TestMethod]
    public void ACommentInAPostDeployScriptIsNotIncluded()
    {
        var batch = Generate(Table, Table + " COMMENT ON TABLE app.t IS 'Things.';").MergePostDeploymentScript("COMMENT ON TABLE app.t IS 'From the script.';");

        var script = new ScriptGenerator().GenerateSqlScript(CommentStatementOperation.CommentsOnly(batch));

        StringAssert.Contains(script, "Things.");
        Assert.IsFalse(script.Contains("From the script.", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void NoCommentStatementIsClassifiedAsDestructiveOrUnsupported()
    {
        var batch = CommentStatementOperation.CommentsOnly(Generate(Table + " COMMENT ON TABLE app.t IS 'Old.';", "CREATE SCHEMA app; COMMENT ON TABLE app.t IS NULL;"));

        Assert.HasCount(0, batch.GetDestructiveOperations());
        Assert.HasCount(0, batch.GetUnsupportedOperations());
    }

    [TestMethod]
    public void TheCommentOfANewColumnOnAnExistingTableIsSetWithTheColumn()
    {
        // G-170: the ADD COLUMN used to leave the comment for the next publish.
        var script = new ScriptGenerator().GenerateSqlScript(Generate(Table, Table + " ALTER TABLE app.t ADD COLUMN x text; COMMENT ON COLUMN app.t.x IS 'Hi.';"));

        var add = script.IndexOf("ADD COLUMN \"x\"", StringComparison.Ordinal);
        var comment = script.IndexOf("COMMENT ON COLUMN \"app\".\"t\".\"x\" IS 'Hi.';", StringComparison.Ordinal);
        Assert.IsTrue(add >= 0 && comment > add, script);
    }

    [TestMethod]
    public void ANewColumnWithoutACommentGetsNoCommentStatement()
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(Table, Table + " ALTER TABLE app.t ADD COLUMN x text;"));

        Assert.IsFalse(script.Contains("COMMENT ON", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void TheCommentOfANewColumnIsACommentStatementOfTheCommentsOnlyScript()
    {
        var statements = CommentsOnly(Table, Table + " ALTER TABLE app.t ADD COLUMN x text; COMMENT ON COLUMN app.t.x IS 'Hi.';");

        Assert.HasCount(1, statements);
        StringAssert.Contains(statements[0], "COMMENT ON COLUMN \"app\".\"t\".\"x\"");
    }
    [TestMethod]
    public void TheLeadingReasonLinesOfAStatementAreIgnored()
    {
        Assert.AreEqual("COMMENT ON TABLE a IS 'x'", CommentStatementOperation.WithoutLeadingComments("-- why\n/* more */\n  COMMENT ON TABLE a IS 'x'"));
    }

    private static List<string> CommentsOnly(string oldSql, string newSql)
        => CommentStatementOperation.CommentsOnly(Generate(oldSql, newSql)).Operations
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static OperationBatch Generate(string oldSql, string newSql)
    {
        var oldModel = Build(oldSql);
        var newModel = Build(newSql);
        return new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
