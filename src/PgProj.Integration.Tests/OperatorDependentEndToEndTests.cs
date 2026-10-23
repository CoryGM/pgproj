using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-138 against a real server, on every version: what uses a project's operator (a column default, a CHECK, an index predicate, a view, a domain CHECK, a
/// <c>BEGIN ATOMIC</c> function) is written before the operator in the file and still deployed after it; and when the operator has to be dropped and created
/// again, PostgreSQL's own record of what uses it (<c>pg_depend</c>) says what to take off first and put back after: before this, the drop failed with
/// <c>2BP01</c>. The same holds one level down, when the function an operator runs changes its return type.
/// </summary>
[TestClass]
public sealed class OperatorDependentEndToEndTests
{
    private const string Users = """
        CREATE SCHEMA s;
        CREATE TABLE s.c (id int PRIMARY KEY, code int NOT NULL DEFAULT (CASE WHEN 1 OPERATOR(s.===) 1 THEN 1 ELSE 0 END), n int, CONSTRAINT c_ck CHECK (n OPERATOR(s.===) n));
        CREATE INDEX c_idx ON s.c (n) WHERE n OPERATOR(s.===) 1;
        CREATE VIEW s.v AS SELECT id, (n OPERATOR(s.===) 1) AS one FROM s.c;
        CREATE VIEW s.w AS SELECT id, (n OPERATOR(s.+#+) 1) AS bumped FROM s.c;
        CREATE DOMAIN s.pos AS int CHECK (VALUE OPERATOR(s.===) VALUE);
        CREATE FUNCTION s.uses(x int) RETURNS boolean LANGUAGE sql BEGIN ATOMIC SELECT x OPERATOR(s.===) 1; END;
        """;

    private static string Definitions(string equalExtra = "", string addReturns = "int", string addBody = "SELECT a + b")
        => $"""
            CREATE FUNCTION s.same(a int, b int) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$ SELECT a = b $$;
            CREATE FUNCTION s.add2(a int, b int) RETURNS {addReturns} LANGUAGE sql IMMUTABLE AS $$ {addBody} $$;
            CREATE OPERATOR s.=== (LEFTARG = int, RIGHTARG = int, FUNCTION = s.same{equalExtra});
            CREATE OPERATOR s.+#+ (LEFTARG = int, RIGHTARG = int, FUNCTION = s.add2);
            """;

    // The users come first in the file, on purpose: the order the objects are written in must not matter.
    private static string Project(string definitions) => "CREATE SCHEMA IF NOT EXISTS s;\n" + Users.Replace("CREATE SCHEMA s;", string.Empty, StringComparison.Ordinal) + "\n" + definitions;

    [TestMethod]
    public async Task WhatUsesAnOperatorIsDeployedAfterItAndAChangedOperatorIsRebuiltAroundItsUsers()
    {
        var databaseName = await CreateDatabaseAsync("g138");
        var projectDirectory = CreateProjectDirectory("g138");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Project(Definitions()));

            // Before this, the table, the view and the function were created first and PostgreSQL refused the script.
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            await ExecuteAsync(connectionString, "INSERT INTO s.c (id, n) VALUES (1, 5);");
            Assert.AreEqual("false|6", await ScalarTextAsync(connectionString, "SELECT (SELECT one::text FROM s.v) || '|' || (SELECT bumped::text FROM s.w);"));

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A changed operator is dropped and created again, and everything that PostgreSQL records as using it is taken off first and put back after.
            WriteSchema(projectDirectory, Project(Definitions(", HASHES")));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual("true", await ScalarTextAsync(connectionString, "SELECT oprcanhash::text FROM pg_operator WHERE oprname = '===' AND oprnamespace = 's'::regnamespace;"));
            Assert.AreEqual("false|6", await ScalarTextAsync(connectionString, "SELECT (SELECT one::text FROM s.v) || '|' || (SELECT bumped::text FROM s.w);"));
            Assert.AreEqual(
                "c_ck:true,c_pkey:true,pos_check:true",
                await ScalarTextAsync(connectionString, "SELECT string_agg(conname || ':' || convalidated::text, ',' ORDER BY conname) FROM pg_constraint WHERE (conrelid = 's.c'::regclass OR contypid = 's.pos'::regtype) AND contype <> 'n';"));
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_indexes WHERE schemaname = 's' AND indexname IN ('c_idx', 'c_pkey');"));
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_attrdef WHERE adrelid = 's.c'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ARoutineAnOperatorRunsCanChangeItsReturnTypeAndTheOperatorAndItsUsersComeBack()
    {
        var databaseName = await CreateDatabaseAsync("g138f");
        var projectDirectory = CreateProjectDirectory("g138f");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Project(Definitions()));
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            await ExecuteAsync(connectionString, "INSERT INTO s.c (id, n) VALUES (1, 5);");

            // The function under the operator returns bigint now: DROP FUNCTION needs the operator, and the view on it, out of the way first.
            WriteSchema(projectDirectory, Project(Definitions(addReturns: "bigint", addBody: "SELECT (a + b)::bigint")));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual("bigint|6", await ScalarTextAsync(connectionString, "SELECT pg_get_function_result('s.add2(int,int)'::regprocedure) || '|' || (SELECT bumped::text FROM s.w);"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
