using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-286 against a real server (approved by the user 2026-10-14). A domain over a quoted type name with a capital letter (<c>CREATE DOMAIN dm AS "Mytype"</c>) recorded
/// its base type in lower case, so <c>build</c> reported the type missing and a project, or an <c>init</c> scaffold, with such a domain did not build; a domain over
/// <c>"Name"</c> was taken for one over the built-in <c>name</c>. Such a project now builds, publishes, converges, and round-trips through <c>init</c>. The issue's
/// <c>CHECK (length(VALUE) &lt; 10)</c> on <c>short_name</c> was left out here because it did not converge on any domain; G-288 fixed that, and its own test
/// (<c>DomainCheckRenderingEndToEndTests</c>) covers this domain with the check.
/// </summary>
[TestClass]
public sealed class DomainQuotedBaseTypeEndToEndTests
{
    [TestMethod]
    public async Task ADomainOverAQuotedTypeBuildsPublishesConvergesAndIsScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g286");
        var projectDirectory = CreateProjectDirectory("g286");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g286-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g286b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE DOMAIN public."Mytype" AS int;
                CREATE DOMAIN public.dm AS "Mytype" CHECK (VALUE > 0);
                CREATE DOMAIN public."Name" AS character varying(50);
                CREATE DOMAIN public.short_name AS "Name";
                CREATE TABLE public.person (id int PRIMARY KEY, n public.short_name, m public.dm);
                """);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.DoesNotContain("missing type", built.Output, StringComparison.Ordinal);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(
                "dm:\"Mytype\",short_name:\"Name\"",
                await ScalarTextAsync(connectionString, """
                    SELECT string_agg(t.typname || ':' || format_type(t.typbasetype, NULL), ',' ORDER BY t.typname)
                    FROM pg_type t WHERE t.typname IN ('dm', 'short_name')
                    """));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldBuilt = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, scaffoldBuilt.ExitCode, "the scaffold does not build.\n" + scaffoldBuilt.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var scaffoldPublished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, scaffoldPublished.ExitCode, "publishing the scaffold failed.\n" + scaffoldPublished.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
            await AssertDiffIsEmptyAsync(projectFile, secondConnection);
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
