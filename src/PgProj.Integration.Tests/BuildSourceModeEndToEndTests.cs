using System.Text.Json;
using PgProj.Cli;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-304c against a real server: <c>build --source-connection</c> builds a live database in place of the project's SQL files, so <c>--dump-model</c> and <c>--erd</c> describe
/// the database and the diagnostics say whether what <c>init</c> would write builds. <c>build</c> never connects to a target. <c>document --source-connection</c> documents the
/// same dictionary as the project <c>init</c> scaffolds from that database.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BuildSourceModeEndToEndTests
{
    private const string Schema = """
        CREATE TABLE customer (id int PRIMARY KEY, email text NOT NULL);
        CREATE TABLE orders (id int PRIMARY KEY, customer_id int NOT NULL REFERENCES customer (id));
        CREATE VIEW order_ids AS SELECT id FROM orders;
        COMMENT ON TABLE customer IS 'Who buys.';
        """;

    [TestMethod]
    public async Task ADatabaseIsBuiltDumpedAndDrawnWithNoProjectFile()
    {
        var databaseName = await CreateDatabaseAsync("g304c_build");
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g304c-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connection = ConnectionStringFor(databaseName);
            await ExecuteAsync(connection, Schema);
            Directory.CreateDirectory(directory);
            var dump = Path.Combine(directory, "model.json");
            var erd = Path.Combine(directory, "diagram");

            var run = await RunAsync("build", "--project", Path.Combine(directory, "none", "pgproj.json"), "--source-connection", connection, "--dump-model", dump, "--erd", erd);

            Assert.AreEqual(0, run.ExitCode, run.Output);
            StringAssert.Contains(run.Output, "Source database: " + databaseName);
            Assert.DoesNotContain("Loaded project:", run.Output, StringComparison.Ordinal);
            StringAssert.Contains(File.ReadAllText(dump), "customer");
            StringAssert.Contains(File.ReadAllText(erd + ".mmd"), "orders");
            StringAssert.Contains(File.ReadAllText(erd + ".mmd"), "customer");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task OnlyTheOptionOnTheCommandLineTurnsTheModeOnAndTheEnvironmentIsFollowedWhenBare()
    {
        var databaseName = await CreateDatabaseAsync("g304c_mode");
        var projectDirectory = CreateProjectDirectory("g304c-mode", "");
        try
        {
            await ExecuteAsync(ConnectionStringFor(databaseName), "CREATE TABLE in_the_database (id int);");
            WriteSchema(projectDirectory, "CREATE TABLE in_the_files (id int);");
            var dump = Path.Combine(projectDirectory, "model.json");
            var previous = Environment.GetEnvironmentVariable(ConnectionSettings.SourceVariable);
            Environment.SetEnvironmentVariable(ConnectionSettings.SourceVariable, ConnectionStringFor(databaseName));
            try
            {
                var plain = await RunAsync("build", "--project", ProjectFile(projectDirectory), "--dump-model", dump);
                Assert.AreEqual(0, plain.ExitCode, plain.Output);
                StringAssert.Contains(File.ReadAllText(dump), "in_the_files");

                var bare = await RunAsync("build", "--project", ProjectFile(projectDirectory), "--source-connection", "--dump-model", dump);
                Assert.AreEqual(0, bare.ExitCode, bare.Output);
                StringAssert.Contains(File.ReadAllText(dump), "in_the_database");
                Assert.DoesNotContain("in_the_files", File.ReadAllText(dump), StringComparison.Ordinal);

                Environment.SetEnvironmentVariable(ConnectionSettings.SourceVariable, null);
                var none = await RunAsync("build", "--project", ProjectFile(projectDirectory), "--source-connection");
                Assert.AreEqual(1, none.ExitCode, none.Output);
                StringAssert.Contains(none.Output, "source connection string is required");
            }
            finally
            {
                Environment.SetEnvironmentVariable(ConnectionSettings.SourceVariable, previous);
            }
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task BuildTakesNoTargetAndRefusesTenantsInThisMode()
    {
        var projectDirectory = CreateProjectDirectory("g304c-tenants", """, "tenants": { "template": "./sql/Tenant", "prefix": "shop" }""");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectDirectory, "sql", "Tenant"));
            var target = await RunAsync("build", "--project", ProjectFile(projectDirectory), "--target-connection", "Host=x");
            Assert.AreEqual(1, target.ExitCode, target.Output);

            var tenants = await RunAsync("build", "--project", ProjectFile(projectDirectory), "--source-connection", "Host=127.0.0.1;Port=1;Database=x;Username=x;Timeout=1");
            Assert.AreEqual(1, tenants.ExitCode, tenants.Output);
            StringAssert.Contains(tenants.Output, "tenants");
        }
        finally
        {
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task DocumentingTheDatabaseGivesTheDictionaryOfTheProjectInitScaffoldsFromIt()
    {
        var databaseName = await CreateDatabaseAsync("g304c_doc");
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g304c-doc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connection = ConnectionStringFor(databaseName);
            await ExecuteAsync(connection, Schema);
            var scaffold = Path.Combine(directory, "scaffold", "pgproj.json");
            var init = await RunAsync("init", "--project", scaffold, "--source-connection", connection);
            Assert.AreEqual(0, init.ExitCode, init.Output);

            var live = await RunAsync("document", "--project", Path.Combine(directory, "none", "pgproj.json"), "--source-connection", connection, "--format", "json", "--output", Path.Combine(directory, "live"));
            var fromFiles = await RunAsync("document", "--project", scaffold, "--format", "json", "--output", Path.Combine(directory, "files"));
            Assert.AreEqual(0, live.ExitCode, live.Output);
            Assert.AreEqual(0, fromFiles.ExitCode, fromFiles.Output);

            Assert.AreEqual(Schemas(Path.Combine(directory, "live")), Schemas(Path.Combine(directory, "files")));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>The schemas of a documentation file, with each view column's type left out: a database names a type as the catalog prints it (<c>integer</c>), a project as it was written (<c>pg_catalog.int4</c>).</summary>
    private static string Schemas(string outputDirectory)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(outputDirectory, "database-documentation.json")))!;
        foreach (var schema in root["schemas"]!.AsArray())
        {
            foreach (var view in schema!["views"]?.AsArray() ?? [])
            {
                foreach (var column in view!["columns"]!.AsArray())
                {
                    column!.AsObject().Remove("type");
                }
            }
        }

        return root["schemas"]!.ToJsonString();
    }
}
