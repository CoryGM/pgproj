namespace PgProj.Core.Tests;

/// <summary>G-216: the comments on a live view's columns and a composite type's fields are written after it, in the snapshot and the scaffold.</summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveRelationColumnCommentTests
{
    private static readonly LiveViewSnapshot View = new("public", "v", " SELECT 1 AS a;") { ColumnCommentStatements = ["COMMENT ON COLUMN public.v.a IS 'view a'"] };

    private static readonly LiveTypeSnapshot Composite = new("public", "ct", "user-defined", "CREATE TYPE public.ct AS (x integer)") { ColumnCommentStatements = ["COMMENT ON COLUMN public.ct.x IS 'field x'"] };

    [TestMethod]
    public void TheSnapshotCarriesTheComments()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [], [], [], views: [View], types: [Composite]);

        StringAssert.Contains(sql, "COMMENT ON COLUMN public.v.a IS 'view a';");
        StringAssert.Contains(sql, "COMMENT ON COLUMN public.ct.x IS 'field x';");
    }

    [TestMethod]
    public void TheScaffoldWritesThemInTheObjectsFile()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [], [], [], [View], [], [Composite], [], [], [], [])
            .ToDictionary(static file => file.RelativePath, static file => file.Content);

        StringAssert.Contains(files["Schema/public/View/v.sql"], "COMMENT ON COLUMN public.v.a IS 'view a';");
        StringAssert.Contains(files["Schema/public/Type/ct.sql"], "COMMENT ON COLUMN public.ct.x IS 'field x';");
    }
}
