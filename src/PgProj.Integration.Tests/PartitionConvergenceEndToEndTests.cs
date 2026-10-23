using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-101 against a real server. PostgreSQL copies a partitioned parent's primary/unique/foreign keys, its CHECK
/// constraints, and its indexes onto each partition, and rewrites a bound's literals in the key's type. The live
/// loader read the copies as the partition's own objects (phantom removals) and the bound as changed (a blocking
/// placeholder), so a partition of a keyed table never converged and every later publish was refused. <c>pgproj init</c>,
/// which reads through the same queries, scaffolded the copies into each partition's file, so its project could not be
/// deployed ("multiple primary keys").
/// </summary>
[TestClass]
public sealed class PartitionConvergenceEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA s;
        CREATE TABLE s.log (id bigserial, at timestamptz NOT NULL DEFAULT now(), what text, PRIMARY KEY (id, at)) PARTITION BY RANGE (at);
        CREATE TABLE s.log_2026 PARTITION OF s.log FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
        CREATE TABLE s.log_old PARTITION OF s.log FOR VALUES FROM (MINVALUE) TO ('2026-01-01');
        CREATE TABLE s.log_rest PARTITION OF s.log DEFAULT;
        CREATE TABLE s.d (id int, day date, amt numeric(10,2)) PARTITION BY RANGE (day, amt);
        CREATE TABLE s.d1 PARTITION OF s.d FOR VALUES FROM ('2026-01-01', 0) TO ('2026-12-31', 1.5);
        CREATE TABLE s.h (id int PRIMARY KEY) PARTITION BY HASH (id);
        CREATE TABLE s.h0 PARTITION OF s.h FOR VALUES WITH (MODULUS 2, REMAINDER 0);
        CREATE TABLE s.h1 PARTITION OF s.h FOR VALUES WITH (modulus 2, remainder 1);
        CREATE TABLE s.ref (id int PRIMARY KEY);
        CREATE TABLE s.p (id int, r int REFERENCES s.ref (id), k int CHECK (k > 0), PRIMARY KEY (id, k)) PARTITION BY LIST (k);
        CREATE TABLE s.p1 PARTITION OF s.p (CONSTRAINT p1_own_check CHECK (id > 0)) FOR VALUES IN (1);
        CREATE INDEX p_r_idx ON s.p (r);
        CREATE INDEX p1_own_idx ON s.p1 (id);
        """;

    [TestMethod]
    public async Task PartitionsOfKeyedTablesConverge_ARealBoundChangeIsStillSeen_AndInitScaffoldDeploys()
    {
        var databaseName = $"g101_{Guid.NewGuid():N}";
        var scaffoldDatabaseName = $"g101s_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g101-" + Guid.NewGuid().ToString("N"));
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g101s-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));
        Directory.CreateDirectory(scaffoldDirectory);

        await CreateDatabaseAsync(databaseName);
        await CreateDatabaseAsync(scaffoldDatabaseName);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g101", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(sqlFile, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffAsync(projectFile, connectionString, "Removed: 0", "Modified: 0");

            // The second publish used to be refused (a -- TODO placeholder for the "changed" bound).
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "second publish --execute failed.\n" + republished.Output);

            // A real bound change is still seen, and still blocked by G-24c's placeholder rather than attempted.
            await File.WriteAllTextAsync(sqlFile, Sql.Replace("TO ('2027-01-01')", "TO ('2028-01-01')", StringComparison.Ordinal));
            await AssertDiffAsync(projectFile, connectionString, "Removed: 0", "Modified: 1");
            var blocked = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, blocked.ExitCode, blocked.Output);
            StringAssert.Contains(blocked.Output, "s.log_2026");
            await File.WriteAllTextAsync(sqlFile, Sql);

            // init reads through the same queries: its scaffold must hold only each partition's own objects, so it deploys
            // into an empty database and re-diffs empty there.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var partitionFile = await File.ReadAllTextAsync(Path.Combine(scaffoldDirectory, "sql", "Schema", "s", "Table", "p1.sql"));
            StringAssert.Contains(partitionFile, "p1_own_check");
            StringAssert.Contains(partitionFile, "p1_own_idx");
            Assert.IsFalse(partitionFile.Contains("p1_pkey", StringComparison.Ordinal), partitionFile);
            Assert.IsFalse(partitionFile.Contains("p_k_check", StringComparison.Ordinal), partitionFile);
            Assert.IsFalse(partitionFile.Contains("p_r_fkey", StringComparison.Ordinal), partitionFile);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, "publish of the init scaffold failed.\n" + deployedScaffold.Output);
            await AssertDiffAsync(scaffoldProject, scaffoldConnectionString, "Removed: 0", "Modified: 0");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    private static string ConnectionStringFor(string databaseName)
        => new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;

    private static async Task AssertDiffAsync(string projectFile, string connectionString, string expectedRemoved, string expectedModified)
    {
        var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
        Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
        Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains(expectedRemoved, diffed.Output, StringComparison.Ordinal);
        Assert.Contains(expectedModified, diffed.Output, StringComparison.Ordinal);
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

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Task CreateDatabaseAsync(string databaseName)
        => ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");

    private static Task DropDatabaseAsync(string databaseName)
        => ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");

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
