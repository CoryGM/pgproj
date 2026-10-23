using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-172 against a real server. A <c>CHECK</c> that compares an enum column to a literal, tests it with <c>IN</c>, or uses a column of a
/// domain type, and one that calls <c>currval('s.seq')</c> without a cast, was read as modified after every publish, because PostgreSQL stores
/// the expression with typed literals, <c>= ANY (ARRAY[...])</c>, base-type casts of domain columns, and a <c>regclass</c> cast. The
/// diff now compares PostgreSQL's rendering of the project's expression with its rendering of the live one.
/// </summary>
[TestClass]
public sealed class CheckConstraintConvergenceEndToEndTests
{
    private const string Sql = """
        CREATE DOMAIN s.money AS numeric(12,2) CONSTRAINT money_nonneg CHECK (VALUE >= 0);
        CREATE TYPE s.st AS ENUM ('draft', 'placed', 'cancelled');
        CREATE SEQUENCE s.seq;
        CREATE TABLE s.t (
            id int,
            status s.st NOT NULL DEFAULT 'draft',
            amount s.money NOT NULL,
            discount s.money NOT NULL DEFAULT 0,
            subtotal s.money NOT NULL DEFAULT 0,
            addr int,
            processed timestamp,
            CONSTRAINT ck_in CHECK (status IN ('draft', 'cancelled') OR addr IS NOT NULL),
            CONSTRAINT ck_eq CHECK ((status = 'draft') = (processed IS NULL)),
            CONSTRAINT ck_dom CHECK (discount <= subtotal),
            CONSTRAINT ck_pos CHECK (amount > 0),
            CONSTRAINT ck_seq CHECK (id < currval('s.seq')),
            CONSTRAINT ck_plain CHECK (id > 0 AND id < 1000)
        );
        """;

    [TestMethod]
    public async Task ChecksOnEnumsDomainsAndInListsConverge_ARealChangeIsDeployed()
    {
        var databaseName = $"g172_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g172-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g172", "version": "1.0", "source": "./sql", "schemas": ["s"] }""");
            await File.WriteAllTextAsync(sqlFile, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");

            // Nothing to deploy, so the second publish writes no constraint statements.
            var scriptPath = Path.Combine(projectDirectory, "second.sql");
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, republished.ExitCode, republished.Output);
            var secondScript = File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath) : string.Empty;
            Assert.IsFalse(secondScript.Contains("CHECK", StringComparison.Ordinal), secondScript);

            // A real change to an IN list and to a plain check is still seen, deployed, and converges.
            await File.WriteAllTextAsync(sqlFile, Sql
                .Replace("status IN ('draft', 'cancelled')", "status IN ('draft')", StringComparison.Ordinal)
                .Replace("id < 1000", "id < 2000", StringComparison.Ordinal));
            var changedDiff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.Contains("ck_in", changedDiff.Output, StringComparison.Ordinal);
            Assert.Contains("ck_plain", changedDiff.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("ck_eq", changedDiff.Output, StringComparison.Ordinal);
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");
            StringAssert.Contains(await ScalarAsync(connectionString, "SELECT pg_get_expr(conbin, conrelid) FROM pg_constraint WHERE conname = 'ck_plain';"), "2000");
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static async Task AssertDiffAsync(string projectFile, string connectionString, string expectedModified)
    {
        var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
        Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
        Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains(expectedModified, diffed.Output, StringComparison.Ordinal);
    }

    private static async Task<string?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync() as string;
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
