using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-184 against a real server. A domain over another domain has its CHECK stored with <c>VALUE</c> cast to the root type (<c>VALUE::int4</c>), which the
/// project's text does not have, so <c>diff</c> listed the domain as modified after every clean publish.
/// </summary>
[TestClass]
public sealed class DomainOverDomainEndToEndTests
{
    private const string Sql = """
        CREATE DOMAIN public.posint AS int CHECK (VALUE > 0);
        CREATE DOMAIN public.smallpos AS public.posint CHECK (VALUE < 100);
        CREATE DOMAIN public.tiny AS public.smallpos NOT NULL CHECK (VALUE <> 13);
        CREATE TABLE public.d (id int, a public.smallpos, t public.tiny);
        """;

    [TestMethod]
    public async Task ADomainOverADomainConverges_AndARealChangeIsStillSeen()
    {
        var databaseName = await CreateDatabaseAsync("g184");
        var projectDirectory = CreateProjectDirectory("g184");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g184-init-" + Guid.NewGuid().ToString("N"));
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

            // A different limit is a change, and is deployed.
            WriteSchema(projectDirectory, Edit(Sql, "VALUE < 100", "VALUE < 50"));
            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diffed.Output, "smallpos");
            Assert.Contains("Modified: 1", diffed.Output, StringComparison.Ordinal);
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT string_agg(pg_get_constraintdef(oid), ' ') FROM pg_constraint WHERE contypid = 'public.smallpos'::regtype"), "50");
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
