using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-292 against a real server. An aggregate written with a named argument (<c>VARIADIC items anycompatiblearray</c>) was deployed and scaffolded without the name,
/// which is lost from <c>pg_proc.proargnames</c>, <c>\df</c>, and the documentation.
/// </summary>
[TestClass]
public sealed class AggregateArgumentNameEndToEndTests
{
    private const string Schema = """
        CREATE FUNCTION public.cleast_accum(anycompatible, VARIADIC anycompatiblearray) RETURNS anycompatible LANGUAGE sql AS 'SELECT least($1, min(v)) FROM unnest($2) v';
        CREATE AGGREGATE public.cleast_agg(VARIADIC items anycompatiblearray) (SFUNC = public.cleast_accum, STYPE = anycompatible);
        CREATE FUNCTION public.plain_acc(state int, v int) RETURNS int LANGUAGE sql AS 'SELECT state + v';
        CREATE AGGREGATE public.total(amount int) (SFUNC = public.plain_acc, STYPE = int);
        CREATE AGGREGATE public.unnamed_total(int) (SFUNC = public.plain_acc, STYPE = int);
        """;

    private const string NamesQuery = """
        SELECT string_agg(p.proname || ':' || coalesce(p.proargnames::text, '-'), '|' ORDER BY p.proname)
        FROM pg_proc p WHERE p.prokind = 'a' AND p.pronamespace = 'public'::regnamespace;
        """;

    [TestMethod]
    public async Task AnAggregatesArgumentNamesAreDeployedKeptByInitAndConverge()
    {
        var databaseName = await CreateDatabaseAsync("g292");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g292s");
        var projectDirectory = CreateProjectDirectory("g292");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            var names = await ScalarTextAsync(connectionString, NamesQuery);
            Assert.AreEqual("cleast_agg:{items}|total:{amount}|unnamed_total:-", names);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", scaffoldProject);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual(names, await ScalarTextAsync(scaffoldConnectionString, NamesQuery));
            await AssertDiffIsEmptyAsync(scaffoldProject, scaffoldConnectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
