using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-220 against a real server. A sequence's changed <c>START</c> was deployed as <c>RESTART WITH</c>, which moved the counter (so a key it feeds could be
/// handed out twice) and left the recorded start unchanged, so every later publish restarted it again. It is now <c>START WITH</c>: the start is recorded, the
/// counter keeps its place, and the project converges.
/// </summary>
[TestClass]
public sealed class SequenceStartEndToEndTests
{
    [TestMethod]
    public async Task AChangedStartIsRecordedWithoutMovingTheCounterAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g220");
        var projectDirectory = CreateProjectDirectory("g220");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE SEQUENCE public.s START 10;\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await ExecuteAsync(connectionString, "SELECT nextval('public.s'), nextval('public.s'), nextval('public.s');");

            WriteSchema(projectDirectory, "CREATE SEQUENCE public.s START 20;\n");
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, altered.ExitCode, "the second publish failed.\n" + altered.Output);
            Assert.AreEqual("20", await ScalarTextAsync(connectionString, "SELECT seqstart::text FROM pg_sequence WHERE seqrelid = 'public.s'::regclass;"));
            Assert.AreEqual("13", await ScalarTextAsync(connectionString, "SELECT nextval('public.s')::text;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
