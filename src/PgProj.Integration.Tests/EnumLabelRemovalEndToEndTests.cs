using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-190 against a real server. Removing or reordering an enum label of a type a column uses used to fail midway with <c>2BP01</c>; <c>publish --execute</c> now
/// refuses it before running anything, naming the type and the table, and leaves the database as it was.
/// </summary>
[TestClass]
public sealed class EnumLabelRemovalEndToEndTests
{
    private const string Sql = """
        CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');
        CREATE TABLE public.m (id int, cur public.mood);
        """;

    [TestMethod]
    public async Task ARemovedLabelOfATypeInUseIsRefusedBeforeAnythingRuns()
    {
        var databaseName = await CreateDatabaseAsync("g190");
        var projectDirectory = CreateProjectDirectory("g190");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);

            // The extra column is created in the same publish; nothing of it may be applied when the label change is refused.
            WriteSchema(projectDirectory, Edit(Sql, "('sad', 'ok', 'happy')", "('sad', 'happy')").Replace("cur public.mood", "cur public.mood, extra int", StringComparison.Ordinal));
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "unsupported placeholder");
            StringAssert.Contains(refused.Output, "public.mood");
            Assert.AreEqual("sad,ok,happy", await ScalarTextAsync(connectionString, "SELECT string_agg(enumlabel, ',' ORDER BY enumsortorder) FROM pg_enum WHERE enumtypid = 'public.mood'::regtype"));
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'm' AND column_name = 'extra'"));

            // The script is still available for review and says what to do.
            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(dryRun.Output, "-- TODO: The enum type public.mood loses or reorders a label");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
