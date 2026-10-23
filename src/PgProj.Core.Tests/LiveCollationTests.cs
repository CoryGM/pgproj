using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-148: a live collation becomes a <c>CREATE COLLATION</c> the parser reads back as the same object a project's statement gives, and <c>init</c> writes each in a file of its
/// own under its schema's <c>COLLATION</c> folder.
/// </summary>
[TestClass]
public sealed class LiveCollationTests
{
    private static LiveCollationSnapshot Icu() => new("app", "ci", "icu", null, null, "und-u-ks-level2", false, null);

    [TestMethod]
    public void AnIcuCollationIsWrittenByItsLocale()
    {
        Assert.AreEqual(
            "CREATE COLLATION app.ci (PROVIDER = icu, LOCALE = 'und-u-ks-level2', DETERMINISTIC = false);",
            LiveDatabaseModelLoader.BuildCreateCollationStatement(Icu()));
    }

    [TestMethod]
    public void AnIcuCollationOnTheRootLocaleKeepsItsEmptyLocale()
    {
        // PostgreSQL 14 and 15 store the root locale as an empty one and refuse a CREATE COLLATION that states none (G-298).
        Assert.AreEqual(
            "CREATE COLLATION app.cs (PROVIDER = icu, LOCALE = '');",
            LiveDatabaseModelLoader.BuildCreateCollationStatement(new LiveCollationSnapshot("app", "cs", "icu", null, null, string.Empty, true, null)));
    }

    [TestMethod]
    public void ALibcCollationIsWrittenByBothLocalesAndRulesAreKept()
    {
        Assert.AreEqual(
            "CREATE COLLATION app.c (PROVIDER = libc, LC_COLLATE = 'C', LC_CTYPE = 'POSIX');",
            LiveDatabaseModelLoader.BuildCreateCollationStatement(new LiveCollationSnapshot("app", "c", "libc", "C", "POSIX", null, true, null)));
        StringAssert.Contains(
            LiveDatabaseModelLoader.BuildCreateCollationStatement(Icu() with { Rules = "&a<b'" }),
            "RULES = '&a<b'''");
    }

    [TestMethod]
    public void ALiveCollationIsTheSameObjectAsTheProjectsStatement()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("app")], [], [], [], collations: [Icu()]);
        var live = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql).Collations["collation:app.ci"];
        var project = new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE COLLATION app.ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);")]).Collations["collation:app.ci"];

        Assert.AreEqual(project.Provider, live.Provider);
        Assert.AreEqual(project.Locale, live.Locale);
        Assert.AreEqual(project.IsDeterministic, live.IsDeterministic);
        Assert.AreEqual(project.LcCollate, live.LcCollate);
    }

    [TestMethod]
    public void InitWritesACollationInItsOwnFileUnderTheSchema()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("app")], [], [], [], [], [], [], [], [], [], [], collations: [Icu()]);

        var file = files.Single(static f => f.RelativePath == "Schema/app/Collation/ci.sql");
        StringAssert.Contains(file.Content, "CREATE COLLATION app.ci");
    }
}
