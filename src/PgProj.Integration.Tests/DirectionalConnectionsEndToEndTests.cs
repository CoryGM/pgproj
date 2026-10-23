using System.Text.Json;
using PgProj.Cli;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-304a against a real server, on every version. A connection has a direction: <c>init</c> and <c>document</c> read a source (<c>--source-connection</c>,
/// <c>PGPROJ_SOURCE_CONNECTION</c>, <c>sourceConnectionString</c>), <c>diff</c> and <c>publish</c> work on a target (<c>--target-connection</c>,
/// <c>PGPROJ_TARGET_CONNECTION</c>, <c>targetConnectionString</c>). Before, <c>init</c> wrote the database it scaffolded from where <c>diff</c> and <c>publish</c> read
/// their database, so scaffolding production pointed <c>publish</c> at production.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DirectionalConnectionsEndToEndTests
{
    private const string Unreachable = "Host=127.0.0.1;Port=1;Database=x;Username=x;Timeout=1";

    [TestMethod]
    public async Task TheTargetComesFromTheOptionThenTheVariableThenTheProjectFile()
    {
        var databaseName = await CreateDatabaseAsync("g304a_target");
        var projectDirectory = CreateProjectDirectory("g304a-target", $$""", "targetConnectionString": "{{Unreachable}}" """);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE a (id int PRIMARY KEY);");

            // The variable beats the project file, which points nowhere.
            using (new EnvironmentVariable(ConnectionSettings.TargetVariable, connectionString))
            {
                var fromVariable = await RunAsync("publish", "--project", projectFile, "--execute");
                Assert.AreEqual(0, fromVariable.ExitCode, fromVariable.Output);
            }

            Assert.AreEqual("a", await ScalarTextAsync(connectionString, "SELECT to_regclass('public.a')::text"));

            // The option beats the variable, which points nowhere.
            WriteSchema(projectDirectory, "CREATE TABLE a (id int PRIMARY KEY); CREATE TABLE b (id int PRIMARY KEY);");
            using (new EnvironmentVariable(ConnectionSettings.TargetVariable, Unreachable))
            {
                var fromOption = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
                Assert.AreEqual(0, fromOption.ExitCode, fromOption.Output);
            }

            Assert.AreEqual("b", await ScalarTextAsync(connectionString, "SELECT to_regclass('public.b')::text"));

            // With neither, the project file's targetConnectionString is the target.
            File.WriteAllText(projectFile, $$"""{ "name": "g304a-target", "version": "1.0", "source": "./sql", "targetConnectionString": "{{connectionString}}" }""");
            using (new EnvironmentVariable(ConnectionSettings.TargetVariable, null))
            {
                await AssertDiffIsEmptyAsync(projectFile, connectionString);
                var fromFile = await RunAsync("diff", "--project", projectFile);
                Assert.AreEqual(0, fromFile.ExitCode, fromFile.Output);
                StringAssert.Contains(fromFile.Output, "Modified: 0");
            }
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AScaffoldRecordsItsSourceAndNeverNamesATarget()
    {
        var databaseName = await CreateDatabaseAsync("g304a_scaffold");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g304a-scaffold-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ExecuteAsync(ConnectionStringFor(databaseName), "CREATE TABLE t (id int PRIMARY KEY);");
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", ConnectionStringFor(databaseName));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(scaffoldProject));
            Assert.IsTrue(document.RootElement.TryGetProperty("sourceConnectionString", out _));
            Assert.IsFalse(document.RootElement.TryGetProperty("targetConnectionString", out _));
            Assert.IsFalse(document.RootElement.TryGetProperty("connectionString", out _));

            // The project the scaffold made does not know where to publish, so it cannot publish to the database it came from.
            using var noTarget = new EnvironmentVariable(ConnectionSettings.TargetVariable, null);
            foreach (var command in new[] { "diff", "publish" })
            {
                var run = await RunAsync(command, "--project", scaffoldProject);
                Assert.AreEqual(1, run.ExitCode, $"{command}: {run.Output}");
                StringAssert.Contains(run.Output, "target connection string is required");
                StringAssert.Contains(run.Output, "--target-connection");
                StringAssert.Contains(run.Output, ConnectionSettings.TargetVariable);
                StringAssert.Contains(run.Output, "targetConnectionString");
            }
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    [TestMethod]
    public async Task InitReadsTheSourceFromTheVariableWithOrWithoutTheBareOption()
    {
        var databaseName = await CreateDatabaseAsync("g304a_init_env");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g304a-initenv-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ExecuteAsync(ConnectionStringFor(databaseName), "CREATE TABLE t (id int PRIMARY KEY);");
            using (new EnvironmentVariable(ConnectionSettings.SourceVariable, ConnectionStringFor(databaseName)))
            {
                var first = await RunAsync("init", "--project", Path.Combine(scaffoldDirectory, "one", "pgproj.json"));
                Assert.AreEqual(0, first.ExitCode, first.Output);
                var second = await RunAsync("init", "--project", Path.Combine(scaffoldDirectory, "two", "pgproj.json"), "--source-connection");
                Assert.AreEqual(0, second.ExitCode, second.Output);
            }

            Assert.IsTrue(File.Exists(Path.Combine(scaffoldDirectory, "one", "sql", "Schema", "public", "Table", "t.sql")));
            StringAssert.Contains(File.ReadAllText(Path.Combine(scaffoldDirectory, "two", "pgproj.json")), "sourceConnectionString");

            using (new EnvironmentVariable(ConnectionSettings.SourceVariable, null))
            {
                var none = await RunAsync("init", "--project", Path.Combine(scaffoldDirectory, "three", "pgproj.json"));
                Assert.AreEqual(1, none.ExitCode, none.Output);
                StringAssert.Contains(none.Output, "source connection string is required");
                StringAssert.Contains(none.Output, ConnectionSettings.SourceVariable);
            }
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    [TestMethod]
    public async Task DocumentReadsADatabaseOnlyWhenToldToAndTakesItsConnectionFromTheVariableOrTheFile()
    {
        var databaseName = await CreateDatabaseAsync("g304a_document");
        var projectDirectory = CreateProjectDirectory("g304a-document", $$""", "sourceConnectionString": "{{Unreachable}}" """);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE in_the_files (id int PRIMARY KEY);");
            await ExecuteAsync(connectionString, "CREATE TABLE in_the_database (id int PRIMARY KEY);");

            // The variable alone does not switch document to a database.
            using (new EnvironmentVariable(ConnectionSettings.SourceVariable, connectionString))
            {
                var output = Path.Combine(projectDirectory, "from-files");
                var run = await RunAsync("document", "--project", projectFile, "--format", "json", "--output", output);
                Assert.AreEqual(0, run.ExitCode, run.Output);
                Assert.AreEqual("project", SourceKind(output));

                // A bare option takes the variable, which names the real database.
                var bare = Path.Combine(projectDirectory, "from-variable");
                run = await RunAsync("document", "--project", projectFile, "--source-connection", "--format", "json", "--output", bare);
                Assert.AreEqual(0, run.ExitCode, run.Output);
                Assert.AreEqual("database", SourceKind(bare));
                StringAssert.Contains(File.ReadAllText(Path.Combine(bare, "database-documentation.json")), "in_the_database");
            }

            // With no variable, a bare option takes sourceConnectionString, which here names nothing reachable.
            using (new EnvironmentVariable(ConnectionSettings.SourceVariable, null))
            {
                var run = await RunAsync("document", "--project", projectFile, "--source-connection", "--format", "json", "--output", Path.Combine(projectDirectory, "unreachable"));
                Assert.AreEqual(1, run.ExitCode, run.Output);

                // Pointed at the real database it is read.
                File.WriteAllText(projectFile, $$"""{ "name": "g304a-document", "version": "1.0", "source": "./sql", "sourceConnectionString": "{{connectionString}}" }""");
                var fromFile = Path.Combine(projectDirectory, "from-file");
                run = await RunAsync("document", "--project", projectFile, "--source-connection", "--format", "json", "--output", fromFile);
                Assert.AreEqual(0, run.ExitCode, run.Output);
                Assert.AreEqual("database", SourceKind(fromFile));

                // Nowhere to get a connection from.
                File.WriteAllText(projectFile, """{ "name": "g304a-document", "version": "1.0", "source": "./sql" }""");
                run = await RunAsync("document", "--project", projectFile, "--source-connection", "--format", "json", "--output", Path.Combine(projectDirectory, "nothing"));
                Assert.AreEqual(1, run.ExitCode, run.Output);
                StringAssert.Contains(run.Output, "source connection string is required");
            }
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AProjectFileWithTheRemovedConnectionStringIsRefusedByEveryCommandThatReadsIt()
    {
        var projectDirectory = CreateProjectDirectory("g304a-removed", $$""", "connectionString": "{{Unreachable}}" """);
        try
        {
            foreach (var command in new[] { "build", "diff", "publish", "document" })
            {
                var run = await RunAsync(command, "--project", ProjectFile(projectDirectory));
                Assert.AreEqual(1, run.ExitCode, $"{command}: {run.Output}");
                StringAssert.Contains(run.Output, "'connectionString' property, which is no longer read", command);
                StringAssert.Contains(run.Output, "targetConnectionString", command);
                StringAssert.Contains(run.Output, "sourceConnectionString", command);
            }
        }
        finally
        {
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static string SourceKind(string outputDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDirectory, "database-documentation.json")));
        return document.RootElement.GetProperty("source").GetProperty("kind").GetString()!;
    }

    /// <summary>Sets an environment variable for a test and puts it back; a null value removes it.</summary>
    private sealed class EnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentVariable(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }
}
