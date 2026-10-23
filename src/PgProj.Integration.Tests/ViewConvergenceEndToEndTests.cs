using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-96 against a real server: views written the ordinary way (a <c>public.</c> qualifier, an alias without <c>AS</c>,
/// <c>*</c>, an untyped literal, <c>IN</c>/<c>LIKE</c>/<c>BETWEEN</c>) used to re-diff after every deploy, because
/// PostgreSQL stores its own rendering of a view; a materialized view was dropped and recreated on every publish, which
/// <c>--allow-destructive</c> then had to approve. The project's body is now rendered by the server too.
/// </summary>
[TestClass]
public sealed class ViewConvergenceEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.o (id int, cid int);
        CREATE TABLE public.c (id int, name text);
        CREATE VIEW public.v_plain AS SELECT id, name FROM public.c WHERE id > 5;
        CREATE VIEW public.v_sub AS SELECT c.id, c.name, (SELECT count(*) FROM public.o WHERE o.cid = c.id) AS n FROM public.c c;
        CREATE MATERIALIZED VIEW public.mv AS SELECT count(*) n FROM public.o;
        CREATE SCHEMA s;
        CREATE TABLE s.c (id int, name text);
        CREATE VIEW s.v_star AS SELECT * FROM s.c;
        CREATE VIEW s.v_where AS SELECT id FROM s.c WHERE id > 5 AND name = 'x';
        CREATE VIEW s.v_forms AS SELECT id FROM s.c WHERE id IN (1, 2) AND name LIKE 'a%' AND id BETWEEN 1 AND 9;
        """;

    [TestMethod]
    public async Task ViewsConverge_MaterializedViewIsNotRebuilt_AndARealChangeIsStillDeployed()
    {
        var databaseName = $"g96_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g96-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g96", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(sqlFile, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");

            // A second publish must not rebuild the materialized view, and so must not need --allow-destructive.
            var materializedViewOid = await ScalarAsync<uint>(connectionString, "SELECT 'public.mv'::regclass::oid;");
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "second publish --execute failed.\n" + republished.Output);
            Assert.AreEqual(materializedViewOid, await ScalarAsync<uint>(connectionString, "SELECT 'public.mv'::regclass::oid;"));

            // A real change to a view is still seen and deployed, and converges afterwards.
            await File.WriteAllTextAsync(sqlFile, Sql.Replace("name = 'x'", "name = 'y'", StringComparison.Ordinal));
            await AssertDiffAsync(projectFile, connectionString, "Modified: 1");
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute of a changed view failed.\n" + changed.Output);
            StringAssert.Contains(await ScalarAsync<string>(connectionString, "SELECT pg_get_viewdef('s.v_where'::regclass, true);"), "'y'::text");
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");

            // Nothing the renderer created is left behind.
            Assert.AreEqual(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM pg_catalog.pg_class WHERE relname LIKE 'pgproj_render%';"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ARoleThatCannotCreateTemporaryViews_StillGetsAWorkingDiff()
    {
        var databaseName = $"g96t_{Guid.NewGuid():N}";
        var role = $"g96_reader_{Guid.NewGuid():N}"[..24];
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g96t-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var ownerConnectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g96t", "version": "1.0", "source": "./sql", "excludeGrants": true }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), "CREATE TABLE public.c (id int); CREATE VIEW public.v AS SELECT id FROM public.c;");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", ownerConnectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            await ExecuteAsync(ownerConnectionString, $"""
                CREATE ROLE {role} LOGIN PASSWORD 'r';
                REVOKE TEMPORARY ON DATABASE {databaseName} FROM PUBLIC;
                GRANT USAGE ON SCHEMA public TO {role};
                GRANT SELECT ON ALL TABLES IN SCHEMA public TO {role};
                """);
            var readerConnectionString = new NpgsqlConnectionStringBuilder(ownerConnectionString) { Username = role, Password = "r" }.ConnectionString;

            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", readerConnectionString);

            Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
            Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
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

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
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
        var root = new RootCommand("pgproj") { BuildCommand.Create(context), DiffCommand.Create(context), PublishCommand.Create(context) };

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
