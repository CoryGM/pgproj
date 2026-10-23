using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-283 against a real server. A sequence's <c>OWNED BY</c> written with an unqualified table (Synapse's full schema does) showed as modified after every publish, and every publish
/// ran the same <c>ALTER SEQUENCE</c> again.
/// </summary>
[TestClass]
public sealed class SequenceOwnedByUnqualifiedEndToEndTests
{
    private const string Schema = """
        CREATE TABLE instance_map (instance_id int NOT NULL);
        CREATE SEQUENCE instance_map_instance_id_seq;
        ALTER SEQUENCE instance_map_instance_id_seq OWNED BY instance_map.instance_id;
        """;

    [TestMethod]
    public async Task AnUnqualifiedOwningTableConvergesAndTheSecondPublishHasNothingToDo()
    {
        var databaseName = await CreateDatabaseAsync("g283");
        var projectDirectory = CreateProjectDirectory("g283");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("instance_map", await ScalarTextAsync(connectionString, "SELECT d.refobjid::regclass::text FROM pg_depend d WHERE d.objid = 'public.instance_map_instance_id_seq'::regclass AND d.deptype = 'a';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var second = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.IsFalse(second.Output.Contains("ALTER SEQUENCE", StringComparison.Ordinal), "nothing is proposed:\n" + second.Output);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
