using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-200 against a real server. A primary key's, unique constraint's, or exclusion constraint's <c>INCLUDE (...)</c> was not deployed, with no
/// warning, and <c>diff</c> reported nothing. It is now deployed, read back, compared, scaffolded by <c>init</c>, and a changed list is deployed.
/// </summary>
[TestClass]
public sealed class ConstraintIncludeEndToEndTests
{
    private const string Sql = """
        CREATE EXTENSION IF NOT EXISTS btree_gist;
        CREATE TABLE public.t (id int, a int, "B" int, r int4range,
            CONSTRAINT t_pk PRIMARY KEY (id) INCLUDE (a) WITH (fillfactor = 70),
            CONSTRAINT t_u UNIQUE (a) INCLUDE ("B", id),
            CONSTRAINT t_x EXCLUDE USING gist (r WITH &&) INCLUDE (a));
        """;

    private const string Definitions = "SELECT string_agg(conname || ': ' || pg_get_constraintdef(oid), ' | ' ORDER BY conname) FROM pg_constraint WHERE conrelid = 'public.t'::regclass AND contype IN ('p', 'u', 'x');";

    [TestMethod]
    public async Task IncludeColumnsAreDeployedAndConverge_AndAChangeIsDeployed()
    {
        var databaseName = await CreateDatabaseAsync("g200");
        var projectDirectory = CreateProjectDirectory("g200");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g200-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("t_pk: PRIMARY KEY (id) INCLUDE (a) | t_u: UNIQUE (a) INCLUDE (\"B\", id) | t_x: EXCLUDE USING gist (r WITH &&) INCLUDE (a)", await ScalarTextAsync(connectionString, Definitions));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            WriteSchema(projectDirectory, Edit(Edit(Sql, "PRIMARY KEY (id) INCLUDE (a)", "PRIMARY KEY (id)"), "UNIQUE (a) INCLUDE (\"B\", id)", "UNIQUE (a) INCLUDE (id, \"B\")"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual("t_pk: PRIMARY KEY (id) | t_u: UNIQUE (a) INCLUDE (id, \"B\") | t_x: EXCLUDE USING gist (r WITH &&) INCLUDE (a)", await ScalarTextAsync(connectionString, Definitions));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
