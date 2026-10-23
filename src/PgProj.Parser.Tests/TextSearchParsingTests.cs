namespace PgProj.Parser.Tests;

/// <summary>
/// G-149: text search dictionaries and configurations, and the <c>ALTER</c> statements that map tokens and set options, are read instead of being warned about and ignored
/// (G-106). Parsers, templates, and the <c>REPLACE</c> forms are still reported.
/// </summary>
[TestClass]
public sealed class TextSearchParsingTests
{
    [TestMethod]
    public void ADictionaryIsReadWithItsTemplateAndOptions()
    {
        var tree = new PgQueryParser().Parse("CREATE TEXT SEARCH DICTIONARY app.my_stem (TEMPLATE = snowball, Language = english, StopWords = 'English', Foo = 3);");
        var node = tree.TextSearchDictionaries.Single();

        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual("my_stem", node.Name);
        Assert.AreEqual("app", node.SchemaName);
        Assert.AreEqual("snowball", node.Template);
        Assert.AreEqual("english", node.Options["language"]);
        Assert.AreEqual("English", node.Options["stopwords"]);
        Assert.AreEqual("3", node.Options["foo"]);
        Assert.IsFalse(node.Options.ContainsKey("template"));
    }

    [TestMethod]
    public void AQualifiedTemplateKeepsItsSchemaAndAnUnqualifiedNameTakesTheDefaultSchema()
    {
        var node = new PgQueryParser().Parse("CREATE TEXT SEARCH DICTIONARY d2 (TEMPLATE = pg_catalog.simple);").TextSearchDictionaries.Single();

        Assert.AreEqual("pg_catalog.simple", node.Template);
        Assert.AreEqual("public", node.SchemaName);
    }

    [TestMethod]
    public void AConfigurationIsReadWithItsParserOrItsCopySource()
    {
        var copy = new PgQueryParser().Parse("CREATE TEXT SEARCH CONFIGURATION app.my_cfg (COPY = pg_catalog.english);").TextSearchConfigurations.Single();
        var parser = new PgQueryParser().Parse("CREATE TEXT SEARCH CONFIGURATION app.blank (PARSER = pg_catalog.\"default\");").TextSearchConfigurations.Single();

        Assert.AreEqual("pg_catalog.english", copy.CopyFrom);
        Assert.IsNull(copy.Parser);
        Assert.AreEqual("pg_catalog.default", parser.Parser);
        Assert.IsNull(parser.CopyFrom);
    }

    [TestMethod]
    public void MappingStatementsAreReadWithTheirTokensAndDictionaries()
    {
        var tree = new PgQueryParser().Parse(
            "ALTER TEXT SEARCH CONFIGURATION app.c ADD MAPPING FOR asciiword, word WITH app.my_stem, simple;"
            + "ALTER TEXT SEARCH CONFIGURATION app.c ALTER MAPPING FOR asciiword WITH simple;"
            + "ALTER TEXT SEARCH CONFIGURATION app.c DROP MAPPING IF EXISTS FOR word;");
        var mappings = tree.Nodes.OfType<TextSearchMappingNode>().ToArray();

        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual(3, mappings.Length);
        Assert.AreEqual(TextSearchMappingAction.Add, mappings[0].Action);
        CollectionAssert.AreEqual(new[] { "asciiword", "word" }, mappings[0].Tokens.ToArray());
        CollectionAssert.AreEqual(new[] { "app.my_stem", "simple" }, mappings[0].Dictionaries.ToArray());
        Assert.AreEqual(TextSearchMappingAction.Alter, mappings[1].Action);
        Assert.AreEqual(TextSearchMappingAction.Drop, mappings[2].Action);
        Assert.IsEmpty(mappings[2].Dictionaries);
        Assert.AreEqual("app", mappings[0].SchemaName);
    }

    [TestMethod]
    public void ADictionaryAlterIsReadAndAnOptionWithoutAValueIsNull()
    {
        var node = new PgQueryParser().Parse("ALTER TEXT SEARCH DICTIONARY app.d (StopWords = 'x', Language);").Nodes.OfType<TextSearchDictionaryAlterNode>().Single();

        Assert.AreEqual("x", node.Options["stopwords"]);
        Assert.IsNull(node.Options["language"]);
    }

    [TestMethod]
    public void TheReplaceFormsParsersAndTemplatesAreStillReported()
    {
        var replace = new PgQueryParser().Parse("ALTER TEXT SEARCH CONFIGURATION app.c ALTER MAPPING REPLACE simple WITH app.syn;");
        var parser = new PgQueryParser().Parse("CREATE TEXT SEARCH PARSER app.p (START = a, GETTOKEN = b, END = c, LEXTYPES = d);");
        var template = new PgQueryParser().Parse("CREATE TEXT SEARCH TEMPLATE app.t (INIT = a, LEXIZE = b);");

        StringAssert.Contains(replace.Diagnostics.Single().Message, "ALTER TEXT SEARCH CONFIGURATION ... REPLACE");
        StringAssert.Contains(parser.Diagnostics.Single().Message, "TEXT SEARCH PARSER");
        StringAssert.Contains(template.Diagnostics.Single().Message, "TEXT SEARCH TEMPLATE");
    }
}
