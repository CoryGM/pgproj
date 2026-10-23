using PgProj.Engine;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-309d: <see cref="PgProjEngine.ScaffoldAsync"/> returns the files <c>pgproj init</c> writes, as text, and writes none itself: the same database scaffolded both ways
/// gives the same SQL files, <c>pgproj.json</c>, and tenant file, and the engine's run leaves its output folder untouched.
/// </summary>
[TestClass]
public sealed class ScaffoldAsyncEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA app;
        CREATE TABLE app.customer (id bigint PRIMARY KEY, email text NOT NULL);
        COMMENT ON TABLE app.customer IS 'A buyer.';
        CREATE SCHEMA tenant_acme;
        CREATE TABLE tenant_acme.orders (id int PRIMARY KEY, customer_id bigint REFERENCES app.customer (id));
        CREATE SCHEMA tenant_globex;
        CREATE TABLE tenant_globex.orders (id int PRIMARY KEY, customer_id bigint REFERENCES app.customer (id), note text);
        """;

    [TestMethod]
    public async Task TheEngineReturnsWhatInitWritesAndWritesNothing()
    {
        var database = await CreateDatabaseAsync("g309d");
        var cliDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g309d-cli-" + Guid.NewGuid().ToString("N"));
        var engineDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g309d-engine-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(database);
            await ExecuteAsync(connectionString, Sql);

            var init = await RunAsync("init", "--project", Path.Combine(cliDirectory, "pgproj.json"), "--source-connection", connectionString, "--tenant-prefix", "tenant", "--tenant-file", "--report", "--erd");
            Assert.AreEqual(0, init.ExitCode, init.Output);

            InitResult? seen = null;
            var result = await new PgProjEngine().ScaffoldAsync(
                new ScaffoldRequest
                {
                    SourceConnectionString = connectionString,
                    ProjectFilePath = Path.Combine(engineDirectory, "pgproj.json"),
                    TenantPrefix = "tenant",
                    TenantFile = true,
                    Report = true,
                    Diagram = ErdFilter.None,
                    AfterScaffold = (scaffolded, _) =>
                    {
                        seen = scaffolded;
                        return Task.FromResult<InitDiagramFile?>(null);
                    },
                },
                TestContext.CancellationToken);

            Assert.AreEqual(InitStatus.Succeeded, result.Status, result.Message);
            Assert.IsFalse(Directory.Exists(engineDirectory), "The engine writes no file.");
            Assert.IsNull(seen!.ReportText, "The report is made after the hook.");
            Assert.AreEqual(Path.Combine(engineDirectory, "sql"), result.OutputDirectory);

            // Every SQL file init wrote, and no other, with the same text.
            var written = Directory.EnumerateFiles(Path.Combine(cliDirectory, "sql"), "*.sql", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(Path.Combine(cliDirectory, "sql"), path).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
            CollectionAssert.AreEquivalent(written.Keys.ToArray(), result.Files.Select(static file => file.RelativePath).ToArray());
            foreach (var file in result.Files)
            {
                Assert.AreEqual(written[file.RelativePath], file.Content, file.RelativePath);
            }

            Assert.AreEqual(File.ReadAllText(Path.Combine(cliDirectory, "pgproj.json")), result.ProjectFileText);
            Assert.AreEqual(File.ReadAllText(Path.Combine(cliDirectory, InitResult.TenantFileName)), result.TenantFileText);

            // What init printed after writing the files is what the result carries.
            Assert.AreEqual("tenant_acme", result.Tenants!.TemplateSchema);
            Assert.AreEqual("tenant_globex", result.Tenants.Differing.Single().Schema);
            foreach (var message in result.Messages)
            {
                StringAssert.Contains(init.Output, message.Text);
            }

            StringAssert.StartsWith(result.DiagramText, "erDiagram");
            Assert.AreEqual(BuildStatus.Succeeded, result.Project!.Status);
            StringAssert.StartsWith(result.ReportFileName, "init-report-");
            StringAssert.Contains(result.ReportText, "## Tenants");
            Assert.IsNotEmpty(result.UnreadObjectKinds);
            foreach (var kind in result.UnreadObjectKinds)
            {
                StringAssert.Contains(init.Output, "  - " + kind);
            }
        }
        finally
        {
            TryDeleteDirectory(cliDirectory);
            TryDeleteDirectory(engineDirectory);
            await DropDatabaseAsync(database);
        }
    }

    [TestMethod]
    public async Task ASchemaTheDatabaseLacksIsRefusedAndNothingIsHandedToTheShell()
    {
        var database = await CreateDatabaseAsync("g309d");
        try
        {
            var called = false;
            var result = await new PgProjEngine().ScaffoldAsync(
                new ScaffoldRequest
                {
                    SourceConnectionString = ConnectionStringFor(database),
                    ProjectFilePath = Path.Combine(Path.GetTempPath(), "pgproj-g309d-" + Guid.NewGuid().ToString("N"), "pgproj.json"),
                    IncludeSchemas = ["nope"],
                    AfterScaffold = (_, _) =>
                    {
                        called = true;
                        return Task.FromResult<InitDiagramFile?>(null);
                    },
                },
                TestContext.CancellationToken);

            Assert.AreEqual(InitStatus.Refused, result.Status);
            Assert.AreEqual("--schemas names a schema the database does not have: nope.", result.Message);
            Assert.IsFalse(called);
        }
        finally
        {
            await DropDatabaseAsync(database);
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
