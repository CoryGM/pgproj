using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-253 against a PostgreSQL 18 server: <c>NOT NULL ... NO INHERIT</c>, a child's own named NOT NULL on an inherited column, and a declared child column that gets its NOT NULL from a parent are
/// deployed, converge, survive <c>init</c>, and a toggle is made in place.
/// </summary>
[TestClass]
public sealed class NotNullNoInheritEndToEndTests
{
    private const string Objects = """
        CREATE TABLE public.p (f1 integer NOT NULL NO INHERIT, a integer, z integer NOT NULL);
        CREATE TABLE public.c () INHERITS (public.p);
        CREATE TABLE public.c2 (CONSTRAINT foo NOT NULL a, f1 integer, b integer CONSTRAINT nn NOT NULL) INHERITS (public.p);
        CREATE TABLE public.c3 (CONSTRAINT foo3 NOT NULL a NO INHERIT) INHERITS (public.p);
        """;

    // table:column:attnotnull:constraint name:local:no inherit, for every NOT NULL of the project's tables
    private const string Query = "SELECT string_agg(c.relname || '.' || a.attname || ':' || a.attnotnull::text || ':' || coalesce(k.conname, '') || ':' || coalesce(k.conislocal::text, '') || ':' || coalesce(k.connoinherit::text, ''), ',' ORDER BY c.relname, a.attnum) "
        + "FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped LEFT JOIN pg_constraint k ON k.conrelid = c.oid AND k.contype = 'n' AND k.conkey[1] = a.attnum "
        + "WHERE c.relnamespace = 'public'::regnamespace AND c.relkind = 'r'";

    [TestMethod]
    [PostgreSqlVersion(Minimum = 18)]
    public async Task NotNullNoInheritAndAChildsOwnConstraintsPublishConvergeAndRoundTrip()
    {
        var databaseName = await CreateDatabaseAsync("g253");
        var projectDirectory = CreateProjectDirectory("g253");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g253-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g253b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);

            WriteSchema(projectDirectory, Objects);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            var state = await ScalarTextAsync(connectionString, Query);
            // p.f1 is NO INHERIT, so no child has it; c2.a and c3.a carry the child's own constraints; c2.f1 is declared nullable.
            StringAssert.Contains(state, "p.f1:true:p_f1_not_null:true:true");
            StringAssert.Contains(state, "c.f1:false::");
            StringAssert.Contains(state, "c2.a:true:foo:true:false");
            StringAssert.Contains(state, "c2.f1:false::");
            StringAssert.Contains(state, "c3.a:true:foo3:true:true");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            Assert.AreEqual(state, await ScalarTextAsync(secondConnection, Query));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);

            // Toggling NO INHERIT on the parent's own NOT NULL is made in place.
            WriteSchema(projectDirectory, Objects.Replace("f1 integer NOT NULL NO INHERIT", "f1 integer NOT NULL", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            StringAssert.Contains(await ScalarTextAsync(connectionString, Query), "p.f1:true:p_f1_not_null:true:false");
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
}
