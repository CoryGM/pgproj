using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-242 against a real server. A table with a serial column and another owner than the publishing role made <c>ALTER SEQUENCE ... OWNED BY</c> fail ("sequence must have same
/// owner as table it is linked to"), because the table's owner was changed first. The sequence is now handed to its column before any owner statement.
/// </summary>
[TestClass]
public sealed class SequenceOwnerOrderEndToEndTests
{
    [TestMethod]
    public async Task ATableWithASerialColumnAndAnotherOwnerIsPublishedAndScaffolded()
    {
        var role = "g242_" + Guid.NewGuid().ToString("N")[..12];
        var databaseName = await CreateDatabaseAsync("g242");
        var projectDirectory = CreateProjectDirectory("g242");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g242-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g242b");
        var adminConnection = ConnectionStringFor(databaseName);
        try
        {
            await ExecuteAsync(adminConnection, $"CREATE ROLE {role};");
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, $"""
                CREATE SEQUENCE public.deptest_f1_seq OWNED BY public.deptest.f1;
                CREATE TABLE public.deptest (f1 integer DEFAULT nextval('public.deptest_f1_seq'::regclass) NOT NULL, f2 text);
                ALTER TABLE public.deptest OWNER TO {role};
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", adminConnection, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual($"{role}|{role}", await ScalarTextAsync(adminConnection, "SELECT pg_get_userbyid(t.relowner) || '|' || pg_get_userbyid(s.relowner) FROM pg_class t, pg_class s WHERE t.oid = 'public.deptest'::regclass AND s.oid = 'public.deptest_f1_seq'::regclass"));
            await AssertDiffIsEmptyAsync(projectFile, adminConnection);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", adminConnection);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(ConnectionStringFor("postgres"), $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
