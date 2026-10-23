using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-197 against a real server. A <c>SECURITY DEFINER</c> function's <c>SET search_path</c> was not deployed, so it ran with the caller's
/// <c>search_path</c>, and <c>COST</c>, <c>ROWS</c>, <c>PARALLEL</c>, and <c>LEAKPROOF</c> were dropped too, with nothing reported. They are now deployed,
/// read back from <c>pg_get_functiondef</c>, compared, scaffolded by <c>init</c>, and changed in place with <c>CREATE OR REPLACE</c>.
/// </summary>
[TestClass]
public sealed class RoutineOptionsEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA "My S";
        create function public.f(a int, b int default 5) returns int language sql immutable leakproof parallel safe cost 5
          security definer set search_path = pg_catalog, public as $$ select a + b $$;
        CREATE FUNCTION public.g() RETURNS SETOF int LANGUAGE plpgsql PARALLEL RESTRICTED COST 7.5 ROWS 42
          SET work_mem = '64MB' SET search_path = "My S", "$user", public SET statement_timeout = 5000 AS $$ BEGIN RETURN NEXT 1; END $$;
        CREATE FUNCTION public.h() RETURNS int LANGUAGE sql SET myapp.tenant = 'x' SET enable_seqscan = off SET work_mem FROM CURRENT AS $$ SELECT 1 $$;
        CREATE PROCEDURE public.p() LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog AS $$ SELECT 1 $$;
        """;

    private const string Options = """
        SELECT string_agg(proname || ':' || prosecdef::text || ',' || proleakproof::text || ',' || proparallel::text || ',' || procost::text || ',' || prorows::text || ','
            || coalesce(array_to_string(proconfig, '|'), ''), ' ' ORDER BY proname)
        FROM pg_proc WHERE pronamespace = 'public'::regnamespace;
        """;

    [TestMethod]
    public async Task RoutineOptionsAreDeployedAndConverge_AndChangesAreMadeInPlace()
    {
        var databaseName = await CreateDatabaseAsync("g197");
        var projectDirectory = CreateProjectDirectory("g197");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g197-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            var deployed = await ScalarTextAsync(connectionString, Options);
            StringAssert.Contains(deployed, "f:true,true,s,5,0,search_path=pg_catalog, public ");
            StringAssert.Contains(deployed, "g:false,false,r,7.5,42,work_mem=64MB|search_path=\"My S\", \"$user\", public|statement_timeout=5000 ");
            StringAssert.Contains(deployed, "h:false,false,u,100,0,myapp.tenant=x|enable_seqscan=off|work_mem=");
            StringAssert.EndsWith(deployed, "p:true,false,u,100,0,search_path=pg_catalog");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            const string Oids = "SELECT string_agg(oid::text, ',' ORDER BY proname) FROM pg_proc WHERE pronamespace = 'public'::regnamespace;";
            var oids = await ScalarTextAsync(connectionString, Oids);

            // A pinned search_path removed, options changed, and settings dropped are each a CREATE OR REPLACE, which resets what is left out.
            var changedSql = Edit(Edit(Edit(Sql,
                "immutable leakproof parallel safe cost 5\n  security definer set search_path = pg_catalog, public",
                "immutable parallel restricted cost 9\n  security definer"),
                "PARALLEL RESTRICTED COST 7.5 ROWS 42\n  SET work_mem = '64MB' SET search_path = \"My S\", \"$user\", public SET statement_timeout = 5000",
                "ROWS 10 SET search_path = public"),
                "SECURITY DEFINER SET search_path = pg_catalog AS", "SECURITY DEFINER SET search_path = public AS");
            WriteSchema(projectDirectory, changedSql);
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual(oids, await ScalarTextAsync(connectionString, Oids), "The routines were dropped and recreated, not replaced.");
            var replaced = await ScalarTextAsync(connectionString, Options);
            StringAssert.Contains(replaced, "f:true,false,r,9,0, ");
            StringAssert.Contains(replaced, "g:false,false,u,100,10,search_path=public ");
            StringAssert.EndsWith(replaced, "p:true,false,u,100,0,search_path=public");
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
