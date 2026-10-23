using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-93 against a real server. <c>build</c> refuses a reference PostgreSQL would not find along the search path, where it used to pass
/// and fail at <c>publish</c>. A project that sets <c>searchPath</c> writes names without a schema, is deployed under that path, and converges.
/// And the live database is read with a fixed path, so a database whose own <c>search_path</c> includes a project schema no longer reads
/// that schema's indexes and types unqualified and reports them changed.
/// </summary>
[TestClass]
public sealed class SearchPathEndToEndTests
{
    private const string ConfiguredSql = """
        CREATE SCHEMA IF NOT EXISTS shop;
        CREATE TABLE public.region (id int PRIMARY KEY, name text);
        CREATE TYPE mood AS ENUM ('ok', 'sad');
        CREATE TABLE customer (id bigint PRIMARY KEY, region_id int REFERENCES region (id), m mood DEFAULT 'ok');
        CREATE TABLE orders (id bigint PRIMARY KEY, customer_id bigint REFERENCES customer (id), amount numeric NOT NULL);
        CREATE INDEX orders_customer_idx ON orders (customer_id);
        CREATE INDEX region_name_idx ON region (name);
        CREATE FUNCTION order_total(p bigint) RETURNS numeric LANGUAGE sql AS $$ SELECT coalesce(sum(amount), 0) FROM orders WHERE customer_id = p $$;
        CREATE FUNCTION customer_mood(c mood) RETURNS text LANGUAGE sql AS $$ SELECT c::text $$;
        CREATE VIEW customer_totals AS SELECT c.id, r.name AS region, order_total(c.id) AS total FROM customer c LEFT JOIN region r ON r.id = c.region_id;
        CREATE FUNCTION touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER orders_touch BEFORE UPDATE ON orders FOR EACH ROW EXECUTE FUNCTION touch();
        ALTER TABLE orders ADD COLUMN note text DEFAULT customer_mood('ok');
        """;

    [TestMethod]
    public async Task AProjectWithASearchPathIsDeployedUnderItAndConverges()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, "\"searchPath\": [\"shop\", \"public\"]", ConfiguredSql);

            var built = await RunAsync("build", "--project", projectFile);
            StringAssert.Contains(built.Output, "No diagnostics.");

            var scriptPath = Path.Combine(directory, "first.sql");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            StringAssert.StartsWith(await File.ReadAllTextAsync(scriptPath), "SET search_path TO shop, public;");

            Assert.AreEqual(
                "public.region|public.region_name_idx|shop.customer|shop.customer_totals|shop.orders|shop.orders_customer_idx",
                await ScalarAsync(connectionString, "SELECT string_agg(n.nspname || '.' || c.relname, '|' ORDER BY n.nspname || '.' || c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname IN ('shop', 'public') AND c.relkind IN ('r', 'v', 'i') AND c.relname NOT LIKE '%pkey';"));
            Assert.AreEqual("shop.mood", await ScalarAsync(connectionString, "SELECT format_type(atttypid, null) FROM pg_attribute WHERE attrelid = 'shop.customer'::regclass AND attname = 'm';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var secondScript = Path.Combine(directory, "second.sql");
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", secondScript);
            Assert.AreEqual(0, republished.ExitCode, republished.Output);
            var second = File.Exists(secondScript) ? await File.ReadAllTextAsync(secondScript) : string.Empty;
            Assert.IsFalse(second.Contains("CREATE", StringComparison.Ordinal) || second.Contains("DROP", StringComparison.Ordinal) || second.Contains("ALTER", StringComparison.Ordinal), second);
        });
    }

    [TestMethod]
    public async Task WithoutTheSchemaOnThePathBuildRefusesWhatPublishWouldFailOn()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, null, """
                CREATE SCHEMA shop;
                CREATE TABLE shop.customer (id bigint PRIMARY KEY);
                CREATE FUNCTION shop.order_total(p bigint) RETURNS numeric LANGUAGE sql AS $$ SELECT 1::numeric $$;
                CREATE VIEW shop.customer_totals AS SELECT c.id, order_total(c.id) AS total FROM shop.customer c;
                """);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(1, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "View 'shop.customer_totals' references the function 'order_total' without a schema");

            // publish refuses too, before anything reaches the server.
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, published.ExitCode, published.Output);
            StringAssert.Contains(published.Output, "without a schema");

            // And it is right to: PostgreSQL rejects the same SQL under its default path.
            var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connectionString, """
                CREATE SCHEMA shop;
                CREATE TABLE shop.customer (id bigint PRIMARY KEY);
                CREATE FUNCTION shop.order_total(p bigint) RETURNS numeric LANGUAGE sql AS $$ SELECT 1::numeric $$;
                CREATE VIEW shop.customer_totals AS SELECT c.id, order_total(c.id) AS total FROM shop.customer c;
                """));
            Assert.AreEqual("42883", exception.SqlState);
        });
    }

    [TestMethod]
    public async Task ADatabaseWithItsOwnSearchPathIsReadTheSameWay()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"ALTER DATABASE {database} SET search_path = app, public;");
            var projectFile = await WriteProjectAsync(directory, null, """
                CREATE SCHEMA app;
                CREATE TYPE app.st AS ENUM ('a');
                CREATE TABLE public.t (id int PRIMARY KEY);
                CREATE TABLE app.c (id int PRIMARY KEY, t_id int REFERENCES public.t (id), s app.st);
                CREATE INDEX c_t_idx ON app.c (t_id);
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = $"g93_{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g93-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "sql"));
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            await test(ConnectionStringFor(databaseName), directory);
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            TryDeleteDirectory(directory);
        }
    }

    private static async Task<string> WriteProjectAsync(string directory, string? extraProperty, string sql)
    {
        var projectFile = Path.Combine(directory, "pgproj.json");
        var extra = extraProperty is null ? string.Empty : ", " + extraProperty;
        await File.WriteAllTextAsync(projectFile, "{ \"name\": \"g93\", \"version\": \"1.0\", \"source\": \"./sql\"" + extra + " }");
        await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), sql);
        return projectFile;
    }

    private static string ConnectionStringFor(string databaseName)
        => new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;

    private static async Task AssertDiffIsEmptyAsync(string projectFile, string connectionString)
    {
        var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
        Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
        Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Modified: 0", diffed.Output, StringComparison.Ordinal);
    }

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj")
        {
            InitCommand.Create(context),
            BuildCommand.Create(context),
            DiffCommand.Create(context),
            PublishCommand.Create(context),
        };

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = await root.Parse(args).InvokeAsync();
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
