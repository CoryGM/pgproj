using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-47: runs the real <c>pgproj publish --execute</c> against a disposable database for a project that adds columns with
/// <c>ALTER TABLE ... ADD COLUMN</c>. Those statements used to be ignored, so the publish exited 0 and never created the columns.
/// </summary>
[TestClass]
public sealed class CliPublishAlterTableEndToEndTests
{
    // Every new database has the plpgsql extension installed, and publish compares against the whole database, so the project declares
    // it exactly as a real project does (see G-46).
    private const string TablesSql = """
        CREATE EXTENSION plpgsql SCHEMA pg_catalog;

        CREATE TABLE public.customers (
            id bigint PRIMARY KEY,
            email text NOT NULL UNIQUE
        );
        """;

    // A separate file, and a function whose name sorts after the table's, so only a real dependency orders it first.
    private const string FunctionSql = """
        CREATE FUNCTION public.zz_utc_now() RETURNS timestamptz LANGUAGE sql AS $$ SELECT now() AT TIME ZONE 'utc' $$;
        """;

    private const string AlterSql = """
        ALTER TABLE public.customers ADD COLUMN phone varchar(20) NOT NULL DEFAULT 'none';
        ALTER TABLE public.customers ADD COLUMN IF NOT EXISTS email text;
        ALTER TABLE public.customers
            ADD COLUMN created timestamp NOT NULL DEFAULT public.zz_utc_now(),
            ADD COLUMN parent_id bigint REFERENCES public.customers (id);
        """;

    [TestMethod]
    public async Task PublishExecute_CreatesColumnsAddedByAlterTable_ThenConvergesAndAddsLaterOnes()
    {
        var databaseName = $"g47_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g47-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g47", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "1_tables.sql"), TablesSql);
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "2_alter.sql"), AlterSql);
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "3_functions.sql"), FunctionSql);

            // 1. From an empty database: the table is created with every added column, its default, and its foreign key.
            var first = await PublishAsync(projectFile, connectionString);
            Assert.AreEqual(0, first.ExitCode, "First publish failed.\n" + first.Output);
            Assert.AreEqual(4L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'customers' AND column_name IN ('phone', 'created', 'parent_id', 'email');"));
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'customers' AND column_name = 'phone' AND is_nullable = 'NO' AND character_maximum_length = 20 AND column_default LIKE '%none%';"));
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM pg_catalog.pg_constraint WHERE contype = 'f' AND conrelid = 'public.customers'::regclass;"));
            await ExecuteAsync(connectionString, "INSERT INTO public.customers (id, email) VALUES (1, 'a@example.com');");
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM public.customers WHERE id = 1 AND phone = 'none' AND created IS NOT NULL;"), "The defaults of the added columns must apply.");

            // 2. Publishing again is a no-op: the added columns and their defaults read back as the project's.
            var second = await PublishAsync(projectFile, connectionString);
            Assert.AreEqual(0, second.ExitCode, "Second publish failed.\n" + second.Output);
            AssertNoDdl(second.Output, "second publish");

            // 3. A column added later, in the same style, is added to the table that already exists.
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "4_later.sql"), "ALTER TABLE public.customers ADD COLUMN nickname text;");
            var third = await PublishAsync(projectFile, connectionString);
            Assert.AreEqual(0, third.ExitCode, "Publish that adds a column failed.\n" + third.Output);
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'customers' AND column_name = 'nickname';"));
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM public.customers WHERE id = 1;"), "The existing row must survive.");

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

    [TestMethod]
    public async Task PublishExecute_ReportsAnAlterTableActionItCannotApply()
    {
        var databaseName = $"g47w_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g47w-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g47w", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), TablesSql + "\nALTER TABLE public.customers DROP COLUMN email;\nALTER TABLE public.customers ALTER COLUMN id SET STATISTICS 100;\nALTER TABLE public.customers OWNER TO postgres;");

            var result = await PublishAsync(projectFile, connectionString);

            Assert.AreEqual(0, result.ExitCode, result.Output);
            StringAssert.Contains(result.Output, "ALTER TABLE public.customers: DROP COLUMN is not modeled; the action is ignored.");
            StringAssert.Contains(result.Output, "ALTER TABLE public.customers: ALTER COLUMN ... SET STATISTICS is not modeled; the action is ignored.");
            // The owner is modeled now (G-146): it is applied and is not reported as an action the model cannot apply.
            Assert.IsFalse(result.Output.Contains("OWNER TO is not modeled", StringComparison.Ordinal), result.Output);
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'customers' AND column_name = 'email';"), "An action the model cannot apply must not be applied, and must be reported.");
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

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
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
