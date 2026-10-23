using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-207 step 3 against a real server. Types are deployed as the project wrote them and compared by their normalized names: a routine's
/// <c>"char"</c> stays the one-byte type (it was deployed as <c>character(1)</c> and re-diffed, G-201), a quoted schema and type name deploy
/// in a routine, a cast, an operator, and an aggregate (they failed, G-201), and a typed table with <c>float8</c> and <c>varchar</c> fields
/// converges (it re-diffed on every run, G-199).
/// </summary>
[TestClass]
public sealed class TypeAsWrittenEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA "My S";
        CREATE TYPE "My S"."My T" AS (a int);
        CREATE FUNCTION public.f1(a "char", b double precision, c "My S"."My T", d INT) RETURNS SETOF "char" LANGUAGE sql AS $$ SELECT a $$;
        CREATE FUNCTION public.f2() RETURNS "My S"."My T" LANGUAGE sql AS $$ SELECT ROW(1)::"My S"."My T" $$;
        CREATE TABLE public.t (a "My S"."My T", b "char", c INT, d VARCHAR(10));
        CREATE DOMAIN public.dm AS VARCHAR(10);
        CREATE FUNCTION public.eqf("My S"."My T", "My S"."My T") RETURNS bool LANGUAGE sql IMMUTABLE AS $$ SELECT true $$;
        CREATE OPERATOR public.=== (LEFTARG = "My S"."My T", RIGHTARG = "My S"."My T", FUNCTION = public.eqf);
        CREATE FUNCTION public.tocharf("My S"."My T") RETURNS "char" LANGUAGE sql IMMUTABLE AS $$ SELECT 'x'::"char" $$;
        CREATE CAST ("My S"."My T" AS "char") WITH FUNCTION public.tocharf("My S"."My T");
        CREATE FUNCTION public.accf(int, "char") RETURNS int LANGUAGE sql IMMUTABLE AS $$ SELECT $1 $$;
        CREATE AGGREGATE public.agg("char") (SFUNC = public.accf, STYPE = INTEGER, INITCOND = '0');
        CREATE TYPE public.pt AS (x float8, code varchar(10), n int);
        CREATE TABLE public.typed OF public.pt;
        """;

    [TestMethod]
    public async Task TypesAreDeployedAsWrittenAndConverge()
    {
        var databaseName = await CreateDatabaseAsync("g207t");
        var projectDirectory = CreateProjectDirectory("g207t");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g207t-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("a \"char\", b double precision, c \"My S\".\"My T\", d integer", await ScalarTextAsync(connectionString, "SELECT pg_get_function_arguments('public.f1'::regproc);"));
            Assert.AreEqual("SETOF \"char\"", await ScalarTextAsync(connectionString, "SELECT pg_get_function_result('public.f1'::regproc);"));
            Assert.AreEqual("\"My S\".\"My T\"->\"char\"", await ScalarTextAsync(connectionString, "SELECT castsource::regtype || '->' || casttarget::regtype FROM pg_cast WHERE castfunc = 'public.tocharf'::regproc;"));
            Assert.AreEqual("\"char\"", await ScalarTextAsync(connectionString, "SELECT pg_get_function_arguments('public.agg'::regproc);"));
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
