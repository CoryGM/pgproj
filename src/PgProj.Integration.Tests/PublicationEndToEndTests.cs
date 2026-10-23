using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-134 against a real server, on every version: publications (one with a column list, a row filter, a table, a whole schema, and options; one <c>FOR ALL TABLES</c>; an empty
/// one) deploy after the tables whatever order the file has, read back from <c>pg_publication</c>, converge <c>0/0/0</c> although PostgreSQL prints the row filter its own way,
/// are changed in place with <c>ALTER PUBLICATION</c> (options, tables, schemas) and dropped and created again only when <c>FOR ALL TABLES</c> changes, round-trip through
/// <c>init</c> into files of their own, and are not dropped by accident: removing one needs <c>--allow-destructive</c>, and a project that lists <c>schemas</c> only manages the
/// publications that name something in them.
/// </summary>
[TestClass]
public sealed class PublicationEndToEndTests
{
    private static string Schema(
        string orders = "FOR TABLE public.orders (id, total) WHERE (id > 0), public.customers",
        string options = "publish = 'insert, update', publish_via_partition_root = true",
        string schemas = "FOR TABLES IN SCHEMA app",
        string everything = "FOR ALL TABLES")
        => $"""
            CREATE PUBLICATION pub_orders {orders} WITH ({options});
            CREATE PUBLICATION pub_schema {schemas};
            CREATE PUBLICATION pub_everything {everything};
            CREATE PUBLICATION pub_empty;
            CREATE SCHEMA app;
            CREATE TABLE app.events (id int PRIMARY KEY, kind text);
            CREATE TABLE public.orders (id int PRIMARY KEY, total int, note text);
            CREATE TABLE public.customers (id int PRIMARY KEY, name text);
            """;

    private const string Expected = "pub_empty:false:1111:false:0:0|pub_everything:true:1111:false:0:0|pub_orders:false:1100:true:2:0|pub_schema:false:1111:false:0:1";

    [TestMethod]
    [PostgreSqlVersion(Minimum = 15)]
    public async Task PublicationsDeployConvergeChangeInPlaceRoundTripThroughInitAndAreNotDroppedByAccident()
    {
        var databaseName = await CreateDatabaseAsync("g134");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g134s");
        var projectDirectory = CreateProjectDirectory("g134");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema());

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            // The publications come before the schema and the tables in the file, and still deploy after them.
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(Expected, await Publications(connectionString));
            Assert.AreEqual("id,total|(id > 0)", await ScalarTextAsync(connectionString, "SELECT (SELECT string_agg(a.attname, ',' ORDER BY a.attnum) FROM pg_attribute a WHERE a.attrelid = r.prrelid AND a.attnum = ANY (r.prattrs)) || '|' || pg_get_expr(r.prqual, r.prrelid) FROM pg_publication_rel r WHERE r.prrelid = 'public.orders'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);

            // The row filter, the column list, and the options written another way are the same publication.
            WriteSchema(projectDirectory, Schema(orders: "FOR TABLE public.orders (total, id) WHERE (((id) > 0)), public.customers", options: "publish_via_partition_root = on, publish = 'update,insert'"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            WriteSchema(projectDirectory, Schema());

            // Round trip through init: each publication is in a file of its own, and the scaffold deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.AreEqual(4, Directory.GetFiles(Path.Combine(scaffoldDirectory, "Publication"), "*.sql").Length);
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual(Expected, await Publications(scaffoldConnectionString));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // Options, tables, and schemas change in place with ALTER PUBLICATION: the publication keeps its OID.
            var oid = await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_publication WHERE pubname = 'pub_orders';");
            WriteSchema(projectDirectory, Schema(orders: "FOR TABLE public.orders", options: "publish = 'insert'", schemas: "FOR TABLES IN SCHEMA app, public"));
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, altered.ExitCode, "publish --execute failed.\n" + altered.Output);
            Assert.AreEqual("pub_empty:false:1111:false:0:0|pub_everything:true:1111:false:0:0|pub_orders:false:1000:false:1:0|pub_schema:false:1111:false:0:2", await Publications(connectionString));
            Assert.AreEqual(oid, await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_publication WHERE pubname = 'pub_orders';"), "Altered in place, not dropped and created again.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A publication that stops naming anything has its tables and schemas dropped from it.
            WriteSchema(projectDirectory, Schema(orders: "", options: "publish = 'insert'", schemas: "FOR TABLES IN SCHEMA app, public"));
            var emptied = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, emptied.ExitCode, emptied.Output);
            Assert.AreEqual("pub_empty:false:1111:false:0:0|pub_everything:true:1111:false:0:0|pub_orders:false:1000:false:0:0|pub_schema:false:1111:false:0:2", await Publications(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // FOR ALL TABLES cannot be altered into or out of: that is a drop and a create, and it is not a destructive change, since the publication comes straight back.
            WriteSchema(projectDirectory, Schema(orders: "", options: "publish = 'insert'", schemas: "FOR TABLES IN SCHEMA app, public", everything: "FOR TABLE public.orders"));
            var everythingOid = await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_publication WHERE pubname = 'pub_everything';");
            var recreated = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, recreated.ExitCode, recreated.Output);
            Assert.AreEqual("false", await ScalarTextAsync(connectionString, "SELECT puballtables::text FROM pg_publication WHERE pubname = 'pub_everything';"));
            Assert.AreNotEqual(everythingOid, await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_publication WHERE pubname = 'pub_everything';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Removing one is destructive.
            var withoutEmpty = File.ReadAllText(Path.Combine(projectDirectory, "sql", "schema.sql")).ReplaceLineEndings("\n").Replace("CREATE PUBLICATION pub_empty;\n", string.Empty, StringComparison.Ordinal);
            WriteSchema(projectDirectory, withoutEmpty);
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "publication dropped");
            StringAssert.Contains(await Publications(connectionString), "pub_empty");

            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.IsFalse((await Publications(connectionString)).Contains("pub_empty", StringComparison.Ordinal));
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
    public async Task AProjectThatListsSchemasOnlyManagesThePublicationsThatNameSomethingInThem()
    {
        var databaseName = await CreateDatabaseAsync("g134sc");
        var projectDirectory = CreateProjectDirectory("g134sc", ", \"schemas\": [\"app\"]");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, "CREATE TABLE public.other (id int PRIMARY KEY); CREATE PUBLICATION pub_other FOR TABLE public.other;");
            WriteSchema(projectDirectory, """
                CREATE SCHEMA app;
                CREATE TABLE app.t (id int PRIMARY KEY);
                CREATE PUBLICATION pub_app FOR TABLE app.t;
                """);

            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_publication;"));
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> Publications(string connectionString)
        => ScalarTextAsync(
            connectionString,
            """
            SELECT coalesce(string_agg(p.pubname || ':' || p.puballtables::text || ':' || p.pubinsert::int::text || p.pubupdate::int::text || p.pubdelete::int::text || p.pubtruncate::int::text
                              || ':' || p.pubviaroot::text
                              || ':' || (SELECT count(*) FROM pg_publication_rel r WHERE r.prpubid = p.oid)::text
                              || ':' || (SELECT count(*) FROM pg_publication_namespace n WHERE n.pnpubid = p.oid)::text, '|' ORDER BY p.pubname), '')
            FROM pg_publication p;
            """);
}
