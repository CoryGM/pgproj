using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-77: real <c>CREATE DOMAIN</c> support against a real server - base type, DEFAULT, NOT NULL, and CHECK
/// constraints - including redeploying a later change to one of those with in-place <c>ALTER DOMAIN</c> rather
/// than dropping the domain (which fails while any column still uses it, the case this gap exists for), and
/// <c>pgproj init</c> scaffolding the same structure back out of a live database.
/// </summary>
[TestClass]
public sealed class DomainEndToEndTests
{
    private const string Sql = """
        CREATE DOMAIN public.positive_amount AS numeric(10,2) DEFAULT 0 CONSTRAINT positive CHECK (VALUE > 0) NOT NULL;

        CREATE TABLE public.invoices (
            id bigint PRIMARY KEY,
            total public.positive_amount
        );
        """;

    [TestMethod]
    public async Task PublishExecute_CreatesADomainWithFullStructure_AndReDiffsEmpty()
    {
        var databaseName = $"g77_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g77-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g77", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), Sql);

            var scriptPath = Path.Combine(projectDirectory, "deploy.sql");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);

            var script = await File.ReadAllTextAsync(scriptPath);
            StringAssert.Contains(script, "CREATE DOMAIN \"public\".\"positive_amount\" AS numeric(10,2) DEFAULT 0 CONSTRAINT \"positive\" CHECK (");
            StringAssert.Contains(script, "NOT NULL;");

            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
            Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Modified: 0", diffed.Output, StringComparison.Ordinal);

            // The domain is used by a real column, so a naive redeploy strategy (drop then recreate) would fail
            // here (2BP01, dependent objects still exist) - this only succeeds because the CHECK constraint change
            // below is deployed with in-place ALTER DOMAIN instead (G-77).
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "sql", "schema.sql"),
                Sql.Replace("CHECK (VALUE > 0)", "CHECK (VALUE > 0) CONSTRAINT max_amount CHECK (VALUE < 1000000)", StringComparison.Ordinal));

            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, republished.ExitCode, "publish --execute (ALTER DOMAIN) failed.\n" + republished.Output);

            var alterScript = await File.ReadAllTextAsync(scriptPath);
            StringAssert.Contains(alterScript, "ALTER DOMAIN \"public\".\"positive_amount\" ADD CONSTRAINT \"max_amount\" CHECK (");
            Assert.IsFalse(alterScript.Contains("DROP DOMAIN", StringComparison.Ordinal), alterScript);

            var rediffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, rediffed.ExitCode, rediffed.Output);
            Assert.Contains("Added: 0", rediffed.Output, StringComparison.Ordinal);
            Assert.Contains("Removed: 0", rediffed.Output, StringComparison.Ordinal);
            Assert.Contains("Modified: 0", rediffed.Output, StringComparison.Ordinal);
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
    public async Task Init_ScaffoldsADomainWithFullStructure_ThatDiffsEmptyAgainstTheSameDatabase()
    {
        var databaseName = $"g77init_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g77init-" + Guid.NewGuid().ToString("N"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(Sql, connection);
                await command.ExecuteNonQueryAsync();
            }

            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var initResult = await RunAsync("init", "--project", projectFile, "--output", Path.Combine(projectDirectory, "sql"), "--source-connection", connectionString);
            Assert.AreEqual(0, initResult.ExitCode, "init failed.\n" + initResult.Output);

            var typeFile = Path.Combine(projectDirectory, "sql", "Schema", "public", "Type", "positive_amount.sql");
            Assert.IsTrue(File.Exists(typeFile));
            var typeSql = await File.ReadAllTextAsync(typeFile);
            StringAssert.Contains(typeSql, "CREATE DOMAIN");
            StringAssert.Contains(typeSql, "DEFAULT");
            StringAssert.Contains(typeSql, "CHECK");
            StringAssert.Contains(typeSql, "NOT NULL");

            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
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

    /// <summary>
    /// G-98: the tests above use a named CHECK. An unnamed one reads back from the server under PostgreSQL's default name
    /// (<c>&lt;domain&gt;_check</c>), which used to look like a different CHECK: every publish tried to drop and recreate the
    /// domain, and once a column used it, that failed (2BP01). It must converge, and an ordinary change next to it must
    /// still be deployed in place.
    /// </summary>
    [TestMethod]
    public async Task UnnamedChecks_OnADomainInUse_Converge_AndADefaultChangeIsAlteredInPlace()
    {
        const string UnnamedSql = """
            CREATE DOMAIN public.pn AS numeric(10,2) DEFAULT 1 CHECK (VALUE > 0) CHECK (VALUE < 1000);
            CREATE DOMAIN public.code AS text CHECK (VALUE IN ('a', 'b')) CHECK (length(VALUE) < 5);
            CREATE TABLE public.items (id bigint PRIMARY KEY, amount public.pn, kind public.code);
            """;

        var databaseName = $"g98_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g98-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g98", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(sqlFile, UnnamedSql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");

            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "second publish --execute failed.\n" + republished.Output);

            await File.WriteAllTextAsync(sqlFile, UnnamedSql.Replace("DEFAULT 1", "DEFAULT 2", StringComparison.Ordinal));
            var scriptPath = Path.Combine(projectDirectory, "deploy.sql");
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, changed.ExitCode, "publish --execute of a DEFAULT change failed.\n" + changed.Output);
            var script = await File.ReadAllTextAsync(scriptPath);
            StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"pn\" SET DEFAULT 2;");
            Assert.IsFalse(script.Contains("DROP DOMAIN", StringComparison.Ordinal), script);
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");
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

    /// <summary>
    /// G-109: a change to an unnamed CHECK on a domain in use used to fall back to DROP DOMAIN, which fails (2BP01). It is now altered in
    /// place: a changed or removed CHECK is dropped under the name the database gave it, and a new one is added unnamed.
    /// </summary>
    [TestMethod]
    public async Task UnnamedChecks_OnADomainInUse_AreChangedAddedAndRemovedInPlace()
    {
        const string BaseSql = """
            CREATE DOMAIN public.code AS text CHECK (VALUE IN ('a', 'b')) CHECK (length(VALUE) < 5);
            CREATE TABLE public.items (id bigint PRIMARY KEY, kind public.code);
            """;

        var databaseName = $"g109_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g109-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g109", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(sqlFile, BaseSql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var insert = new NpgsqlCommand("INSERT INTO public.items VALUES (1, 'a');", connection);
                await insert.ExecuteNonQueryAsync();
            }

            // Changed, then added, then removed: each is deployed in place, never as DROP DOMAIN, and converges.
            var sql = BaseSql.Replace("length(VALUE) < 5", "length(VALUE) < 6", StringComparison.Ordinal);
            await PublishInPlaceAsync(sqlFile, sql, projectFile, connectionString, projectDirectory, "DROP CONSTRAINT \"code_check1\"", "ADD CHECK (length(VALUE) < 6)");

            sql = sql.Replace("CHECK (length(VALUE) < 6);", "CHECK (length(VALUE) < 6) CHECK (VALUE <> 'zz');", StringComparison.Ordinal);
            await PublishInPlaceAsync(sqlFile, sql, projectFile, connectionString, projectDirectory, "ADD CHECK (VALUE <> 'zz')");

            sql = sql.Replace("CHECK (VALUE IN ('a', 'b')) ", string.Empty, StringComparison.Ordinal);
            await PublishInPlaceAsync(sqlFile, sql, projectFile, connectionString, projectDirectory, "DROP CONSTRAINT \"code_check\"");
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

    private static async Task PublishInPlaceAsync(string sqlFile, string sql, string projectFile, string connectionString, string projectDirectory, params string[] expectedStatements)
    {
        await File.WriteAllTextAsync(sqlFile, sql);
        var scriptPath = Path.Combine(projectDirectory, $"deploy-{Guid.NewGuid():N}.sql");
        var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
        var script = await File.ReadAllTextAsync(scriptPath);
        Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output + "\n" + script);
        foreach (var expected in expectedStatements)
        {
            StringAssert.Contains(script, expected);
        }

        Assert.IsFalse(script.Contains("DROP DOMAIN", StringComparison.Ordinal), script);
        await AssertDiffAsync(projectFile, connectionString, "Modified: 0");
    }

    private static async Task AssertDiffAsync(string projectFile, string connectionString, string expectedModified)
    {
        var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
        Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
        Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains(expectedModified, diffed.Output, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj") { InitCommand.Create(context), BuildCommand.Create(context), DiffCommand.Create(context), PublishCommand.Create(context) };

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
}
