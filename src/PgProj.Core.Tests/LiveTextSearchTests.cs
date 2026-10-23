using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-149: live text search objects become statements the parser reads back as the same objects a project's statements give, and <c>init</c> writes each in a file of its own
/// under its schema.
/// </summary>
[TestClass]
public sealed class LiveTextSearchTests
{
    private static LiveTextSearchSnapshot Snapshot() => new(
        [new LiveTextSearchDictionarySnapshot("app", "my_stem", "pg_catalog", "snowball", "language = 'english', stopwords = 'english'"), new LiveTextSearchDictionarySnapshot("app", "syn", "pg_catalog", "simple", string.Empty)],
        [new LiveTextSearchConfigurationSnapshot("app", "cfg", "pg_catalog", "default", [new LiveTextSearchMapping("asciiword", ["app.my_stem", "app.syn"]), new LiveTextSearchMapping("word", ["pg_catalog.simple"])])]);

    [TestMethod]
    public void TheStatementsAreQuotedAndMapTokenByToken()
    {
        var statements = LiveDatabaseModelLoader.BuildTextSearchStatements(Snapshot()).ToArray();

        CollectionAssert.Contains(statements, "CREATE TEXT SEARCH DICTIONARY \"app\".\"my_stem\" (TEMPLATE = \"pg_catalog\".\"snowball\", language = 'english', stopwords = 'english');");
        CollectionAssert.Contains(statements, "CREATE TEXT SEARCH DICTIONARY \"app\".\"syn\" (TEMPLATE = \"pg_catalog\".\"simple\");");
        CollectionAssert.Contains(statements, "CREATE TEXT SEARCH CONFIGURATION \"app\".\"cfg\" (PARSER = \"pg_catalog\".\"default\");");
        CollectionAssert.Contains(statements, "ALTER TEXT SEARCH CONFIGURATION \"app\".\"cfg\" ADD MAPPING FOR \"asciiword\" WITH \"app\".\"my_stem\", \"app\".\"syn\";");
    }

    [TestMethod]
    public void LiveObjectsAreTheSameAsTheProjectsStatements()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("app")], [], [], [], textSearch: Snapshot());
        var live = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);
        var project = new SemanticPipeline().Execute([new PgQueryParser().Parse(
            "CREATE SCHEMA app; CREATE TEXT SEARCH DICTIONARY app.my_stem (TEMPLATE = snowball, Language = english, StopWords = english); CREATE TEXT SEARCH DICTIONARY app.syn (TEMPLATE = simple); "
            + "CREATE TEXT SEARCH CONFIGURATION app.cfg (PARSER = pg_catalog.\"default\"); ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR asciiword WITH my_stem, syn; ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word WITH simple;")]);

        CollectionAssert.AreEqual(project.TextSearchConfigurations["tsconfiguration:app.cfg"].NormalizedMappings["asciiword"].ToArray(), live.TextSearchConfigurations["tsconfiguration:app.cfg"].NormalizedMappings["asciiword"].ToArray());
        CollectionAssert.AreEqual(project.TextSearchConfigurations["tsconfiguration:app.cfg"].NormalizedMappings["word"].ToArray(), live.TextSearchConfigurations["tsconfiguration:app.cfg"].NormalizedMappings["word"].ToArray());
        Assert.AreEqual(project.TextSearchConfigurations["tsconfiguration:app.cfg"].NormalizedParser, live.TextSearchConfigurations["tsconfiguration:app.cfg"].NormalizedParser);
        CollectionAssert.AreEqual(project.TextSearchDictionaries["tsdictionary:app.my_stem"].Options.OrderBy(static p => p.Key).ToArray(), live.TextSearchDictionaries["tsdictionary:app.my_stem"].Options.OrderBy(static p => p.Key).ToArray());
        Assert.AreEqual(project.TextSearchDictionaries["tsdictionary:app.my_stem"].NormalizedTemplate, live.TextSearchDictionaries["tsdictionary:app.my_stem"].NormalizedTemplate);
    }

    [TestMethod]
    public void InitWritesEachObjectInAFileOfItsOwnUnderItsSchema()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("app")], [], [], [], [], [], [], [], [], [], [], textSearch: Snapshot());

        var paths = files.Select(static f => f.RelativePath).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.IsSubsetOf(
            new[] { "Schema/app/TextSearchConfiguration/cfg.sql", "Schema/app/TextSearchDictionary/my_stem.sql", "Schema/app/TextSearchDictionary/syn.sql" },
            paths);
        StringAssert.Contains(files.Single(static f => f.RelativePath.EndsWith("cfg.sql", StringComparison.Ordinal)).Content, "ADD MAPPING FOR \"word\"");
    }
}
