using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-193 against a real server. A function declared <c>RETURNS TABLE</c> with more than one column returns <c>record</c>, which the project listed among the
/// function's referenced types and the database does not, so the function was modified after every publish.
/// </summary>
[TestClass]
public sealed class ReturnsTableFunctionEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.t (id int, name text);
        CREATE TYPE public.mood AS ENUM ('ok', 'sad');
        CREATE FUNCTION public.f(p int) RETURNS TABLE (id int, name text) LANGUAGE sql STABLE AS $$ SELECT id, name FROM public.t WHERE id = p $$;
        CREATE FUNCTION public.g() RETURNS TABLE (x int, m public.mood) LANGUAGE plpgsql AS $$ BEGIN RETURN QUERY SELECT 1, 'ok'::public.mood; END $$;
        CREATE FUNCTION public.h() RETURNS TABLE (x int) LANGUAGE sql AS $$ SELECT 1 $$;
        """;

    [TestMethod]
    public async Task FunctionsReturningATableConverge_AndAChangedParameterIsSeen()
    {
        var databaseName = await CreateDatabaseAsync("g193");
        var projectDirectory = CreateProjectDirectory("g193");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g193-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // A changed parameter is still seen, as a function of a new identity (a changed output column is G-198's).
            WriteSchema(projectDirectory, Edit(Sql, "public.f(p int)", "public.f(p bigint)"));
            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.Contains("+ Function public.f(int8)", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("- Function public.f(int4)", diffed.Output, StringComparison.Ordinal);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
