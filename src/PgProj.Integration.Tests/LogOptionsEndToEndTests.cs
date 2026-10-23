using System.CommandLine;
using System.Text.Json;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-143: <c>--verbose</c> and <c>--quiet</c> against a real server, on the commands that connect. The result of a command (the script <c>publish</c> prints,
/// the JSON <c>diff</c> writes) is never filtered; standard output and standard error are captured apart, since where a line goes is the point.
/// </summary>
[TestClass]
public sealed class LogOptionsEndToEndTests
{
    private const string Sql = "CREATE TABLE public.g143_t (id bigint NOT NULL);";

    [TestMethod]
    public async Task PublishQuietPrintsTheScriptAndNothingElse()
    {
        await WithProjectAsync(async (projectFile, connectionString) =>
        {
            var normal = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString);
            var quiet = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--quiet");

            Assert.AreEqual(0, normal.ExitCode, normal.Error);
            StringAssert.Contains(normal.Out, "Loaded project:");
            StringAssert.Contains(normal.Out, "g143_t");

            Assert.AreEqual(0, quiet.ExitCode, quiet.Error);
            Assert.IsFalse(quiet.Out.Contains("Loaded project:", StringComparison.Ordinal), quiet.Out);
            Assert.IsFalse(quiet.Out.Contains("No diagnostics.", StringComparison.Ordinal), quiet.Out);
            StringAssert.Contains(quiet.Out, "CREATE TABLE");
            StringAssert.Contains(quiet.Out, "g143_t");
            Assert.AreEqual(string.Empty, quiet.Error);
        });
    }

    [TestMethod]
    public async Task PublishQuietExecuteSaysNothingAndStillDeploys()
    {
        await WithProjectAsync(async (projectFile, connectionString) =>
        {
            var quiet = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--quiet");

            Assert.AreEqual(0, quiet.ExitCode, quiet.Error);
            Assert.AreEqual(string.Empty, quiet.Error);
            Assert.IsFalse(quiet.Out.Contains("Executed deployment script.", StringComparison.Ordinal), quiet.Out);
            await EndToEndSupport.AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task PublishQuietStillRefusesAndReportsOnStandardError()
    {
        await WithProjectAsync(async (projectFile, connectionString) =>
        {
            await EndToEndSupport.ExecuteAsync(connectionString, "CREATE TABLE public.g143_extra (id bigint);");

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--quiet");

            Assert.AreEqual(1, refused.ExitCode, refused.Out);
            StringAssert.Contains(refused.Error, "Publish failed:");
            StringAssert.Contains(refused.Error, "--allow-destructive");
        });
    }

    [TestMethod]
    public async Task PublishVerboseNarratesTheStepsOnStandardErrorAndKeepsTheScriptOnStandardOutput()
    {
        await WithProjectAsync(async (projectFile, connectionString) =>
        {
            var verbose = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--verbose");

            Assert.AreEqual(0, verbose.ExitCode, verbose.Error);
            StringAssert.Contains(verbose.Error, "[verbose] Connected to PostgreSQL");
            StringAssert.Contains(verbose.Error, "[verbose] Read the live database in");
            StringAssert.Contains(verbose.Error, "2 added, 0 removed, 0 modified");
            Assert.IsFalse(verbose.Out.Contains("[verbose]", StringComparison.Ordinal), verbose.Out);
            StringAssert.Contains(verbose.Out, "g143_t");
        });
    }

    [TestMethod]
    public async Task DiffJsonStaysValidJsonOnStandardOutputWhateverTheLevel()
    {
        await WithProjectAsync(async (projectFile, connectionString) =>
        {
            foreach (var level in new[] { "--quiet", "--verbose" })
            {
                var run = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--format", "json", level);

                Assert.AreEqual(0, run.ExitCode, run.Error);
                using var document = JsonDocument.Parse(run.Out);
                Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind);
                Assert.AreEqual(level == "--verbose", run.Error.Contains("[verbose] Connected to PostgreSQL", StringComparison.Ordinal), $"{level}: {run.Error}");
            }
        });
    }

    [TestMethod]
    public async Task DiffQuietWritesTheChangeListWithoutTheHeader()
    {
        await WithProjectAsync(async (projectFile, connectionString) =>
        {
            var quiet = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--quiet");

            Assert.AreEqual(0, quiet.ExitCode, quiet.Error);
            Assert.IsFalse(quiet.Out.Contains("Loaded project:", StringComparison.Ordinal), quiet.Out);
            StringAssert.Contains(quiet.Out, "Added: 2");
            Assert.AreEqual(string.Empty, quiet.Error);
        });
    }

    [TestMethod]
    public async Task InitQuietWritesNoLinesAndVerboseNamesEachFileWritten()
    {
        var databaseName = await EndToEndSupport.CreateDatabaseAsync("g143_init");
        var connectionString = EndToEndSupport.ConnectionStringFor(databaseName);
        var quietDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g143-quiet-" + Guid.NewGuid().ToString("N"));
        var verboseDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g143-verbose-" + Guid.NewGuid().ToString("N"));
        try
        {
            await EndToEndSupport.ExecuteAsync(connectionString, Sql);

            var quiet = await RunAsync("init", "--source-connection", connectionString, "--project", Path.Combine(quietDirectory, "pgproj.json"), "--quiet");
            var verbose = await RunAsync("init", "--source-connection", connectionString, "--project", Path.Combine(verboseDirectory, "pgproj.json"), "--verbose");

            Assert.AreEqual(0, quiet.ExitCode, quiet.Error);
            Assert.AreEqual(string.Empty, quiet.Out);
            Assert.AreEqual(string.Empty, quiet.Error);
            Assert.IsTrue(File.Exists(Path.Combine(quietDirectory, "pgproj.json")));

            Assert.AreEqual(0, verbose.ExitCode, verbose.Error);
            StringAssert.Contains(verbose.Out, "SQL file(s) to:");
            StringAssert.Contains(verbose.Error, "[verbose] Connected to PostgreSQL");
            StringAssert.Contains(verbose.Error, "[verbose] Wrote: ");
            StringAssert.Contains(verbose.Error, "g143_t");
        }
        finally
        {
            EndToEndSupport.TryDeleteDirectory(quietDirectory);
            EndToEndSupport.TryDeleteDirectory(verboseDirectory);
            await EndToEndSupport.DropDatabaseAsync(databaseName);
        }
    }

    [TestMethod]
    public async Task AnErrorMessageNeverShowsAPasswordInTheConnectionString()
    {
        var projectDirectory = EndToEndSupport.CreateProjectDirectory("g143_secret");
        try
        {
            EndToEndSupport.WriteSchema(projectDirectory, Sql);

            var run = await RunAsync("diff", "--project", EndToEndSupport.ProjectFile(projectDirectory), "--target-connection", "Host=127.0.0.1;Port=1;Database=d;Username=u;Password=hunter2;Timeout=2", "--verbose");

            Assert.AreEqual(1, run.ExitCode, run.Out);
            StringAssert.Contains(run.Error, "Diff failed:");
            Assert.IsFalse((run.Out + run.Error).Contains("hunter2", StringComparison.Ordinal), run.Out + run.Error);
        }
        finally
        {
            EndToEndSupport.TryDeleteDirectory(projectDirectory);
        }
    }

    private static async Task WithProjectAsync(Func<string, string, Task> test)
    {
        var databaseName = await EndToEndSupport.CreateDatabaseAsync("g143");
        var connectionString = EndToEndSupport.ConnectionStringFor(databaseName);
        var projectDirectory = EndToEndSupport.CreateProjectDirectory("g143");
        try
        {
            EndToEndSupport.WriteSchema(projectDirectory, Sql);
            await test(EndToEndSupport.ProjectFile(projectDirectory), connectionString);
        }
        finally
        {
            EndToEndSupport.TryDeleteDirectory(projectDirectory);
            await EndToEndSupport.DropDatabaseAsync(databaseName);
        }
    }

    /// <summary>Like <see cref="EndToEndSupport.RunAsync"/>, with standard output and standard error captured apart.</summary>
    private static async Task<(int ExitCode, string Out, string Error)> RunAsync(params string[] args)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj")
        {
            InitCommand.Create(context),
            BuildCommand.Create(context),
            DiffCommand.Create(context),
            PublishCommand.Create(context),
        };

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exitCode = await root.Parse(args).InvokeAsync();
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
