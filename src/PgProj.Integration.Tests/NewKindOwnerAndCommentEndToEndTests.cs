using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-154 against a real server, on every version: the owner and the comment of a collation, an extended statistics object, a text search dictionary, and a text search
/// configuration deploy after the object, read back, converge <c>0/0/0</c>, round-trip through <c>init</c>, and change safely. A comment-only change is a <c>COMMENT ON</c> (it
/// neither drops a collation that a column uses nor is refused for it), and a changed owner needs <c>--allow-destructive</c>. Roles are cluster-wide, so the test names its own
/// and drops them last.
/// </summary>
[TestClass]
public sealed class NewKindOwnerAndCommentEndToEndTests
{
    private static string Schema(string owner, string collationComment = "a collation", string statisticsComment = "some stats")
        => $"""
            CREATE SCHEMA app;
            CREATE TABLE app.t (id int PRIMARY KEY, a int, b int, name text COLLATE app.ci);
            CREATE COLLATION app.ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
            COMMENT ON COLLATION app.ci IS '{collationComment}';
            ALTER COLLATION app.ci OWNER TO {owner};
            CREATE STATISTICS app.st ON a, b FROM app.t;
            COMMENT ON STATISTICS app.st IS '{statisticsComment}';
            ALTER STATISTICS app.st OWNER TO {owner};
            CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = simple);
            COMMENT ON TEXT SEARCH DICTIONARY app.d IS 'it''s a dictionary';
            ALTER TEXT SEARCH DICTIONARY app.d OWNER TO {owner};
            CREATE TEXT SEARCH CONFIGURATION app.c (PARSER = pg_catalog."default");
            ALTER TEXT SEARCH CONFIGURATION app.c ADD MAPPING FOR asciiword WITH app.d;
            COMMENT ON TEXT SEARCH CONFIGURATION app.c IS 'a configuration';
            ALTER TEXT SEARCH CONFIGURATION app.c OWNER TO {owner};
            """;

    private const string Query = """
        SELECT concat_ws('|',
            (SELECT pg_get_userbyid(collowner) || ':' || coalesce(obj_description(oid, 'pg_collation'), '-') FROM pg_collation WHERE collname = 'ci'),
            (SELECT pg_get_userbyid(stxowner) || ':' || coalesce(obj_description(oid, 'pg_statistic_ext'), '-') FROM pg_statistic_ext WHERE stxname = 'st'),
            (SELECT pg_get_userbyid(dictowner) || ':' || coalesce(obj_description(oid, 'pg_ts_dict'), '-') FROM pg_ts_dict WHERE dictname = 'd'),
            (SELECT pg_get_userbyid(cfgowner) || ':' || coalesce(obj_description(oid, 'pg_ts_config'), '-') FROM pg_ts_config WHERE cfgname = 'c'));
        """;

    [TestMethod]
    public async Task OwnersAndCommentsDeployConvergeRoundTripThroughInitAndChangeSafely()
    {
        var owner = $"g154_a_{Guid.NewGuid():N}"[..24];
        var other = $"g154_b_{Guid.NewGuid():N}"[..24];
        var databaseName = await CreateDatabaseAsync("g154");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g154s");
        var projectDirectory = CreateProjectDirectory("g154");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {owner} NOLOGIN; CREATE ROLE {other} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema(owner));

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("[Warning]", StringComparison.Ordinal), built.Output);
            Assert.IsFalse(built.Output.Contains("not modeled", StringComparison.Ordinal), built.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual($"{owner}:a collation|{owner}:some stats|{owner}:it's a dictionary|{owner}:a configuration", await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.IsFalse(again.Output.Contains("OWNER TO \"", StringComparison.Ordinal) || again.Output.Contains("COMMENT ON", StringComparison.Ordinal), again.Output);

            // Round trip through init: each owner and comment is written beside its object, and the scaffold deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var collationFile = File.ReadAllText(Path.Combine(scaffoldDirectory, "Schema", "app", "Collation", "ci.sql"));
            StringAssert.Contains(collationFile, "COMMENT ON COLLATION");
            StringAssert.Contains(collationFile, "OWNER TO");
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual(await ScalarTextAsync(connectionString, Query), await ScalarTextAsync(scaffoldConnectionString, Query));

            // A comment-only change is a COMMENT ON, even on a collation a column uses.
            var collationOid = await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_collation WHERE collname = 'ci';");
            WriteSchema(projectDirectory, Schema(owner, collationComment: "changed", statisticsComment: "changed too"));
            var commented = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, commented.ExitCode, commented.Output);
            Assert.AreEqual($"{owner}:changed|{owner}:changed too|{owner}:it's a dictionary|{owner}:a configuration", await ScalarTextAsync(connectionString, Query));
            Assert.AreEqual(collationOid, await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_collation WHERE collname = 'ci';"), "Commented in place, not dropped and created again.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A changed owner needs --allow-destructive.
            WriteSchema(projectDirectory, Schema(other, collationComment: "changed", statisticsComment: "changed too"));
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "--allow-destructive");
            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.AreEqual($"{other}:changed|{other}:changed too|{other}:it's a dictionary|{other}:a configuration", await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {owner}; DROP ROLE IF EXISTS {other};");
            TryDeleteDirectory(projectDirectory);
        }
    }
}
