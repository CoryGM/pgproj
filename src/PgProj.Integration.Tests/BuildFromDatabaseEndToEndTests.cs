using System.Text.Json;
using Npgsql;
using PgProj.Engine;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-318 against a real server: <see cref="BuildRequest.SourceConnectionString"/> builds a live database through the public API, as <c>build --source-connection</c>
/// does (and the command now calls it); a server that refuses the connection is <see cref="BuildStatus.ConnectionFailed"/>; and the engine masks its messages but
/// never the deployment SQL, so a secret a project puts into its SQL on purpose is deployed as written.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BuildFromDatabaseEndToEndTests
{
    [TestMethod]
    public async Task ADatabaseIsBuiltThroughTheEngineAsTheCommandBuildsIt()
    {
        var databaseName = await CreateDatabaseAsync("g318_build");
        try
        {
            var connection = ConnectionStringFor(databaseName);
            await ExecuteAsync(connection, "CREATE TABLE customer (id int PRIMARY KEY, email text NOT NULL); CREATE VIEW v AS SELECT id FROM customer;");
            var events = new List<EngineEvent>();

            var result = await new PgProjEngine().BuildAsync(new BuildRequest { SourceConnectionString = connection, Progress = new Collect(events) }, TestContext.CancellationToken);

            Assert.AreEqual(BuildStatus.Succeeded, result.Status, result.Message);
            Assert.AreEqual(databaseName, result.Source!.Database);
            Assert.IsNull(result.ProjectFilePath);
            Assert.IsNotEmpty(result.Documents);
            Assert.IsTrue(result.Documents.All(static document => document.Path.StartsWith("source:/", StringComparison.Ordinal)), string.Join(", ", result.Documents.Select(static d => d.Path)));
            Assert.IsTrue(result.Model!.Tables.Values.Any(static table => table.Name == "customer"));
            var kinds = events.Select(static e => e.Kind).ToArray();
            CollectionAssert.Contains(kinds, EngineEventKind.SourceConnected);
            CollectionAssert.Contains(kinds, EngineEventKind.SourceRead);
            Assert.IsTrue(events.Where(static e => e.Kind == EngineEventKind.DocumentRead).All(static e => e.Path!.StartsWith("source:/", StringComparison.Ordinal)));

            using var json = JsonDocument.Parse(result.ToJson());
            Assert.AreEqual(databaseName, json.RootElement.GetProperty("source").GetProperty("database").GetString());
            Assert.AreEqual("succeeded", json.RootElement.GetProperty("status").GetString());

            // The command is the engine's call, printed.
            var run = await RunAsync("build", "--project", Path.Combine(Path.GetTempPath(), "pgproj-g318-none", "pgproj.json"), "--source-connection", connection);
            Assert.AreEqual(0, run.ExitCode, run.Output);
            StringAssert.Contains(run.Output, $"Source database: {result.Source}");
            StringAssert.Contains(run.Output, $"Files scaffolded from it: {result.Documents.Count}");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [TestMethod]
    public async Task AWrongPasswordIsConnectionFailedInTheEngineAndInTheJsonOfTheCommand()
    {
        var connection = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Password = "not-the-password-g318" }.ConnectionString;

        var result = await new PgProjEngine().BuildAsync(new BuildRequest { SourceConnectionString = connection }, TestContext.CancellationToken);

        Assert.AreEqual(BuildStatus.ConnectionFailed, result.Status);
        StringAssert.Contains(result.Message, "28P01");
        Assert.DoesNotContain("not-the-password-g318", result.Message!);
        Assert.IsNull(result.Model);

        var run = await RunAsync("build", "--project", Path.Combine(Path.GetTempPath(), "pgproj-g318-none", "pgproj.json"), "--source-connection", connection, "--format", "json");
        Assert.AreEqual(1, run.ExitCode, run.Output);
        StringAssert.Contains(run.Output, "\"status\": \"connectionFailed\"");
        StringAssert.Contains(run.Output, "Build failed: ");
        Assert.DoesNotContain("not-the-password-g318", run.Output);
    }

    [TestMethod]
    public async Task ASecretTheProjectPutsInItsSqlIsDeployedAsWritten()
    {
        var databaseName = await CreateDatabaseAsync("g318_secret");
        var directory = CreateProjectDirectory("g318");
        try
        {
            var connection = ConnectionStringFor(databaseName);
            WriteSchema(directory, "CREATE TABLE t (id int); COMMENT ON TABLE t IS 'password=hunter2';");

            var plan = await new PgProjEngine().PublishAsync(
                new PublishRequest { ProjectFilePath = ProjectFile(directory), TargetConnectionString = connection },
                TestContext.CancellationToken);
            Assert.AreEqual(PublishStatus.Planned, plan.Status, plan.Message);
            StringAssert.Contains(plan.Script, "'password=hunter2'");
            StringAssert.Contains(plan.Parts.Single().TransactionalScript, "'password=hunter2'");

            var dryRun = await RunAsync("publish", "--project", ProjectFile(directory), "--target-connection", connection, "--dry-run");
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            StringAssert.Contains(dryRun.Output, "'password=hunter2'");

            var run = await new PgProjEngine().PublishAsync(
                new PublishRequest { ProjectFilePath = ProjectFile(directory), TargetConnectionString = connection, Mode = PublishMode.Execute },
                TestContext.CancellationToken);
            Assert.AreEqual(PublishStatus.Published, run.Status, run.Message);
            Assert.AreEqual("password=hunter2", await ScalarTextAsync(connection, "SELECT obj_description('public.t'::regclass, 'pg_class')"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    private sealed class Collect(List<EngineEvent> events) : IProgress<EngineEvent>
    {
        public void Report(EngineEvent value) => events.Add(value);
    }

    public TestContext TestContext { get; set; } = null!;
}
