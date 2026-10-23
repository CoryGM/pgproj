using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-195 against a real server. The storage parameters of an index and of a primary key's or unique constraint's index were neither deployed nor compared. They
/// are now deployed, read back (<c>pg_get_indexdef</c> and <c>pg_get_constraintdef</c> print them quoted), compared, scaffolded by <c>init</c>, and changed in
/// place with <c>ALTER INDEX ... SET/RESET</c>, which keeps the index. An option set on the database alone is reset, as the user decided.
/// </summary>
[TestClass]
public sealed class IndexStorageOptionEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.t (
            id int, u int, a int, doc jsonb,
            CONSTRAINT t_pk PRIMARY KEY (id) WITH (fillfactor = 80),
            CONSTRAINT t_u UNIQUE (u) WITH (fillfactor=60) DEFERRABLE INITIALLY DEFERRED);
        CREATE INDEX t_a ON public.t (a) WITH (fillfactor = 70, deduplicate_items = off) WHERE a > 0;
        CREATE INDEX t_doc ON public.t USING gin (doc) WITH (fastupdate = off);
        """;

    private const string Options = "SELECT string_agg(c.relname || ':' || coalesce(array_to_string(c.reloptions, ','), '-'), ' ' ORDER BY c.relname) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE i.indrelid = 'public.t'::regclass;";

    [TestMethod]
    public async Task IndexOptionsAreDeployedAndConverge_AndChangesAreMadeInPlace()
    {
        var databaseName = await CreateDatabaseAsync("g195");
        var projectDirectory = CreateProjectDirectory("g195");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g195-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("t_a:fillfactor=70,deduplicate_items=off t_doc:fastupdate=off t_pk:fillfactor=80 t_u:fillfactor=60", await ScalarTextAsync(connectionString, Options));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // A changed option is set in place: the index and the key's index are the same relations afterwards.
            var before = await ScalarTextAsync(connectionString, "SELECT 't_a'::regclass::oid || ',' || 't_pk'::regclass::oid;");
            WriteSchema(projectDirectory, Edit(Edit(Sql, "fillfactor = 70, deduplicate_items = off", "fillfactor = 50"), "PRIMARY KEY (id) WITH (fillfactor = 80)", "PRIMARY KEY (id) WITH (fillfactor = 90)"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual(before, await ScalarTextAsync(connectionString, "SELECT 't_a'::regclass::oid || ',' || 't_pk'::regclass::oid;"));
            Assert.AreEqual("t_a:fillfactor=50 t_doc:fastupdate=off t_pk:fillfactor=90 t_u:fillfactor=60", await ScalarTextAsync(connectionString, Options));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // An option set on the database alone is a difference, and publish resets it.
            await ExecuteAsync(connectionString, "ALTER INDEX public.t_doc SET (gin_pending_list_limit = 128);");
            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diffed.Output, "Storage parameters: ");
            var reset = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, reset.ExitCode, "publish --execute failed.\n" + reset.Output);
            Assert.AreEqual("t_a:fillfactor=50 t_doc:fastupdate=off t_pk:fillfactor=90 t_u:fillfactor=60", await ScalarTextAsync(connectionString, Options));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
