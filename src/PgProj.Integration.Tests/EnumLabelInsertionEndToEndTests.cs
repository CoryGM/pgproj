using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-183 against a real server. A label added anywhere but the end of an enum was deployed as <c>DROP TYPE</c> and <c>CREATE TYPE</c>, which
/// PostgreSQL refuses (<c>2BP01</c>) once a column uses the type. It is now <c>ALTER TYPE ... ADD VALUE ... BEFORE | AFTER</c>, in place.
/// </summary>
[TestClass]
public sealed class EnumLabelInsertionEndToEndTests
{
    private const string Sql = """
        CREATE TYPE public.mood AS ENUM ('sad', 'happy');
        CREATE TABLE public.m (id int, cur public.mood);
        CREATE VIEW public.moods AS SELECT id, cur FROM public.m;
        """;

    [TestMethod]
    public async Task LabelsInsertedAnywhereAreAddedInPlace_AndTheDataIsKept()
    {
        var databaseName = await CreateDatabaseAsync("g183");
        var projectDirectory = CreateProjectDirectory("g183");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await ExecuteAsync(connectionString, "INSERT INTO public.m VALUES (1, 'happy');");

            // Between, before the first, and after the last, in one publish.
            WriteSchema(projectDirectory, Edit(Sql, "('sad', 'happy')", "('awful', 'sad', 'ok', 'happy', 'ecstatic')"));
            var scriptPath = Path.Combine(projectDirectory, "insert.sql");
            var inserted = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, inserted.ExitCode, inserted.Output);
            var script = await File.ReadAllTextAsync(scriptPath);
            Assert.DoesNotContain("DROP TYPE", script, StringComparison.Ordinal);
            StringAssert.Contains(script, "ADD VALUE 'awful' BEFORE 'sad'");
            Assert.AreEqual("awful,sad,ok,happy,ecstatic", await LabelsAsync(connectionString));
            Assert.AreEqual("happy", await ScalarTextAsync(connectionString, "SELECT cur::text FROM public.m;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> LabelsAsync(string connectionString)
        => ScalarTextAsync(connectionString, "SELECT string_agg(enumlabel, ',' ORDER BY enumsortorder) FROM pg_enum WHERE enumtypid = 'public.mood'::regtype;");
}
