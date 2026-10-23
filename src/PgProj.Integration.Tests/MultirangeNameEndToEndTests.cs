using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-204 against a real server. A range type's <c>MULTIRANGE_TYPE_NAME</c> was left out, so the multirange got PostgreSQL's default name and a table
/// using the declared name failed to publish. It is now deployed as written, read back only when it is not the default, compared by its effective
/// name, and a table using a multirange is created after its range.
/// </summary>
[TestClass]
public sealed class MultirangeNameEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA s;
        CREATE TYPE public.floatrange AS RANGE (subtype = float8, subtype_diff = float8mi, multirange_type_name = floatmulti);
        CREATE TYPE s."Spans" AS RANGE (subtype = int4, multirange_type_name = s."Many Spans");
        CREATE TYPE public.fr AS RANGE (subtype = float8);
        CREATE TYPE public.pt AS RANGE (subtype = date, multirange_type_name = pt_multirange);
        CREATE TABLE public.t (r public.floatrange, m public.floatmulti, x s."Many Spans", y public.fr_multirange);
        """;

    [TestMethod]
    public async Task MultirangeNamesAreDeployedAndConverge()
    {
        var databaseName = await CreateDatabaseAsync("g204");
        var projectDirectory = CreateProjectDirectory("g204");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g204-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(
                "public.floatmulti public.fr_multirange public.pt_multirange s.\"Many Spans\"",
                await ScalarTextAsync(connectionString, "SELECT string_agg(typnamespace::regnamespace || '.' || quote_ident(typname), ' ' ORDER BY typnamespace::regnamespace::text, typname) FROM pg_type WHERE typtype = 'm' AND typnamespace::regnamespace::text IN ('public', 's');"));
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
