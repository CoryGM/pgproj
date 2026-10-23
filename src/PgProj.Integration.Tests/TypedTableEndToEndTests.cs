using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-136 against a real server, on every version: typed tables (<c>CREATE TABLE t OF some_type [(col WITH OPTIONS ...)]</c>) take their columns from their composite type,
/// are created after it whatever order the files are in, read back with <c>reloftype</c>, converge <c>0/0/0</c>, round-trip through <c>init</c>, and follow their type:
/// a field added to it or dropped from it is carried to the table by <c>ALTER TYPE ... CASCADE</c>, which PostgreSQL requires while a typed table is of the type, and the table's
/// own change is left to what a table can change (a default, NOT NULL). Before G-136 a project's typed table was reported and left out and a database's was not read.
/// </summary>
[TestClass]
public sealed class TypedTableEndToEndTests
{
    private static string Schema(string orderFields = "id int, status text, total numeric(10,2)", string statusOptions = "DEFAULT 'new' NOT NULL", string extraOptions = "")
        => $"""
            CREATE VIEW public.open_orders AS SELECT id, status FROM app.orders WHERE status = 'new';
            CREATE TABLE app.orders OF app.order_type (id WITH OPTIONS PRIMARY KEY, status WITH OPTIONS {statusOptions}{extraOptions});
            CREATE TABLE public.plain_users OF public.user_type;
            CREATE INDEX orders_status_idx ON app.orders (status);
            CREATE SCHEMA app;
            CREATE TYPE app.order_type AS ({orderFields});
            CREATE TYPE public.user_type AS (id int, name text);
            """;

    [TestMethod]
    public async Task TypedTablesFollowTheirTypeAndConvergeAndRoundTripThroughInit()
    {
        var databaseName = await CreateDatabaseAsync("g136");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g136s");
        var projectDirectory = CreateProjectDirectory("g136");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema());

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            // The tables and the view come before the types in the file, and still deploy after them.
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("app.order_type|id,status,total", await OrdersShape(connectionString));
            Assert.AreEqual("user_type", await ScalarTextAsync(connectionString, "SELECT reloftype::regtype::text FROM pg_class WHERE oid = 'public.plain_users'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The table works, and its own default and NOT NULL are on the column.
            await ExecuteAsync(connectionString, "INSERT INTO app.orders (id, total) VALUES (1, 9.50), (2, 20);");
            Assert.AreEqual("new|2", await ScalarTextAsync(connectionString, "SELECT (SELECT status FROM app.orders WHERE id = 1) || '|' || (SELECT count(*) FROM public.open_orders)::text;"));

            // A second publish changes nothing, and the same table written another way is the same table.
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            WriteSchema(projectDirectory, Schema(statusOptions: "NOT NULL DEFAULT 'new'"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            WriteSchema(projectDirectory, Schema());

            // Round trip through init: the scaffold writes each typed table as OF its type, and deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var tableFile = File.ReadAllText(Path.Combine(scaffoldDirectory, "Schema", "app", "Table", "orders.sql"));
            StringAssert.Contains(tableFile, "OF app.order_type");
            StringAssert.Contains(tableFile, "WITH OPTIONS");
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual("app.order_type|id,status,total", await OrdersShape(scaffoldConnectionString));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // A table's own default changes in place.
            WriteSchema(projectDirectory, Schema(statusOptions: "DEFAULT 'open' NOT NULL"));
            var redefaulted = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, redefaulted.ExitCode, redefaulted.Output);
            Assert.AreEqual("'open'::text", await ScalarTextAsync(connectionString, "SELECT pg_get_expr(adbin, adrelid) FROM pg_attrdef WHERE adrelid = 'app.orders'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A field added to the type reaches the table through ALTER TYPE ... CASCADE, with the default the table gives it; the rows stay.
            WriteSchema(projectDirectory, Schema(orderFields: "id int, status text, total numeric(10,2), note text", statusOptions: "DEFAULT 'open' NOT NULL", extraOptions: ", note WITH OPTIONS DEFAULT 'none'"));
            var added = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, added.ExitCode, "publish --execute failed.\n" + added.Output);
            Assert.AreEqual("app.order_type|id,status,total,note", await OrdersShape(connectionString));
            Assert.AreEqual("2|'none'::text", await ScalarTextAsync(connectionString, "SELECT (SELECT count(*) FROM app.orders)::text || '|' || (SELECT pg_get_expr(adbin, adrelid) FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum WHERE d.adrelid = 'app.orders'::regclass AND a.attname = 'note');"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A field dropped from the type is data loss, so it needs --allow-destructive, and then the table loses the column with the type.
            WriteSchema(projectDirectory, Schema(orderFields: "id int, status text, note text", statusOptions: "DEFAULT 'open' NOT NULL", extraOptions: ", note WITH OPTIONS DEFAULT 'none'"));
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "total");
            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, allowed.ExitCode, "publish --execute failed.\n" + allowed.Output);
            Assert.AreEqual("app.order_type|id,status,note", await OrdersShape(connectionString));
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
    public async Task ATypedTableOfATypeTheProjectDoesNotDeclareIsAnError()
    {
        var projectDirectory = CreateProjectDirectory("g136e");
        try
        {
            WriteSchema(projectDirectory, "CREATE TABLE public.typed OF public.nowhere;\n");

            var built = await RunAsync("build", "--project", ProjectFile(projectDirectory));

            Assert.AreEqual(1, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "not a composite type this project declares");
        }
        finally
        {
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> OrdersShape(string connectionString)
        => ScalarTextAsync(
            connectionString,
            "SELECT c.reloftype::regtype::text || '|' || (SELECT string_agg(a.attname, ',' ORDER BY a.attnum) FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped) FROM pg_class c WHERE c.oid = 'app.orders'::regclass;");
}
