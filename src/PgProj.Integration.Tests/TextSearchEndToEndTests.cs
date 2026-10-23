using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-149 against a real server, on every version: text search dictionaries and configurations (one a copy of <c>english</c> with two token types remapped, one built from the
/// default parser) deploy in order whatever order the file has, before the index that names a configuration by a string; read back from the catalogs; converge <c>0/0/0</c>;
/// round-trip through <c>init</c> (a copy comes back as the full set of mappings and still converges); change in place (dictionary options, token mappings); are refused
/// when a template changes under a configuration; and are removed without <c>--allow-destructive</c>.
/// </summary>
[TestClass]
public sealed class TextSearchEndToEndTests
{
    private static string Schema(
        string stemOptions = "Language = english, StopWords = english",
        string blankMappings = "ALTER TEXT SEARCH CONFIGURATION app.blank ADD MAPPING FOR asciiword WITH simple;\nALTER TEXT SEARCH CONFIGURATION app.blank ADD MAPPING FOR word, numword WITH syn, simple;",
        bool withBlank = true,
        string syn = "TEMPLATE = simple")
        => $"""
            CREATE INDEX t_fts ON app.t USING gin (to_tsvector('app.my_cfg'::regconfig, body));
            CREATE TABLE app.t (id int PRIMARY KEY, body text);
            ALTER TEXT SEARCH CONFIGURATION app.my_cfg ALTER MAPPING FOR asciiword, word WITH my_stem, syn;
            CREATE TEXT SEARCH CONFIGURATION app.my_cfg (COPY = pg_catalog.english);
            {(withBlank ? "CREATE TEXT SEARCH CONFIGURATION app.blank (PARSER = pg_catalog.\"default\");" : string.Empty)}
            {(withBlank ? blankMappings : string.Empty)}
            CREATE TEXT SEARCH DICTIONARY app.my_stem (TEMPLATE = snowball, {stemOptions});
            CREATE TEXT SEARCH DICTIONARY app.syn ({syn});
            CREATE SCHEMA app;
            """;

    private const string MappingQuery = """
        SELECT string_agg(tt.alias || '=' || d.dictname, ',' ORDER BY tt.alias, m.mapseqno)
        FROM pg_ts_config_map m JOIN pg_ts_config c ON c.oid = m.mapcfg JOIN pg_namespace n ON n.oid = c.cfgnamespace
        JOIN pg_ts_dict d ON d.oid = m.mapdict CROSS JOIN LATERAL pg_catalog.ts_token_type(c.cfgparser) tt
        WHERE tt.tokid = m.maptokentype AND n.nspname = 'app' AND c.cfgname = '{0}';
        """;

    private static string Mapping(string configuration) => string.Format(System.Globalization.CultureInfo.InvariantCulture, MappingQuery, configuration);

    [TestMethod]
    public async Task TextSearchObjectsDeployConvergeRoundTripThroughInitAndChangeInPlace()
    {
        var databaseName = await CreateDatabaseAsync("g149");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g149s");
        var projectDirectory = CreateProjectDirectory("g149");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema());

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            // The index comes first in the file and the dictionaries last, and everything still deploys in order.
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("2|2", await ScalarTextAsync(connectionString, "SELECT (SELECT count(*) FROM pg_ts_dict d JOIN pg_namespace n ON n.oid = d.dictnamespace WHERE n.nspname = 'app') || '|' || (SELECT count(*) FROM pg_ts_config c JOIN pg_namespace n ON n.oid = c.cfgnamespace WHERE n.nspname = 'app');"));
            Assert.AreEqual("asciiword=simple,numword=syn,numword=simple,word=syn,word=simple", await ScalarTextAsync(connectionString, Mapping("blank")));
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT string_agg(tt.alias || '=' || d.dictname, ',' ORDER BY tt.alias, m.mapseqno) FROM pg_ts_config_map m JOIN pg_ts_config c ON c.oid = m.mapcfg JOIN pg_ts_dict d ON d.oid = m.mapdict CROSS JOIN LATERAL pg_catalog.ts_token_type(c.cfgparser) tt WHERE tt.tokid = m.maptokentype AND c.cfgname = 'my_cfg' AND tt.alias IN ('asciiword', 'word', 'email');"), "asciiword=my_stem,asciiword=syn");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.IsFalse(again.Output.Contains("TEXT SEARCH", StringComparison.Ordinal), again.Output);

            // The same objects written another way: built-in names qualified, tokens in another order, a quoted option.
            WriteSchema(projectDirectory, Schema(
                stemOptions: "language = 'english', stopwords = 'english'",
                blankMappings: "ALTER TEXT SEARCH CONFIGURATION app.blank ADD MAPPING FOR numword, word WITH app.syn, pg_catalog.simple;\nALTER TEXT SEARCH CONFIGURATION app.blank ADD MAPPING FOR asciiword WITH pg_catalog.simple;",
                syn: "TEMPLATE = pg_catalog.simple"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            WriteSchema(projectDirectory, Schema());

            // Round trip through init: a file for each object, the copy comes back as its full set of mappings, and the scaffold deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.AreEqual(2, Directory.GetFiles(Path.Combine(scaffoldDirectory, "Schema", "app", "TextSearchDictionary"), "*.sql").Length);
            Assert.AreEqual(2, Directory.GetFiles(Path.Combine(scaffoldDirectory, "Schema", "app", "TextSearchConfiguration"), "*.sql").Length);
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual(await ScalarTextAsync(connectionString, Mapping("my_cfg")), await ScalarTextAsync(scaffoldConnectionString, Mapping("my_cfg")));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // A dictionary's options change in place (the OID stays), and one written without a value is removed.
            var dictionaryOid = await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_ts_dict WHERE dictname = 'my_stem';");
            WriteSchema(projectDirectory, Schema(stemOptions: "Language = german"));
            var optionChanged = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, optionChanged.ExitCode, optionChanged.Output);
            Assert.AreEqual("language = 'german'", await ScalarTextAsync(connectionString, "SELECT dictinitoption FROM pg_ts_dict WHERE dictname = 'my_stem';"));
            Assert.AreEqual(dictionaryOid, await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_ts_dict WHERE dictname = 'my_stem';"), "Altered in place, not dropped and created again.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A configuration's mappings change in place: one token re-mapped, one added, one dropped.
            var configurationOid = await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_ts_config WHERE cfgname = 'blank';");
            WriteSchema(projectDirectory, Schema(
                stemOptions: "Language = german",
                blankMappings: "ALTER TEXT SEARCH CONFIGURATION app.blank ADD MAPPING FOR asciiword WITH app.syn;\nALTER TEXT SEARCH CONFIGURATION app.blank ADD MAPPING FOR word, email WITH simple;"));
            var remapped = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, remapped.ExitCode, remapped.Output);
            Assert.AreEqual("asciiword=syn,email=simple,word=simple", await ScalarTextAsync(connectionString, Mapping("blank")));
            Assert.AreEqual(configurationOid, await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_ts_config WHERE cfgname = 'blank';"), "Altered in place, not dropped and created again.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A template that changes under a configuration is refused before anything runs, naming what uses it.
            WriteSchema(projectDirectory, Schema(
                stemOptions: "Language = german",
                blankMappings: "ALTER TEXT SEARCH CONFIGURATION app.blank ADD MAPPING FOR asciiword WITH app.syn;\nALTER TEXT SEARCH CONFIGURATION app.blank ADD MAPPING FOR word, email WITH simple;",
                syn: "TEMPLATE = snowball, Language = english"));
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "[TextSearchDictionary] app.syn");
            var preview = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(preview.Output, "-- TODO: The text search dictionary app.syn changed its template");
            Assert.AreEqual("simple", await ScalarTextAsync(connectionString, "SELECT t.tmplname FROM pg_ts_dict d JOIN pg_ts_template t ON t.oid = d.dicttemplate WHERE d.dictname = 'syn';"));

            // A configuration removed is dropped without --allow-destructive: it holds no data.
            WriteSchema(projectDirectory, Schema(stemOptions: "Language = german", withBlank: false));
            var removed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, removed.ExitCode, removed.Output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_ts_config WHERE cfgname = 'blank';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AProjectThatListsSchemasOnlyManagesTheTextSearchObjectsInThem()
    {
        var databaseName = await CreateDatabaseAsync("g149sc");
        var projectDirectory = CreateProjectDirectory("g149sc", ", \"schemas\": [\"app\"]");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, "CREATE TEXT SEARCH DICTIONARY public.other (TEMPLATE = simple); CREATE TEXT SEARCH CONFIGURATION public.other_cfg (COPY = pg_catalog.simple);");
            WriteSchema(projectDirectory, "CREATE SCHEMA app; CREATE TEXT SEARCH DICTIONARY app.mine (TEMPLATE = simple);");

            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_ts_dict WHERE oid >= 16384;"));
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_ts_config WHERE oid >= 16384;"));
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
