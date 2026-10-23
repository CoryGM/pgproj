using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-139 against a real server, on every version: PostgreSQL refuses to retype a column while a rule uses it (<c>0A000: cannot alter type of a column used by a view
/// or rule</c>), whether the rule is on the column's own table, on another table, or on a view. The rules the database records as using the column are dropped before
/// the alter and created again after it, from the project, as views are (G-103); a view that has to be dropped and created again takes its rules with it, so they
/// are created again too.
/// </summary>
[TestClass]
public sealed class RuleColumnChangeEndToEndTests
{
    private static string Project(string nType, string logType)
        => $"""
            CREATE TABLE public.t (id int PRIMARY KEY, n {nType});
            CREATE TABLE public.log (id {logType}, note text);
            CREATE VIEW public.v AS SELECT id, n FROM public.t;
            CREATE RULE t_log AS ON UPDATE TO public.t WHERE new.n <> old.n DO ALSO INSERT INTO public.log (id, note) VALUES (old.id, 'n ' || old.n);
            CREATE RULE v_nodelete AS ON DELETE TO public.v DO INSTEAD NOTHING;
            """;

    [TestMethod]
    public async Task ARuleOnTheTableThatUsesTheColumnIsTakenOffAndPutBack()
    {
        await WithProjectAsync("g139", async (connectionString, projectFile, directory) =>
        {
            WriteSchema(directory, Project("int", "int"));
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1, 5);");

            // n becomes bigint: the rule on t uses old.n and new.n, so PostgreSQL refuses the alter while it exists.
            WriteSchema(directory, Project("bigint", "int"));
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);

            Assert.AreEqual("bigint|2", await ScalarTextAsync(connectionString, "SELECT format_type(atttypid, null) || '|' || (SELECT count(*) FROM pg_rewrite WHERE oid >= 16384 AND rulename <> '_RETURN')::text FROM pg_attribute WHERE attrelid = 'public.t'::regclass AND attname = 'n';"));
            await ExecuteAsync(connectionString, "UPDATE public.t SET n = 6 WHERE id = 1;");
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*) FROM public.log;"), "The rule works again.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task ARuleOnAnotherTableThatWritesTheColumnIsTakenOffAndPutBack()
    {
        await WithProjectAsync("g139o", async (connectionString, projectFile, directory) =>
        {
            WriteSchema(directory, Project("int", "int"));
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            // log.id becomes bigint: the rule on t inserts into it, so it uses that column although it is on another table.
            WriteSchema(directory, Project("int", "bigint"));
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);

            Assert.AreEqual("bigint", await ScalarTextAsync(connectionString, "SELECT format_type(atttypid, null) FROM pg_attribute WHERE attrelid = 'public.log'::regclass AND attname = 'id';"));
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_rewrite WHERE oid >= 16384 AND rulename <> '_RETURN';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task ARuleOnAViewThatIsDroppedAndCreatedAgainIsCreatedAgainToo()
    {
        await WithProjectAsync("g139v", async (connectionString, projectFile, directory) =>
        {
            WriteSchema(directory, Project("int", "int"));
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            // v_nodelete uses nothing of t, but the view it is on has to be dropped for the alter, and a view's rules go with it.
            WriteSchema(directory, Project("bigint", "int"));
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);

            Assert.AreEqual("v_nodelete", await ScalarTextAsync(connectionString, "SELECT string_agg(rulename, ',' ORDER BY rulename) FROM pg_rewrite WHERE ev_class = 'public.v'::regclass AND rulename <> '_RETURN';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task ARuleThatCallsAFunctionThatIsDroppedAndCreatedAgainIsTakenOffAndPutBack()
    {
        await WithProjectAsync("g139f", async (connectionString, projectFile, directory) =>
        {
            static string Users(string returns, string body) => $"""
                CREATE FUNCTION public.next_code(x int) RETURNS {returns} LANGUAGE sql IMMUTABLE AS $$ {body} $$;
                CREATE TABLE public.t (id int);
                CREATE TABLE public.log (code bigint);
                CREATE RULE t_ins AS ON INSERT TO public.t DO ALSO INSERT INTO public.log VALUES (public.next_code(new.id));
                """;
            WriteSchema(directory, Users("int", "SELECT x"));
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            // DROP FUNCTION is refused while the rule calls it; the rule comes off first and goes back after the new function is created.
            WriteSchema(directory, Users("bigint", "SELECT x::bigint"));
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);

            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (7);");
            Assert.AreEqual("bigint|7", await ScalarTextAsync(connectionString, "SELECT pg_get_function_result('public.next_code(int)'::regprocedure) || '|' || (SELECT code FROM public.log)::text;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    private static async Task WithProjectAsync(string name, Func<string, string, string, Task> test)
    {
        var databaseName = await CreateDatabaseAsync(name);
        var projectDirectory = CreateProjectDirectory(name);
        try
        {
            await test(ConnectionStringFor(databaseName), ProjectFile(projectDirectory), projectDirectory);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
