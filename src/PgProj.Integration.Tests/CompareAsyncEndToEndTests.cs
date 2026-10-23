using System.Text.Json;
using PgProj.Core;
using PgProj.Engine;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-309b against a real server: <see cref="PgProjEngine.CompareAsync"/> called as a library, with the project held in memory, finds what <c>pgproj diff</c> finds;
/// a source database is scaffolded in memory, so no temporary folder is made, and its files are named <c>source:/...</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CompareAsyncEndToEndTests
{
    [TestMethod]
    public async Task AProjectInMemoryIsComparedWithADatabaseAndGivesWhatDiffGives()
    {
        var databaseName = await CreateDatabaseAsync("g309b");
        var directory = CreateProjectDirectory("g309b");
        try
        {
            var connection = ConnectionStringFor(databaseName);
            WriteSchema(directory, "CREATE SCHEMA s; CREATE TABLE s.t (id int); CREATE INDEX t_id_idx ON s.t (id);");
            var published = await RunAsync("publish", "--project", ProjectFile(directory), "--target-connection", connection, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            // The project is changed, and held in memory under the path it has on disk; the disk keeps the published version.
            var files = new InMemoryFileSource()
                .Set(ProjectFile(directory), File.ReadAllText(ProjectFile(directory)))
                .Set(Path.Combine(directory, "sql", "schema.sql"), "CREATE SCHEMA s; CREATE TABLE s.t (id int); COMMENT ON TABLE s.t IS 'orders'; CREATE TABLE s.u (id int);");
            var events = new List<EngineEvent>();

            var result = await new PgProjEngine().CompareAsync(
                new CompareRequest { Files = files, ProjectFilePath = ProjectFile(directory), TargetConnectionString = connection, Progress = new Collect(events) },
                TestContext.CancellationToken);

            Assert.AreEqual(CompareStatus.Succeeded, result.Status, result.Message);
            CollectionAssert.AreEquivalent(new[] { "s.u", "s.u.id" }, result.Changes.Added.Select(static change => change.NodeId).ToArray());
            Assert.AreEqual("s.t_id_idx", result.Changes.Removed.Single().NodeId);
            Assert.AreEqual("s.t", result.Changes.Modified.Single().NodeId);
            Assert.AreEqual(4, result.DifferenceCount);
            Assert.IsNull(result.Source);
            CollectionAssert.Contains(events.Select(static e => e.Kind).ToArray(), EngineEventKind.TargetConnected);
            CollectionAssert.Contains(events.Select(static e => e.Kind).ToArray(), EngineEventKind.LiveRead);

            using var json = JsonDocument.Parse(result.ToJson());
            Assert.AreEqual(1, json.RootElement.GetProperty("summary").GetProperty("removed").GetInt32());

            // The same files on disk through the CLI: the same counts.
            File.WriteAllText(Path.Combine(directory, "sql", "schema.sql"), "CREATE SCHEMA s; CREATE TABLE s.t (id int); COMMENT ON TABLE s.t IS 'orders'; CREATE TABLE s.u (id int);");
            var diff = await RunAsync("diff", "--project", ProjectFile(directory), "--target-connection", connection);
            StringAssert.EndsWith(diff.Output.TrimEnd(), "Added: 2" + Environment.NewLine + "Removed: 1" + Environment.NewLine + "Modified: 1");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ASourceDatabaseIsScaffoldedInMemoryAndLeavesNoTemporaryFolder()
    {
        var sourceName = await CreateDatabaseAsync("g309b_src");
        var targetName = await CreateDatabaseAsync("g309b_tgt");
        var before = TemporarySourceFolders();
        try
        {
            await ExecuteAsync(ConnectionStringFor(sourceName), "CREATE TABLE customer (id int PRIMARY KEY, email text NOT NULL); CREATE VIEW v AS SELECT id FROM customer;");

            var result = await new PgProjEngine().CompareAsync(
                new CompareRequest
                {
                    ProjectFilePath = Path.Combine(Path.GetTempPath(), "pgproj-g309b-none", "pgproj.json"),
                    SourceConnectionString = ConnectionStringFor(sourceName),
                    TargetConnectionString = ConnectionStringFor(targetName),
                },
                TestContext.CancellationToken);

            Assert.AreEqual(CompareStatus.Succeeded, result.Status, result.Message);
            Assert.AreEqual(sourceName, result.Source!.Database);
            Assert.AreEqual(targetName, result.Target!.Database);
            Assert.IsGreaterThan(0, result.AddedCount);
            Assert.IsTrue(result.Documents.All(static document => document.Path.StartsWith("source:/", StringComparison.Ordinal)), string.Join(", ", result.Documents.Select(static document => document.Path)));
            CollectionAssert.AreEquivalent(before, TemporarySourceFolders(), "source mode must not leave (or make) a temporary folder");
        }
        finally
        {
            await DropDatabaseAsync(sourceName);
            await DropDatabaseAsync(targetName);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static string[] TemporarySourceFolders()
        => Directory.GetDirectories(Path.GetTempPath(), "pgproj-source-*");

    private sealed class Collect(List<EngineEvent> events) : IProgress<EngineEvent>
    {
        public void Report(EngineEvent value) => events.Add(value);
    }
}
