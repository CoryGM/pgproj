using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-154: a live comment and owner of a collation, extended statistics object, or text search dictionary or configuration become statements the parser reads back as the
/// same model, and <c>init</c> writes them in the file of the object.
/// </summary>
[TestClass]
public sealed class NewKindOwnerAndCommentLiveTests
{
    private static readonly LiveTableSnapshot Table = new("app", "t", [new LiveColumnSnapshot("a", "integer", true, null, false, null, null), new LiveColumnSnapshot("b", "integer", true, null, false, null, null)]);

    private static LiveCollationSnapshot Collation() => new("app", "ci", "icu", null, null, "und", true, null, "It's a collation");

    private static LiveStatisticsSnapshot Statistics() => new("app", "st", "app", "t", "CREATE STATISTICS app.st ON a, b FROM app.t", "stats");

    private static LiveTextSearchSnapshot TextSearch() => new(
        [new LiveTextSearchDictionarySnapshot("app", "d", "pg_catalog", "simple", string.Empty, "dictionary")],
        [new LiveTextSearchConfigurationSnapshot("app", "c", "pg_catalog", "default", [], "configuration")]);

    [TestMethod]
    public void TheSnapshotCarriesEachComment()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("app")], [Table], [], [], collations: [Collation()], statistics: [Statistics()], textSearch: TextSearch());
        var model = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);

        Assert.AreEqual("It's a collation", model.Collations["collation:app.ci"].Comment);
        Assert.AreEqual("stats", model.Statistics["statistics:app.st"].Comment);
        Assert.AreEqual("dictionary", model.TextSearchDictionaries["tsdictionary:app.d"].Comment);
        Assert.AreEqual("configuration", model.TextSearchConfigurations["tsconfiguration:app.c"].Comment);
    }

    [TestMethod]
    public void TheOwnersOfTheseKindsAreStatementsAndOnlyForWhatWasRead()
    {
        var rows = new LiveDatabaseModelLoader.OwnerRow[]
        {
            new("collation", "app", "ci", string.Empty, null, null, "bob"),
            new("statistics", "app", "st", string.Empty, null, null, "bob"),
            new("text search dictionary", "app", "d", string.Empty, null, null, "amy"),
            new("text search configuration", "app", "c", string.Empty, null, null, "amy"),
            new("collation", "app", "other", string.Empty, null, null, "bob"),
        };

        var owners = LiveDatabaseModelLoader.BuildOwnerSnapshots(rows, [new LiveSchemaSnapshot("app")], [Table], [], [], [], [], [], [], [], [], collations: [Collation()], statistics: [Statistics()], textSearch: TextSearch())
            .Select(static owner => owner.Statement)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "ALTER COLLATION app.ci OWNER TO bob;",
                "ALTER STATISTICS app.st OWNER TO bob;",
                "ALTER TEXT SEARCH CONFIGURATION app.c OWNER TO amy;",
                "ALTER TEXT SEARCH DICTIONARY app.d OWNER TO amy;",
            },
            owners);
    }

    [TestMethod]
    public void InitWritesCommentsAndOwnersBesideEachObject()
    {
        var owners = new LiveOwnerSnapshot[]
        {
            new("collation", "app", "ci", string.Empty, "bob", "ALTER COLLATION app.ci OWNER TO bob;"),
            new("statistics", "app", "st", string.Empty, "bob", "ALTER STATISTICS app.st OWNER TO bob;"),
            new("text search dictionary", "app", "d", string.Empty, "amy", "ALTER TEXT SEARCH DICTIONARY app.d OWNER TO amy;"),
            new("text search configuration", "app", "c", string.Empty, "amy", "ALTER TEXT SEARCH CONFIGURATION app.c OWNER TO amy;"),
        };

        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("app")], [Table], [], [], [], [], [], [], [], [], [], owners: owners, collations: [Collation()], statistics: [Statistics()], textSearch: TextSearch());

        string Content(string path) => files.Single(f => f.RelativePath == path).Content;
        StringAssert.Contains(Content("Schema/app/Collation/ci.sql"), "COMMENT ON COLLATION app.ci IS 'It''s a collation';");
        StringAssert.Contains(Content("Schema/app/Collation/ci.sql"), "ALTER COLLATION app.ci OWNER TO bob;");
        StringAssert.Contains(Content("Schema/app/TextSearchDictionary/d.sql"), "ALTER TEXT SEARCH DICTIONARY app.d OWNER TO amy;");
        StringAssert.Contains(Content("Schema/app/TextSearchDictionary/d.sql"), "COMMENT ON TEXT SEARCH DICTIONARY");
        StringAssert.Contains(Content("Schema/app/TextSearchConfiguration/c.sql"), "ALTER TEXT SEARCH CONFIGURATION app.c OWNER TO amy;");
        var tableFile = files.Single(f => f.Content.Contains("CREATE STATISTICS", StringComparison.Ordinal));
        StringAssert.Contains(tableFile.Content, "COMMENT ON STATISTICS app.st IS 'stats';");
        StringAssert.Contains(tableFile.Content, "ALTER STATISTICS app.st OWNER TO bob;");
    }
}
