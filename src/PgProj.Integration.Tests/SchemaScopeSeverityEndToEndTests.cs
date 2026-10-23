using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-83: proves the downstream-safety audit's conclusion against a real server, not just its reasoning. A project
/// that declares <c>schemas: ["public"]</c> has a table whose foreign key targets a schema another project could
/// own (<c>other_team</c>, declared nowhere in this project's own SQL and not present in the live database
/// either - genuinely unresolvable, the scenario the audit examined). <c>build</c> must succeed with a warning
/// instead of refusing the whole project, and the real live/diff/dependency-graph/script-generation pipeline
/// (<c>publish --output</c>, against a real disposable server) must still produce correct SQL for the in-scope,
/// resolvable objects, correctly ordered, with the unresolved reference emitted as plain text rather than
/// dropped, corrupted, or blocked.
/// </summary>
/// <remarks>
/// This deliberately stops short of <c>publish --execute</c>: a foreign key naming a schema that genuinely does
/// not exist anywhere fails at the database itself when actually run (PostgreSQL is the right place for that
/// check, per the audit), and since publish executes as one transaction, that failure would roll back the
/// otherwise-independent, in-scope objects too - an artifact of the single-transaction execution model, not a
/// defect in dependency ordering, diff, or script generation. Making the two coexist under a real <c>--execute</c>
/// would require the live loader to also filter its scan by <c>schemas</c> so a live object belonging to another
/// project's schema is never proposed for removal, which is a separate, larger question tracked as G-85.
/// </remarks>
[TestClass]
public sealed class SchemaScopeSeverityEndToEndTests
{
    private const string ProjectSql = """
        CREATE TABLE public.products (
            id bigint PRIMARY KEY,
            name text NOT NULL
        );

        CREATE TABLE public.orders (
            id bigint PRIMARY KEY,
            customer_id bigint NOT NULL REFERENCES other_team.customers (id),
            product_id bigint REFERENCES public.products (id)
        );
        """;

    [TestMethod]
    public async Task AnOutOfScopeForeignKeyWarnsInsteadOfBlocking_AndTheInScopePartsGenerateCorrectly()
    {
        var databaseName = $"g83_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g83-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;

            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g83", "version": "1.0", "source": "./sql", "schemas": ["public"] }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), ProjectSql);

            // 1. build succeeds (exit 0) with a warning naming the out-of-scope reference, not an error.
            var built = await RunAsync(BuildCommand.Create, "build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, "build should succeed despite the out-of-scope reference.\n" + built.Output);
            Assert.Contains("other_team.customers", built.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("[Error]", built.Output, StringComparison.Ordinal);

            // 2. publish --output runs the real live/diff/dependency-graph/script-generation pipeline against the
            //    disposable server (an empty database, so everything is "added") without executing anything, and
            //    the generated script is correct for the in-scope objects: both tables, in dependency order
            //    (products, which orders.product_id references, before orders), the cross-schema FK emitted as
            //    real text rather than a TODO placeholder, and no unsupported-operation refusal.
            var scriptFile = Path.Combine(projectDirectory, "deploy.sql");
            var published = await RunAsync(PublishCommand.Create, "publish", "--project", projectFile, "--target-connection", connectionString, "--output", scriptFile);
            Assert.AreEqual(0, published.ExitCode, "publish --output should succeed.\n" + published.Output);
            Assert.DoesNotContain("[Error]", published.Output, StringComparison.Ordinal);

            var script = await File.ReadAllTextAsync(scriptFile);
            Assert.DoesNotContain("TODO", script, StringComparison.Ordinal);
            StringAssert.Contains(script, "CREATE TABLE \"public\".\"products\"");
            StringAssert.Contains(script, "CREATE TABLE \"public\".\"orders\"");
            StringAssert.Contains(script, "FOREIGN KEY (\"customer_id\") REFERENCES \"other_team\".\"customers\" (\"id\")");
            StringAssert.Contains(script, "FOREIGN KEY (\"product_id\") REFERENCES \"public\".\"products\" (\"id\")");
            Assert.IsLessThan(
                script.IndexOf("FOREIGN KEY (\"product_id\")", StringComparison.Ordinal),
                script.IndexOf("CREATE TABLE \"public\".\"products\"", StringComparison.Ordinal),
                "products must be created before orders' FK to it is added.\n" + script);

            // 3. the same project, without the unresolvable reference, actually deploys and re-diffs empty - proving
            //    the earlier warning left nothing half-modeled for the objects that do resolve.
            const string ResolvableSql = """
                CREATE TABLE public.products (
                    id bigint PRIMARY KEY,
                    name text NOT NULL
                );

                CREATE TABLE public.orders (
                    id bigint PRIMARY KEY,
                    product_id bigint REFERENCES public.products (id)
                );
                """;
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), ResolvableSql);

            var executed = await RunAsync(PublishCommand.Create, "publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, executed.ExitCode, "publish --execute of the resolvable subset should succeed.\n" + executed.Output);
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'products';"));
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'orders';"));

            var diffed = await RunAsync(DiffCommand.Create, "diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, diffed.ExitCode, "diff should succeed.\n" + diffed.Output);
            Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Modified: 0", diffed.Output, StringComparison.Ordinal);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            try
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(Func<CliContext, Command> createCommand, params string[] args)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj") { createCommand(context) };

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

    private static async Task CreateDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {databaseName};", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
