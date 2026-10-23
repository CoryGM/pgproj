using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-274 against a real server. A rule on a view or table an extension owns (PostGIS's three on <c>geometry_columns</c>) was read as the
/// project's, because PostgreSQL does not record the rule itself as a member: `diff` reported it removed and `publish --allow-destructive`
/// dropped it. A policy or statistics object on an extension's table was read the same way. The test makes the same shape without PostGIS:
/// a table and a view are added to <c>hstore</c> with <c>ALTER EXTENSION ... ADD</c>, which leaves their rules, policy, and statistics
/// out of the extension.
/// </summary>
[TestClass]
public sealed class ExtensionRelationObjectsEndToEndTests
{
    [TestMethod]
    public async Task RulesPoliciesAndStatisticsOnAnExtensionsRelationsAreNotTheProjects()
    {
        var databaseName = await CreateDatabaseAsync("g274");
        var projectDirectory = CreateProjectDirectory("g274");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g274-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, """
                CREATE EXTENSION hstore;
                CREATE TABLE public.ext_t (id integer PRIMARY KEY, v text);
                CREATE VIEW public.ext_v AS SELECT id, v FROM public.ext_t;
                CREATE RULE ext_v_insert AS ON INSERT TO public.ext_v DO INSTEAD INSERT INTO public.ext_t VALUES (NEW.id, NEW.v);
                CREATE RULE ext_t_delete AS ON DELETE TO public.ext_t DO ALSO NOTHING;
                CREATE POLICY ext_t_all ON public.ext_t USING (true);
                CREATE STATISTICS public.ext_t_stats ON id, v FROM public.ext_t;
                ALTER EXTENSION hstore ADD TABLE public.ext_t;
                ALTER EXTENSION hstore ADD VIEW public.ext_v;
                CREATE TABLE public.own (id integer PRIMARY KEY);
                """);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, """
                SELECT count(*)::text FROM pg_depend
                WHERE deptype = 'e' AND classid IN ('pg_rewrite'::regclass, 'pg_policy'::regclass, 'pg_statistic_ext'::regclass)
                """), "the rules, policy, and statistics must not be members themselves, as PostGIS's rules are not");

            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE EXTENSION hstore;
                CREATE TABLE public.own (id integer PRIMARY KEY);
                """);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var script = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run", "--allow-destructive");
            Assert.AreEqual(0, script.ExitCode, script.Output);
            Assert.DoesNotContain("DROP RULE", script.Output);
            Assert.DoesNotContain("DROP POLICY", script.Output);
            Assert.DoesNotContain("DROP STATISTICS", script.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("2,1,1", await ScalarTextAsync(connectionString, """
                SELECT (SELECT count(*) FROM pg_rewrite WHERE rulename IN ('ext_v_insert', 'ext_t_delete'))::text || ','
                    || (SELECT count(*) FROM pg_policy WHERE polname = 'ext_t_all')::text || ','
                    || (SELECT count(*) FROM pg_statistic_ext WHERE stxname = 'ext_t_stats')::text
                """));

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldText = string.Join("\n", Directory.EnumerateFiles(scaffoldDirectory, "*.sql", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.DoesNotContain("ext_v_insert", scaffoldText);
            Assert.DoesNotContain("ext_t_delete", scaffoldText);
            Assert.DoesNotContain("ext_t_all", scaffoldText);
            Assert.DoesNotContain("ext_t_stats", scaffoldText);
            StringAssert.Contains(scaffoldText, "CREATE TABLE public.own");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
