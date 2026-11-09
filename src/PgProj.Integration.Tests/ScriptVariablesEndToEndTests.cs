using Npgsql;
using PgProj.Engine;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-336 against a real server (the user's design of 2026-11-09): the project's deploy scripts name values with psql's syntax; a plan keeps the placeholders
/// and says so; <c>--execute</c> fills them in only in what it runs, refuses to run without a value, and masks a value in a failure; the written script
/// runs with <c>psql -v</c>. The engine is called with <see cref="PublishRequest.Variables"/>, as the CLI calls it with <c>PGPROJ_VAR_&lt;name&gt;</c>.
/// </summary>
[TestClass]
public sealed class ScriptVariablesEndToEndTests
{
    private const string Schema = "CREATE SCHEMA app; CREATE TABLE app.settings (id int PRIMARY KEY);";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task APlanKeepsTheVariablesAndExecuteFillsThemInOnlyInWhatItRuns()
    {
        var role = $"g336_{Guid.NewGuid():N}";
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} LOGIN;");
        var databaseName = await CreateDatabaseAsync("g336run");
        var directory = CreateProjectDirectory("g336run", """, "postdeploy": "./post.sql" """);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            WriteSchema(directory, Schema);
            File.WriteAllText(Path.Combine(directory, "post.sql"), $"ALTER ROLE {role} PASSWORD :'app_password';\nCOMMENT ON TABLE app.settings IS :'region';\n");
            const string Secret = "c0rrect-h0rse-g336";
            var events = new List<EngineEvent>();

            var plan = await new PgProjEngine().PublishAsync(Request(directory, connectionString, PublishMode.Plan), TestContext.CancellationToken);
            Assert.AreEqual(PublishStatus.Planned, plan.Status, plan.Message);
            StringAssert.Contains(plan.Script, "PASSWORD :'app_password'", "a plan keeps the placeholder");
            var kept = plan.Warnings.Single(static warning => warning.Kind == PublishNoticeKind.Variables);
            StringAssert.Contains(kept.Text, ":'app_password', :'region'");
            Assert.IsTrue(kept.ForKeptScript);

            var refused = await new PgProjEngine().PublishAsync(Request(directory, connectionString, PublishMode.Execute) with { Variables = new Dictionary<string, string> { ["region"] = "eu" } }, TestContext.CancellationToken);
            Assert.AreEqual(PublishStatus.Refused, refused.Status);
            StringAssert.StartsWith(refused.Refusals.Single().Text, "Publish failed: the script uses :'app_password', which has no value, so nothing was run.");
            Assert.AreEqual(string.Empty, await ScalarTextAsync(connectionString, "select coalesce(to_regclass('app.settings')::text, '')"), "nothing ran");

            var published = await new PgProjEngine().PublishAsync(
                Request(directory, connectionString, PublishMode.Execute) with
                {
                    Variables = new Dictionary<string, string> { ["app_password"] = Secret, ["region"] = "eu-west" },
                    Progress = new Collect(events),
                },
                TestContext.CancellationToken);

            Assert.AreEqual(PublishStatus.Published, published.Status, published.Message);
            Assert.AreEqual("eu-west", await ScalarTextAsync(connectionString, "select obj_description('app.settings'::regclass)"));
            await using (var login = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Username = role, Password = Secret, Pooling = false }.ConnectionString))
            {
                await login.OpenAsync(TestContext.CancellationToken); // the password the run sent
            }

            Assert.DoesNotContain(Secret, published.Script, "the result keeps the placeholder");
            Assert.IsFalse(events.Any(e => (e.Message ?? string.Empty).Contains(Secret, StringComparison.Ordinal) || (e.Script ?? string.Empty).Contains(Secret, StringComparison.Ordinal)), "no event shows the value");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task AFailedStatementNamesItAsWrittenAndMasksTheValue()
    {
        var databaseName = await CreateDatabaseAsync("g336fail");
        var directory = CreateProjectDirectory("g336fail", """, "postdeploy": "./post.sql" """);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            WriteSchema(directory, Schema);
            // The server quotes the value it could not read in its message.
            File.WriteAllText(Path.Combine(directory, "post.sql"), "SELECT CAST(:'port' AS integer);\n");
            const string Secret = "not-a-number-g336";

            var result = await new PgProjEngine().PublishAsync(
                Request(directory, connectionString, PublishMode.Execute) with { Variables = new Dictionary<string, string> { ["port"] = Secret } },
                TestContext.CancellationToken);

            Assert.AreEqual(PublishStatus.ExecutionFailed, result.Status);
            var failure = result.Execution!.Failure!;
            Assert.DoesNotContain(Secret, result.Message!);
            Assert.DoesNotContain(Secret, failure.Message);
            StringAssert.Contains(failure.Message, "*****", "the server's message is kept, the value masked");
            StringAssert.Contains(failure.Statement!, ":'port'", "the statement as written");
            Assert.IsFalse(result.Execution.FailureReport.Any(static line => line.Contains(Secret, StringComparison.Ordinal)));
            Assert.AreEqual(string.Empty, await ScalarTextAsync(connectionString, "select coalesce(to_regclass('app.settings')::text, '')"), "rolled back");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task TheWrittenScriptRunsWithPsqlsVariables()
    {
        var databaseName = await CreateDatabaseAsync("g336psql");
        var directory = CreateProjectDirectory("g336psql", """, "postdeploy": "./post.sql" """);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(directory);
            WriteSchema(directory, Schema);
            // A row, not a comment: diff compares a table's comment, so a script that sets one would leave a change behind.
            File.WriteAllText(Path.Combine(directory, "post.sql"), "INSERT INTO app.settings VALUES (length(:'region'));\n");
            var scriptPath = Path.Combine(directory, "deploy.sql");

            var written = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--output", scriptPath);
            Assert.AreEqual(0, written.ExitCode, written.Output);
            StringAssert.Contains(written.Output, "psql -v region=... -f <file>");

            var (exitCode, output) = await PostgresContainerFixture.RunPsqlFileAsync(databaseName, await File.ReadAllTextAsync(scriptPath, TestContext.CancellationToken), "-v", "ON_ERROR_STOP=1", "-v", "region=ap-south");

            Assert.AreEqual(0L, exitCode, output);
            Assert.AreEqual("8", await ScalarTextAsync(connectionString, "select id::text from app.settings"), "length('ap-south'), from psql's -v");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ATenantsVariableIsRefusedAsARequestsVariable()
    {
        var result = await new PgProjEngine().PublishAsync(
            new PublishRequest { ProjectFilePath = "pgproj.json", TargetConnectionString = "Host=127.0.0.1;Port=1", Variables = new Dictionary<string, string> { ["tenant_id"] = "x" } },
            TestContext.CancellationToken);

        Assert.AreEqual(PublishStatus.Refused, result.Status);
        StringAssert.Contains(result.Message!, "tenant_id is set by pgproj for each tenant");
    }

    private static PublishRequest Request(string directory, string connectionString, PublishMode mode)
        => new() { ProjectFilePath = ProjectFile(directory), TargetConnectionString = connectionString, Mode = mode };

    private sealed class Collect(List<EngineEvent> events) : IProgress<EngineEvent>
    {
        public void Report(EngineEvent value) => events.Add(value);
    }
}
