using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-44: runs the real <c>pgproj publish --execute</c> command (project loading, live model, diff, script
/// generation, the unsupported-operation check, and transactional execution) against a disposable database.
/// The other integration tests execute generated scripts directly and so never exercised the CLI's
/// fail-closed check, which used to refuse any publish that added a table or a column.
/// </summary>
[TestClass]
public sealed class CliPublishEndToEndTests
{
    // Every new database has the plpgsql extension installed, and publish compares against the whole database, so
    // the project declares it exactly as a real project does. (An undeclared extension is planned for removal; see G-46.)
    private const string InitialSql = """
        CREATE EXTENSION plpgsql SCHEMA pg_catalog;

        CREATE TABLE public.customers (
            id bigint PRIMARY KEY,
            email text NOT NULL UNIQUE,
            name text
        );

        CREATE TABLE public.orders (
            id bigint PRIMARY KEY,
            customer_id bigint REFERENCES public.customers,
            total numeric(10,2) NOT NULL
        );

        CREATE INDEX orders_customer_id_idx ON public.orders (customer_id);

        CREATE FUNCTION public.audit_order() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            -- TODO: record the change somewhere (a user comment, not a pgproj placeholder)
            RETURN NEW;
        END;
        $$;

        CREATE TRIGGER orders_audit_trg AFTER INSERT ON public.orders FOR EACH ROW EXECUTE FUNCTION public.audit_order();
        """;

    // The added column belongs in CREATE TABLE: a state-based project declares the final shape of each table.
    private static readonly string ExtendedSql = InitialSql.Replace("name text", "name text,\n    phone text", StringComparison.Ordinal) + """


        CREATE TABLE public.products (
            id bigint PRIMARY KEY,
            sku text NOT NULL UNIQUE,
            price numeric(10,2)
        );

        CREATE INDEX products_sku_idx ON public.products (price);
        """;

    [TestMethod]
    public async Task PublishExecute_AddsTablesAndColumns_ThenConvergesToNoChanges()
    {
        var databaseName = $"g44_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g44-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g44", "version": "1.0", "source": "./sql" }""");

            // 1. Publish from an empty database: adds tables, columns, constraints, an index, a function, and a trigger.
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), InitialSql);
            var first = await PublishAsync(projectFile, connectionString);
            Assert.AreEqual(0, first.ExitCode, "First publish failed.\n" + first.Output);
            Assert.IsFalse(first.Output.Contains("could only emit a TODO", StringComparison.Ordinal), first.Output);
            Assert.AreEqual(2L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ('customers', 'orders');"));
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM pg_catalog.pg_trigger WHERE tgname = 'orders_audit_trg' AND NOT tgisinternal;"));
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM pg_catalog.pg_constraint WHERE conname = 'orders_customer_id_fkey';"));

            // 2. Publishing the same project again must be a no-op.
            var second = await PublishAsync(projectFile, connectionString);
            Assert.AreEqual(0, second.ExitCode, "Second publish failed.\n" + second.Output);
            AssertNoDdl(second.Output, "second publish");

            // 3. Add a column and a table (the changes the CLI used to refuse).
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), ExtendedSql);
            var third = await PublishAsync(projectFile, connectionString);
            Assert.AreEqual(0, third.ExitCode, "Publish that adds a column and a table failed.\n" + third.Output);
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'customers' AND column_name = 'phone';"));
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'products';"));

            // 4. And it converges again.
            var fourth = await PublishAsync(projectFile, connectionString);
            Assert.AreEqual(0, fourth.ExitCode, "Final publish failed.\n" + fourth.Output);
            AssertNoDdl(fourth.Output, "final publish");
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

    private static void AssertNoDdl(string output, string step)
    {
        foreach (var keyword in new[] { "CREATE ", "ALTER ", "DROP " })
        {
            Assert.IsFalse(output.Contains(keyword, StringComparison.Ordinal), $"Expected the {step} to change nothing but the script contained '{keyword.Trim()}':\n{output}");
        }
    }

    private static async Task<(int ExitCode, string Output)> PublishAsync(string projectFile, string connectionString)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj") { PublishCommand.Create(context) };

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = await root.Parse(["publish", "--project", projectFile, "--target-connection", connectionString, "--execute"]).InvokeAsync();
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
