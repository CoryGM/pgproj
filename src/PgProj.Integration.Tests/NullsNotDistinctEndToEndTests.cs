using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-262 against a real server: <c>NULLS NOT DISTINCT</c> on a unique index and a unique constraint is deployed, converges, is scaffolded by <c>init</c>, and a change is made.
/// </summary>
[TestClass]
public sealed class NullsNotDistinctEndToEndTests
{
    private const string Query = "SELECT string_agg(c.relname || ':' || i.indnullsnotdistinct::text, ',' ORDER BY c.relname) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE i.indrelid = 'public.t'::regclass";

    private const string Objects = """
        CREATE TABLE public.t (a integer, b integer, c integer UNIQUE NULLS NOT DISTINCT, d integer);
        CREATE UNIQUE INDEX t_a ON public.t (a) NULLS NOT DISTINCT;
        ALTER TABLE public.t ADD CONSTRAINT t_b_uq UNIQUE NULLS NOT DISTINCT (b);
        CREATE UNIQUE INDEX t_d ON public.t (d);
        """;

    [TestMethod]
    [PostgreSqlVersion(Minimum = 15)]
    public async Task NullsNotDistinctIsDeployedConvergesIsScaffoldedAndChanged()
    {
        var databaseName = await CreateDatabaseAsync("g262");
        var projectDirectory = CreateProjectDirectory("g262");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g262-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g262b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string expected = "t_a:true,t_b_uq:true,t_c_key:true,t_d:false";

            WriteSchema(projectDirectory, Objects);
            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(expected, await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The database enforces it: a second NULL is refused.
            var refused = await TryExecuteAsync(connectionString, "INSERT INTO public.t (a, b, c, d) VALUES (NULL, 1, 1, 1), (NULL, 2, 2, 2)");
            Assert.IsFalse(refused, "NULLS NOT DISTINCT must refuse a second NULL.");

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            Assert.AreEqual(expected, await ScalarTextAsync(secondConnection, Query));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);

            // A changed setting is made, one way and the other.
            WriteSchema(projectDirectory, Objects
                .Replace("c integer UNIQUE NULLS NOT DISTINCT", "c integer UNIQUE", StringComparison.Ordinal)
                .Replace("CREATE UNIQUE INDEX t_a ON public.t (a) NULLS NOT DISTINCT", "CREATE UNIQUE INDEX t_a ON public.t (a)", StringComparison.Ordinal)
                .Replace("CREATE UNIQUE INDEX t_d ON public.t (d)", "CREATE UNIQUE INDEX t_d ON public.t (d) NULLS NOT DISTINCT", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("t_a:false,t_b_uq:true,t_c_key:false,t_d:true", await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    private static async Task<bool> TryExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        try
        {
            await command.ExecuteNonQueryAsync();
            return true;
        }
        catch (Npgsql.PostgresException)
        {
            return false;
        }
    }
}
