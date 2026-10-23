using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-149: dictionaries are created before the configurations that map them and a configuration before the index that names it; options and mappings change in place; a
/// changed template or parser is a drop and a create, except that one something uses is refused with a message.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TextSearchDeployTests
{
    private const string Dictionaries =
        "CREATE SCHEMA app; "
        + "CREATE TEXT SEARCH DICTIONARY app.syn (TEMPLATE = simple); "
        + "CREATE TEXT SEARCH DICTIONARY app.stem (TEMPLATE = snowball, Language = english, StopWords = english); ";

    private static string Configuration(string mappings, string made = "PARSER = pg_catalog.\"default\"")
        => Dictionaries + $"CREATE TEXT SEARCH CONFIGURATION app.cfg ({made}); " + mappings;

    [TestMethod]
    public void ObjectsAreCreatedInOrderWhateverTheOrderOfTheFile()
    {
        var statements = Statements(
            Build("CREATE SCHEMA app;"),
            Build("CREATE INDEX t_fts ON app.t USING gin (to_tsvector('app.cfg'::regconfig, body)); CREATE TABLE app.t (body text); "
                + "ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word WITH stem; "
                + "CREATE TEXT SEARCH CONFIGURATION app.cfg (PARSER = pg_catalog.\"default\"); "
                + "CREATE TEXT SEARCH DICTIONARY app.stem (TEMPLATE = snowball, Language = english);"));

        int At(string start) => statements.FindIndex(s => s.StartsWith(start, StringComparison.Ordinal));
        Assert.IsTrue(At("CREATE TEXT SEARCH DICTIONARY") < At("CREATE TEXT SEARCH CONFIGURATION"), string.Join("\n", statements));
        Assert.IsTrue(At("CREATE TEXT SEARCH CONFIGURATION") < At("CREATE INDEX"), string.Join("\n", statements));
    }

    [TestMethod]
    public void ADictionaryAndAParserConfigurationAreWrittenWithTheirOptionsAndMappings()
    {
        var script = string.Join("\n", Statements(Build("CREATE SCHEMA app;"), Build(Configuration(
            "ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word, asciiword WITH stem, simple; ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR email WITH syn;"))));

        StringAssert.Contains(script, "CREATE TEXT SEARCH DICTIONARY \"app\".\"stem\" (\n    TEMPLATE = \"snowball\",\n    language = 'english',\n    stopwords = 'english'\n);");
        StringAssert.Contains(script, "CREATE TEXT SEARCH CONFIGURATION \"app\".\"cfg\" (PARSER = \"pg_catalog\".\"default\");");
        StringAssert.Contains(script, "ALTER TEXT SEARCH CONFIGURATION \"app\".\"cfg\" ADD MAPPING FOR \"asciiword\", \"word\" WITH \"app\".\"stem\", \"simple\";");
        StringAssert.Contains(script, "ALTER TEXT SEARCH CONFIGURATION \"app\".\"cfg\" ADD MAPPING FOR \"email\" WITH \"app\".\"syn\";");
    }

    [TestMethod]
    public void ACopyIsCreatedAndThenItsStatedMappingsAreAlteredOrDropped()
    {
        var script = string.Join("\n", Statements(Build("CREATE SCHEMA app;"), Build(Configuration(
            "ALTER TEXT SEARCH CONFIGURATION app.cfg ALTER MAPPING FOR word WITH stem; ALTER TEXT SEARCH CONFIGURATION app.cfg DROP MAPPING IF EXISTS FOR email;", made: "COPY = pg_catalog.english"))));

        StringAssert.Contains(script, "CREATE TEXT SEARCH CONFIGURATION \"app\".\"cfg\" (COPY = \"pg_catalog\".\"english\");");
        StringAssert.Contains(script, "ALTER TEXT SEARCH CONFIGURATION \"app\".\"cfg\" ALTER MAPPING FOR \"word\" WITH \"app\".\"stem\";");
        StringAssert.Contains(script, "ALTER TEXT SEARCH CONFIGURATION \"app\".\"cfg\" DROP MAPPING IF EXISTS FOR \"email\";");
    }

    [TestMethod]
    public void MappingsChangeInPlaceWithAddAlterAndDrop()
    {
        var statements = Statements(
            Build(Configuration("ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word, email, url WITH stem;")),
            Build(Configuration("ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word WITH syn; ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR email WITH stem; ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR host WITH stem;")));

        CollectionAssert.AreEqual(
            new[]
            {
                "ALTER TEXT SEARCH CONFIGURATION \"app\".\"cfg\" ADD MAPPING FOR \"host\" WITH \"app\".\"stem\";\nALTER TEXT SEARCH CONFIGURATION \"app\".\"cfg\" ALTER MAPPING FOR \"word\" WITH \"app\".\"syn\";\nALTER TEXT SEARCH CONFIGURATION \"app\".\"cfg\" DROP MAPPING FOR \"url\";",
            },
            statements);
    }

    [TestMethod]
    public void DictionaryOptionsChangeInPlaceAndARemovedOptionIsWrittenWithoutAValue()
    {
        var statements = Statements(
            Build("CREATE SCHEMA app; CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = snowball, Language = english, StopWords = english);"),
            Build("CREATE SCHEMA app; CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = snowball, Language = german);"));

        CollectionAssert.AreEqual(new[] { "ALTER TEXT SEARCH DICTIONARY \"app\".\"d\" (language = 'german', stopwords);" }, statements);
    }

    [TestMethod]
    public void AChangedTemplateOfADictionaryNothingUsesIsDroppedAndCreatedAgain()
    {
        var statements = Statements(
            Build("CREATE SCHEMA app; CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = snowball, Language = english);"),
            Build("CREATE SCHEMA app; CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = simple);"));

        Assert.AreEqual(2, statements.Count, string.Join("\n", statements));
        Assert.AreEqual("DROP TEXT SEARCH DICTIONARY \"app\".\"d\";", statements[0]);
    }

    [TestMethod]
    public void AChangedParserOrTemplateInUseIsBlockedWithAMessageNamingWhatUsesIt()
    {
        var oldModel = Build(Configuration("ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word WITH stem;"));
        var newModel = Build(Configuration("ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word WITH stem;").Replace("(TEMPLATE = snowball, Language = english, StopWords = english)", "(TEMPLATE = simple)", StringComparison.Ordinal));

        var blocked = Generate(oldModel, newModel).GetUnsupportedOperations().Single();

        StringAssert.Contains(blocked.GenerateSql(), "app.cfg");
        StringAssert.Contains(blocked.GenerateSql(), "template");
    }

    [TestMethod]
    public void RemovedObjectsAreDroppedConfigurationFirstAndAreNotDestructive()
    {
        var batch = Generate(Build(Configuration("ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word WITH stem;")), Build("CREATE SCHEMA app;"));
        var statements = Sql(batch);

        int At(string start) => statements.FindIndex(s => s.StartsWith(start, StringComparison.Ordinal));
        Assert.IsTrue(At("DROP TEXT SEARCH CONFIGURATION") >= 0 && At("DROP TEXT SEARCH CONFIGURATION") < At("DROP TEXT SEARCH DICTIONARY"), string.Join("\n", statements));
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static List<string> Sql(OperationBatch batch)
        => batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel) => Sql(Generate(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
