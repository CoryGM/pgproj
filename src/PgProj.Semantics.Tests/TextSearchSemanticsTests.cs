using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-149: dictionaries and configurations are model objects keyed by schema and name; the project's mapping and option statements are applied to them, in file order and
/// across files; names have a comparison form (<c>pg_catalog.</c> left off, the project's own objects qualified).
/// </summary>
[TestClass]
public sealed class TextSearchSemanticsTests
{
    private const string Setup =
        "CREATE SCHEMA app; "
        + "CREATE TEXT SEARCH DICTIONARY app.my_stem (TEMPLATE = snowball, Language = english); "
        + "CREATE TEXT SEARCH DICTIONARY app.syn (TEMPLATE = pg_catalog.simple); ";

    [TestMethod]
    public void ObjectsAreInTheModelAndMappingsAreApplied()
    {
        var model = Build(Setup
            + "CREATE TEXT SEARCH CONFIGURATION app.cfg (PARSER = pg_catalog.\"default\"); "
            + "ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR asciiword, word WITH my_stem, simple; "
            + "ALTER TEXT SEARCH CONFIGURATION app.cfg ALTER MAPPING FOR word WITH app.syn; "
            + "ALTER TEXT SEARCH CONFIGURATION app.cfg DROP MAPPING FOR asciiword;");

        var configuration = model.TextSearchConfigurations["tsconfiguration:app.cfg"];
        Assert.AreEqual(1, configuration.Mappings.Count);
        CollectionAssert.AreEqual(new[] { "app.syn" }, configuration.Mappings["word"].ToArray());
        Assert.AreEqual("default", configuration.NormalizedParser);
        Assert.AreEqual("snowball", model.TextSearchDictionaries["tsdictionary:app.my_stem"].NormalizedTemplate);
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void DictionaryNamesTakeTheirComparisonFormAndAnUnqualifiedOneOfTheProjectIsQualified()
    {
        var model = Build(Setup
            + "CREATE TEXT SEARCH CONFIGURATION app.cfg (PARSER = pg_catalog.\"default\"); "
            + "ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word WITH my_stem, pg_catalog.simple, simple;");

        var configuration = model.TextSearchConfigurations["tsconfiguration:app.cfg"];
        CollectionAssert.AreEqual(new[] { "app.my_stem", "pg_catalog.simple", "simple" }, configuration.Mappings["word"].ToArray());
        CollectionAssert.AreEqual(new[] { "app.my_stem", "simple", "simple" }, configuration.NormalizedMappings["word"].ToArray());
    }

    [TestMethod]
    public void ACopyRecordsADroppedTokenAsAMappingWithNoDictionaries()
    {
        var model = Build(Setup
            + "CREATE TEXT SEARCH CONFIGURATION app.cfg (COPY = pg_catalog.english); "
            + "ALTER TEXT SEARCH CONFIGURATION app.cfg DROP MAPPING IF EXISTS FOR email;");

        var configuration = model.TextSearchConfigurations["tsconfiguration:app.cfg"];
        Assert.IsTrue(configuration.IsCopy);
        Assert.IsEmpty(configuration.Mappings["email"]);
        Assert.AreEqual("english", configuration.NormalizedCopyFrom);
    }

    [TestMethod]
    public void ADictionaryAlterChangesItsOptionsAndMayBeInAnotherFile()
    {
        var model = new SemanticPipeline().Execute(
        [
            new PgQueryParser().Parse("ALTER TEXT SEARCH DICTIONARY app.my_stem (StopWords = 'german', Language);"),
            new PgQueryParser().Parse(Setup),
        ]);

        var options = model.TextSearchDictionaries["tsdictionary:app.my_stem"].Options;
        Assert.AreEqual("german", options["stopwords"]);
        Assert.IsFalse(options.ContainsKey("language"));
    }

    [TestMethod]
    public void AnAlterOfAnObjectTheProjectDoesNotDeclareIsAWarning()
    {
        var model = Build("ALTER TEXT SEARCH CONFIGURATION pg_catalog.english ADD MAPPING FOR word WITH simple;");

        var diagnostic = model.Diagnostics.Single();
        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, diagnostic.Severity);
        StringAssert.Contains(diagnostic.Message, "pg_catalog.english");
    }

    [TestMethod]
    public void ADuplicateNameIsAnError()
    {
        Assert.IsTrue(Build(Setup + "CREATE TEXT SEARCH DICTIONARY app.syn (TEMPLATE = simple);").HasErrors);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
