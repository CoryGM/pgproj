using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-241 against a real server. Base types are not modeled (the user's decision of 2026-10-13; modeling them is G-267). <c>init</c> wrote a base type's input and output
/// functions, whose <c>CREATE FUNCTION</c> made a shell type, so publishing the scaffold failed with <c>type ... is only a shell</c>; a project written as <c>pg_dump</c>
/// writes one refused to build (<c>Duplicate Type definition</c>). Now <c>init</c> leaves the type and its functions out and names it, the live read leaves the functions out,
/// and a project's base type is reported and ignored with its functions; with the type created beforehand (as a superuser would), what uses it publishes and converges.
/// </summary>
[TestClass]
public sealed class BaseTypeEndToEndTests
{
    /// <summary>A base type as <c>pg_dump</c> writes one: a shell, the internal functions, the full definition; then a cast and a column that use it.</summary>
    private const string BaseTypeSql = """
        CREATE TYPE public.xint4;
        CREATE FUNCTION public.xint4in(cstring) RETURNS public.xint4 LANGUAGE internal IMMUTABLE STRICT AS $$int4in$$;
        CREATE FUNCTION public.xint4out(public.xint4) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS $$int4out$$;
        CREATE TYPE public.xint4 (INTERNALLENGTH = 4, INPUT = public.xint4in, OUTPUT = public.xint4out, ALIGNMENT = int4, STORAGE = plain, PASSEDBYVALUE);
        """;

    private const string UsesSql = """
        CREATE CAST (integer AS public.xint4) WITHOUT FUNCTION;
        CREATE TABLE public.t (id integer PRIMARY KEY, v public.xint4);
        CREATE FUNCTION public.plain() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
        """;

    [TestMethod]
    public async Task InitLeavesABaseTypeAndItsFunctionsOutAndTheRestConverges()
    {
        var databaseName = await CreateDatabaseAsync("g241");
        var secondDatabase = await CreateDatabaseAsync("g241b");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g241-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, BaseTypeSql + UsesSql);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString, "--report");
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            StringAssert.Contains(scaffolded.Output, "Left out a base type, with its input and output functions: public.xint4.");

            var sql = string.Join("\n", Directory.GetFiles(Path.Combine(scaffoldDirectory, "sql"), "*.sql", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.IsFalse(sql.Contains("xint4in", StringComparison.Ordinal), "The input function is left out with the type.\n" + sql);
            Assert.IsFalse(sql.Contains("xint4out", StringComparison.Ordinal), "The output function is left out with the type.\n" + sql);
            StringAssert.Contains(sql, "FUNCTION public.plain()");
            StringAssert.Contains(sql, "xint4");

            var report = File.ReadAllText(Directory.GetFiles(Path.Combine(scaffoldDirectory, "sql"), "init-report-*.md").Single());
            StringAssert.Contains(report, "| `public` | Base type | `xint4` | not supported |");
            Assert.IsFalse(report.Contains("`_xint4`", StringComparison.Ordinal), "The array type PostgreSQL made for the base type is not listed.\n" + report);

            // The scaffold against the database it came from: the type's functions are not read either, so nothing is proposed for dropping.
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // Into a second database, with the type created first (as a superuser would, outside pgproj): the cast and the column publish and converge.
            var secondConnection = ConnectionStringFor(secondDatabase);
            await ExecuteAsync(secondConnection, BaseTypeSql);
            var published = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publishing the scaffold failed.\n" + published.Output);
            Assert.AreEqual("xint4", await ScalarTextAsync(secondConnection, "SELECT atttypid::regtype::text FROM pg_attribute WHERE attrelid = 'public.t'::regclass AND attname = 'v'"));
            Assert.AreEqual("1", await ScalarTextAsync(secondConnection, "SELECT count(*)::text FROM pg_cast WHERE casttarget = 'public.xint4'::regtype"));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    [TestMethod]
    public async Task AProjectWrittenAsPgDumpWritesABaseTypeBuildsAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g241p");
        var projectDirectory = CreateProjectDirectory("g241p");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, BaseTypeSql + "ALTER FUNCTION public.xint4in(cstring) OWNER TO postgres;\nALTER TYPE public.xint4 OWNER TO postgres;\n" + UsesSql);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("Duplicate Type definition", StringComparison.Ordinal), built.Output);
            StringAssert.Contains(built.Output, "CREATE TYPE (shell type) is not modeled");
            StringAssert.Contains(built.Output, "CREATE TYPE (base type) is not modeled");
            StringAssert.Contains(built.Output, "Base type public.xint4 is not modeled, so the functions it names are left out with it: public.xint4in, public.xint4out.");

            // The type is the database's to create; with it there, what the project states publishes, and the type's functions are neither created nor dropped.
            await ExecuteAsync(connectionString, BaseTypeSql);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_proc WHERE proname IN ('xint4in', 'xint4out')"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
