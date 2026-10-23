using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-207 step 2 against a real server. Expressions the parser used to rebuild from its tree are deployed as written, so these deploy and
/// converge: a routine parameter's default with an array cast, <c>ARRAY[...]</c>, <c>CASE</c>, <c>current_user</c>, or a typed literal (G-202,
/// which failed to deploy or re-diffed); an exclusion constraint's expression element (G-208, deployed as a plain column); an extended
/// statistics expression (G-209, dropped); and a publication's row filter with <c>CASE</c> (G-210, which failed to deploy).
/// </summary>
[TestClass]
public sealed class ExpressionAsWrittenEndToEndTests
{
    private const string Sql = """
        CREATE EXTENSION IF NOT EXISTS btree_gist;
        CREATE FUNCTION public.f(a text[] DEFAULT '{a,b}'::text[], b int[] DEFAULT ARRAY[1,2], c int DEFAULT CASE WHEN true THEN 1 ELSE 2 END,
            d text DEFAULT current_user, e int[] DEFAULT '{}', g interval DEFAULT interval '1 day') RETURNS int LANGUAGE sql
            SET search_path = pg_catalog, public AS $$ SELECT 1 $$;
        CREATE PROCEDURE public.p(a int[] DEFAULT '{}', b text DEFAULT current_user) LANGUAGE sql AS $$ SELECT 1 $$;
        CREATE TABLE public.t (a int, b text, r int4range, EXCLUDE USING gist ((CASE WHEN a > 0 THEN 1 ELSE 2 END) WITH =, r WITH &&));
        CREATE STATISTICS public.s ON (CASE WHEN a > 0 THEN 1 ELSE 2 END), b FROM public.t;
        CREATE PUBLICATION pb FOR TABLE public.t WHERE (CASE WHEN a > 0 THEN true ELSE false END AND b <> 'x');
        """;

    [TestMethod]
    [PostgreSqlVersion(Minimum = 15)]
    public async Task RebuiltExpressionsAreDeployedAsWrittenAndConverge()
    {
        var databaseName = await CreateDatabaseAsync("g207");
        var projectDirectory = CreateProjectDirectory("g207");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g207-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT pg_get_function_arguments('public.f'::regproc);"), "c integer DEFAULT CASE WHEN true THEN 1 ELSE 2 END, d text DEFAULT CURRENT_USER");
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE contype = 'x';"), "CASE");
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT pg_get_statisticsobjdef(oid) FROM pg_statistic_ext WHERE stxname = 's';"), "CASE");
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT pg_get_expr(prqual, prrelid) FROM pg_publication_rel;"), "CASE");
            Assert.AreEqual("search_path=pg_catalog, public", await ScalarTextAsync(connectionString, "SELECT array_to_string(proconfig, '|') FROM pg_proc WHERE proname = 'f';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
