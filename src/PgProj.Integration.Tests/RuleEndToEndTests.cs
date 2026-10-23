using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-128 against a real server, on every version: rules (an INSTEAD rule that makes a view writable, an ALSO rule with a condition that logs, and INSTEAD NOTHING)
/// deploy after the relation they are on whatever order the file has, read back from <c>pg_rewrite</c>, converge <c>0/0/0</c> although PostgreSQL rewrites each one
/// when it stores it (they are compared by <c>pg_get_ruledef</c>, the project's rendered by the server), behave, round-trip through <c>init</c> into the file of
/// their table or view, are dropped and created again when they change, and are not dropped by accident: removing one needs <c>--allow-destructive</c>, and a
/// project that lists <c>schemas</c> only manages the rules on relations in those schemas. A view's own <c>_RETURN</c> rule is not a rule.
/// </summary>
[TestClass]
public sealed class RuleEndToEndTests
{
    private const string Schema = """
        CREATE RULE v_ins AS ON INSERT TO public.v DO INSTEAD INSERT INTO public.t (id, n) VALUES (new.id, new.n);
        CREATE RULE t_log AS ON UPDATE TO public.t WHERE new.n <> old.n DO ALSO INSERT INTO public.audit VALUES (old.id, 'n changed');
        CREATE RULE t_keep AS ON DELETE TO public.t DO INSTEAD NOTHING;
        CREATE VIEW public.v AS SELECT id, n FROM public.t;
        CREATE TABLE public.audit (id int, note text);
        CREATE TABLE public.t (id int PRIMARY KEY, n int NOT NULL DEFAULT 0);
        """;

    private const string Expected = "t:t_keep:D:t|t:t_log:U:f|v:v_ins:I:t";

    [TestMethod]
    public async Task RulesDeployConvergeBehaveChangeRoundTripThroughInitAndAreNotDroppedByAccident()
    {
        var databaseName = await CreateDatabaseAsync("g128");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g128s");
        var projectDirectory = CreateProjectDirectory("g128");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(Expected, await Rules(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The rules work: the view takes an insert, an update that changes n is logged, and a delete does nothing.
            await ExecuteAsync(connectionString, "INSERT INTO public.v VALUES (1, 5); UPDATE public.t SET n = 6 WHERE id = 1; UPDATE public.t SET n = 6 WHERE id = 1; DELETE FROM public.t;");
            Assert.AreEqual("1|1|6", await ScalarTextAsync(connectionString, "SELECT (SELECT count(*) FROM public.t)::text || '|' || (SELECT count(*) FROM public.audit)::text || '|' || (SELECT n FROM public.t)::text;"));

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A rule written another way, with the qualifiers and parentheses PostgreSQL prints, is the same rule.
            WriteSchema(projectDirectory, Schema.Replace("WHERE new.n <> old.n", "WHERE (NEW.n <> OLD.n)", StringComparison.Ordinal).Replace("VALUES (old.id, 'n changed')", "VALUES (OLD.id, 'n changed'::text)", StringComparison.Ordinal));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            WriteSchema(projectDirectory, Schema);

            // Round trip through init: each rule is in the file of the relation it is on, and the scaffold deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            StringAssert.Contains(File.ReadAllText(Path.Combine(scaffoldDirectory, "Schema", "public", "View", "v.sql")), "CREATE RULE v_ins");
            StringAssert.Contains(File.ReadAllText(Path.Combine(scaffoldDirectory, "Schema", "public", "Table", "t.sql")), "CREATE RULE t_log");
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual(Expected, await Rules(scaffoldConnectionString));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // A changed condition is a drop and a create, and is not destructive.
            WriteSchema(projectDirectory, Schema.Replace("WHERE new.n <> old.n", "WHERE new.n > old.n", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT pg_get_ruledef(oid) FROM pg_rewrite WHERE rulename = 't_log';"), "new.n > old.n");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Removing one is destructive.
            WriteSchema(projectDirectory, Schema.ReplaceLineEndings("\n").Replace("CREATE RULE t_keep AS ON DELETE TO public.t DO INSTEAD NOTHING;\n", string.Empty, StringComparison.Ordinal).Replace("WHERE new.n <> old.n", "WHERE new.n > old.n", StringComparison.Ordinal));
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "rule dropped");
            StringAssert.Contains(await Rules(connectionString), "t_keep");

            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.AreEqual("t:t_log:U:f|v:v_ins:I:t", await Rules(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AProjectThatListsSchemasOnlyManagesTheRulesOnRelationsInThem()
    {
        var databaseName = await CreateDatabaseAsync("g128sc");
        var projectDirectory = CreateProjectDirectory("g128sc", ", \"schemas\": [\"app\"]");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, "CREATE TABLE public.other (id int); CREATE RULE other_keep AS ON DELETE TO public.other DO INSTEAD NOTHING;");
            WriteSchema(projectDirectory, """
                CREATE SCHEMA app;
                CREATE TABLE app.t (id int);
                CREATE RULE t_keep AS ON DELETE TO app.t DO INSTEAD NOTHING;
                """);

            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_rewrite WHERE oid >= 16384 AND rulename <> '_RETURN';"));
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ARuleThatCannotBeRenderedYetIsStillComparedByItsText()
    {
        var databaseName = await CreateDatabaseAsync("g128r");
        var projectDirectory = CreateProjectDirectory("g128r");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int);\nCREATE RULE t_keep AS ON DELETE TO public.t DO INSTEAD NOTHING;\n");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            // The new action uses a table that does not exist yet, so the server cannot render the rule now: it is a change, and publish creates the table first.
            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int);\nCREATE TABLE public.gone (id int);\nCREATE RULE t_keep AS ON DELETE TO public.t DO ALSO INSERT INTO public.gone VALUES (old.id);\n");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1); DELETE FROM public.t;");
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*) FROM public.gone;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> Rules(string connectionString)
        => ScalarTextAsync(
            connectionString,
            """
            SELECT string_agg(c.relname || ':' || r.rulename || ':' || CASE r.ev_type WHEN '1' THEN 'S' WHEN '2' THEN 'U' WHEN '3' THEN 'I' WHEN '4' THEN 'D' END
                              || ':' || (CASE WHEN r.is_instead THEN 't' ELSE 'f' END), '|' ORDER BY c.relname, r.rulename)
            FROM pg_rewrite r JOIN pg_class c ON c.oid = r.ev_class
            WHERE r.oid >= 16384 AND r.rulename <> '_RETURN';
            """);
}
