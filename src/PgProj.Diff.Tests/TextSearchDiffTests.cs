using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-149: dictionaries are compared by template and option; configurations by parser and the dictionaries of each token type, except that a configuration the project makes as
/// a <c>COPY</c> is compared only on the token types the project states. The same objects written another way are no change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TextSearchDiffTests
{
    private const string Dictionaries =
        "CREATE SCHEMA app; "
        + "CREATE TEXT SEARCH DICTIONARY app.syn (TEMPLATE = simple); "
        + "CREATE TEXT SEARCH DICTIONARY app.stem (TEMPLATE = snowball, Language = english); ";

    private static string Configuration(string mappings, string made = "PARSER = pg_catalog.\"default\"")
        => Dictionaries + $"CREATE TEXT SEARCH CONFIGURATION app.cfg ({made}); " + mappings;

    [TestMethod]
    public void ObjectsDiffedWithThemselvesAreEmpty()
    {
        var model = Build(Configuration("ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word, asciiword WITH app.stem, simple;"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    public void TheSameConfigurationWrittenTwoWaysReportsNothing()
    {
        var first = Build(Configuration("ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word, asciiword WITH stem, simple;"));
        var second = Build(Configuration("ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR asciiword WITH app.stem, pg_catalog.simple; ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word WITH app.stem, simple;", made: "PARSER = \"default\""));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(first, second)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(second, first)));
    }

    [TestMethod]
    public void ACopyIsComparedOnlyOnTheTokensTheProjectStates()
    {
        var live = Build(Configuration("ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word, asciiword, email WITH app.stem, simple;"));
        var project = Build(Configuration("ALTER TEXT SEARCH CONFIGURATION app.cfg ALTER MAPPING FOR word WITH app.stem, simple;", made: "COPY = pg_catalog.english"));
        var drifted = Build(Configuration("ALTER TEXT SEARCH CONFIGURATION app.cfg ALTER MAPPING FOR word WITH app.syn;", made: "COPY = pg_catalog.english"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(live, project)));
        var modified = new DiffEngine().Diff(live, drifted).Modified.Single(static change => change.NodeKind == ChangeNodeKind.TextSearchConfiguration);
        CollectionAssert.AreEqual(new[] { "Mapping:word" }, modified.Details.Select(static detail => detail.PropertyName).ToArray());
    }

    [TestMethod]
    [DataRow("ADD MAPPING FOR word WITH app.stem", "ADD MAPPING FOR word WITH app.syn", "Mapping:word")]
    [DataRow("ADD MAPPING FOR word WITH app.stem, simple", "ADD MAPPING FOR word WITH simple, app.stem", "Mapping:word")]
    [DataRow("ADD MAPPING FOR word WITH app.stem", "ADD MAPPING FOR word, email WITH app.stem", "Mapping:email")]
    public void ARealMappingChangeIsReportedWithTheTokenType(string before, string after, string property)
    {
        var changes = new DiffEngine().Diff(
            Build(Configuration($"ALTER TEXT SEARCH CONFIGURATION app.cfg {before};")),
            Build(Configuration($"ALTER TEXT SEARCH CONFIGURATION app.cfg {after};")));

        var modified = changes.Modified.Single(static change => change.NodeKind == ChangeNodeKind.TextSearchConfiguration);
        Assert.IsTrue(modified.Details.Any(detail => detail.PropertyName == property), string.Join(", ", modified.Details.Select(static d => d.PropertyName)));
    }

    [TestMethod]
    public void AChangedParserIsReported()
    {
        var changes = new DiffEngine().Diff(Build(Configuration(string.Empty)), Build(Configuration(string.Empty, made: "PARSER = pg_catalog.other")));

        Assert.IsTrue(changes.Modified.Single(static change => change.NodeKind == ChangeNodeKind.TextSearchConfiguration).Details.Any(static detail => detail.PropertyName == "Parser"));
    }

    [TestMethod]
    [DataRow("Language = english", "Language = german", "Option:language")]
    [DataRow("Language = english", "Language = english, StopWords = english", "Option:stopwords")]
    public void ADictionaryOptionChangeIsReported(string before, string after, string property)
    {
        var changes = new DiffEngine().Diff(
            Build("CREATE SCHEMA app; CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = snowball, " + before + ");"),
            Build("CREATE SCHEMA app; CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = snowball, " + after + ");"));

        var modified = changes.Modified.Single(static change => change.NodeKind == ChangeNodeKind.TextSearchDictionary);
        Assert.IsTrue(modified.Details.Any(detail => detail.PropertyName == property));
    }

    [TestMethod]
    public void ADictionaryOptionSpelledWithOrWithoutQuotesIsTheSame()
    {
        var first = Build("CREATE SCHEMA app; CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = pg_catalog.snowball, LANGUAGE = english);");
        var second = Build("CREATE SCHEMA app; CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = snowball, language = 'english');");

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(first, second)));
    }

    [TestMethod]
    public void AddedAndRemovedObjectsAreReported()
    {
        var with = Build(Configuration(string.Empty));
        var without = Build("CREATE SCHEMA app;");

        var added = new DiffEngine().Diff(without, with).Added;
        Assert.AreEqual(2, added.Count(static change => change.NodeKind is ChangeNodeKind.TextSearchDictionary));
        Assert.AreEqual(1, added.Count(static change => change.NodeKind is ChangeNodeKind.TextSearchConfiguration));
        Assert.AreEqual(1, new DiffEngine().Diff(with, without).Removed.Count(static change => change.NodeKind is ChangeNodeKind.TextSearchConfiguration));
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
