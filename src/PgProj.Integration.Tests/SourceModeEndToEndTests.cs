using System.Text.Json;
using PgProj.Cli;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-304b against a real server: <c>diff</c> and <c>publish</c> with <c>--source-connection</c> take a live database as the desired state in place of the project's SQL
/// files, for environment promotion (dev -> QA -> prod) and parity checks.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SourceModeEndToEndTests
{
    private const string Schema = """
        CREATE TABLE customer (id int PRIMARY KEY, email text NOT NULL);
        CREATE TABLE orders (id int PRIMARY KEY, customer_id int NOT NULL REFERENCES customer (id), total numeric(10,2) DEFAULT 0);
        CREATE INDEX orders_customer ON orders (customer_id);
        CREATE VIEW big_orders AS SELECT id, total FROM orders WHERE total > 100;
        CREATE FUNCTION double_it(x int) RETURNS int LANGUAGE sql AS 'SELECT x * 2';
        COMMENT ON TABLE customer IS 'Who buys.';
        """;

    private static async Task<(string Name, string Connection)> NewDatabaseAsync(string prefix, string? sql = null)
    {
        var name = await CreateDatabaseAsync(prefix);
        var connection = ConnectionStringFor(name);
        if (sql is not null)
        {
            await ExecuteAsync(connection, sql);
        }

        return (name, connection);
    }

    private static string NoProject(string directory) => Path.Combine(directory, "none", "pgproj.json");

    [TestMethod]
    public async Task ThePublishedTargetMatchesTheSourceWithNoProjectFile()
    {
        var (sourceName, source) = await NewDatabaseAsync("g304b_src", Schema);
        var (targetName, target) = await NewDatabaseAsync("g304b_tgt");
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g304b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = NoProject(directory);

            var preview = await RunAsync("publish", "--project", project, "--source-connection", source, "--target-connection", target, "--dry-run");
            Assert.AreEqual(0, preview.ExitCode, preview.Output);
            StringAssert.Contains(preview.Output, "Source " + sourceName);
            StringAssert.Contains(preview.Output, "-> Target " + targetName);
            StringAssert.Contains(preview.Output, "CREATE TABLE");
            Assert.AreEqual(string.Empty, await ScalarTextAsync(target, "SELECT coalesce(to_regclass('public.customer')::text, '')"), "a dry run changes nothing");

            var published = await RunAsync("publish", "--project", project, "--source-connection", source, "--target-connection", target, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("customer", await ScalarTextAsync(target, "SELECT to_regclass('public.customer')::text"));
            Assert.AreEqual("4", await ScalarTextAsync(target, "SELECT double_it(2)::text"));
            Assert.AreEqual("Who buys.", await ScalarTextAsync(target, "SELECT obj_description('public.customer'::regclass)"));

            // Parity: the pair is clean in both ways, and a second publish proposes nothing.
            var parity = await RunAsync("diff", "--project", project, "--source-connection", source, "--target-connection", target, "--exit-code");
            Assert.AreEqual(0, parity.ExitCode, parity.Output);
            StringAssert.Contains(parity.Output, "Added: 0");
            StringAssert.Contains(parity.Output, "Removed: 0");
            StringAssert.Contains(parity.Output, "Modified: 0");

            var again = await RunAsync("publish", "--project", project, "--source-connection", source, "--target-connection", target, "--dry-run");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            Assert.DoesNotContain("CREATE ", again.Output, StringComparison.Ordinal, again.Output);
        }
        finally
        {
            await DropDatabaseAsync(sourceName);
            await DropDatabaseAsync(targetName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ADatabaseCannotBeItsOwnSource()
    {
        var (name, connection) = await NewDatabaseAsync("g304b_same", Schema);
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g304b-same-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = NoProject(directory);
            foreach (var command in new[] { "diff", "publish" })
            {
                var run = await RunAsync(command, "--project", project, "--source-connection", connection, "--target-connection", connection);
                Assert.AreEqual(1, run.ExitCode, $"{command}: {run.Output}");
                StringAssert.Contains(run.Output, "same database", command);
            }

            // The same server and database reached under another name is caught once both are open.
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection);
            var alias = new Npgsql.NpgsqlConnectionStringBuilder(connection) { Host = builder.Host == "localhost" ? "127.0.0.1" : "localhost" }.ConnectionString;
            var viaAlias = await RunAsync("diff", "--project", project, "--source-connection", connection, "--target-connection", alias);
            Assert.AreEqual(1, viaAlias.ExitCode, viaAlias.Output);
            StringAssert.Contains(viaAlias.Output, "same database");
        }
        finally
        {
            await DropDatabaseAsync(name);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ADevToQaToProdPromotionLeavesEveryPairClean()
    {
        var (devName, dev) = await NewDatabaseAsync("g304b_dev", Schema);
        var (qaName, qa) = await NewDatabaseAsync("g304b_qa");
        var (prodName, prod) = await NewDatabaseAsync("g304b_prod");
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g304b-promo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = NoProject(directory);
            await PromoteAsync(project, dev, qa);
            await PromoteAsync(project, qa, prod);
            await AssertCleanAsync(project, dev, qa);
            await AssertCleanAsync(project, qa, prod);
            await AssertCleanAsync(project, dev, prod);

            // A change in dev travels the same way, in place, with the data kept.
            await ExecuteAsync(prod, "INSERT INTO customer VALUES (1, 'a@example.com');");
            await ExecuteAsync(dev, "ALTER TABLE customer ADD COLUMN phone text; CREATE INDEX customer_email ON customer (email);");
            var drift = await RunAsync("diff", "--project", project, "--source-connection", dev, "--target-connection", qa, "--exit-code");
            Assert.AreEqual(DiffCommand.DifferencesExitCode, drift.ExitCode, drift.Output);
            await PromoteAsync(project, dev, qa);
            await PromoteAsync(project, qa, prod);
            await AssertCleanAsync(project, dev, prod);
            Assert.AreEqual("1", await ScalarTextAsync(prod, "SELECT count(*)::text FROM customer"));
        }
        finally
        {
            await DropDatabaseAsync(devName);
            await DropDatabaseAsync(qaName);
            await DropDatabaseAsync(prodName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task WhatOnlyTheTargetHasIsLabelledAndNotDroppedWithoutPermission()
    {
        var (sourceName, source) = await NewDatabaseAsync("g304b_keep_src", "CREATE TABLE kept (id int PRIMARY KEY);");
        var (targetName, target) = await NewDatabaseAsync("g304b_keep_tgt", "CREATE TABLE kept (id int PRIMARY KEY); CREATE TABLE only_in_prod (id int PRIMARY KEY); INSERT INTO only_in_prod VALUES (1);");
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g304b-keep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = NoProject(directory);
            var diffed = await RunAsync("diff", "--project", project, "--source-connection", source, "--target-connection", target);
            Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
            StringAssert.Contains(diffed.Output, "only in the target");
            StringAssert.Contains(diffed.Output, "only_in_prod");
            StringAssert.Contains(diffed.Output, "- Table public.only_in_prod");

            var refused = await RunAsync("publish", "--project", project, "--source-connection", source, "--target-connection", target, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "--allow-destructive");
            Assert.AreEqual("1", await ScalarTextAsync(target, "SELECT count(*)::text FROM only_in_prod"));

            var allowed = await RunAsync("publish", "--project", project, "--source-connection", source, "--target-connection", target, "--execute", "--allow-destructive");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.AreEqual(string.Empty, await ScalarTextAsync(target, "SELECT coalesce(to_regclass('public.only_in_prod')::text, '')"));
        }
        finally
        {
            await DropDatabaseAsync(sourceName);
            await DropDatabaseAsync(targetName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task TheProjectsScriptsRunAgainstTheTargetAndItsSchemasScopeBothEnds()
    {
        var (sourceName, source) = await NewDatabaseAsync("g304b_scr_src", "CREATE TABLE marker (n int); CREATE SCHEMA other; CREATE TABLE other.x (id int);");
        var (targetName, target) = await NewDatabaseAsync("g304b_scr_tgt");
        var projectDirectory = CreateProjectDirectory("g304b-scripts", """, "schemas": ["public"], "postdeploy": "./post.sql", "excludeOwners": true""");
        try
        {
            File.WriteAllText(Path.Combine(projectDirectory, "post.sql"), "INSERT INTO marker VALUES (42);");
            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--source-connection", source, "--target-connection", target, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            Assert.AreEqual("42", await ScalarTextAsync(target, "SELECT n::text FROM marker"), "the post-deploy script ran on the target");
            Assert.AreEqual("0", await ScalarTextAsync(source, "SELECT count(*)::text FROM marker"), "and not on the source");
            Assert.AreEqual(string.Empty, await ScalarTextAsync(target, "SELECT coalesce(to_regclass('other.x')::text, '')"), "a schema outside the project's schemas is not part of the desired state");
        }
        finally
        {
            await DropDatabaseAsync(sourceName);
            await DropDatabaseAsync(targetName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task OnlyTheOptionOnTheCommandLineTurnsTheModeOn()
    {
        var (sourceName, source) = await NewDatabaseAsync("g304b_mode_src", "CREATE TABLE in_the_source (id int);");
        var (targetName, target) = await NewDatabaseAsync("g304b_mode_tgt");
        var projectDirectory = CreateProjectDirectory("g304b-mode", $$""", "sourceConnectionString": "{{source}}" """);
        try
        {
            WriteSchema(projectDirectory, "CREATE TABLE in_the_files (id int);");

            // The variable and the project file's sourceConnectionString name a source, but the project's own files are still the desired state.
            var previous = Environment.GetEnvironmentVariable(ConnectionSettings.SourceVariable);
            Environment.SetEnvironmentVariable(ConnectionSettings.SourceVariable, source);
            try
            {
                var diffed = await RunAsync("diff", "--project", ProjectFile(projectDirectory), "--target-connection", target);
                Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
                StringAssert.Contains(diffed.Output, "in_the_files");
                Assert.DoesNotContain("in_the_source", diffed.Output, StringComparison.Ordinal);
                Assert.DoesNotContain("-> Target", diffed.Output, StringComparison.Ordinal);

                // A bare option reads the variable.
                var bare = await RunAsync("diff", "--project", ProjectFile(projectDirectory), "--source-connection", "--target-connection", target);
                Assert.AreEqual(0, bare.ExitCode, bare.Output);
                StringAssert.Contains(bare.Output, "in_the_source");
                Assert.DoesNotContain("in_the_files", bare.Output, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(ConnectionSettings.SourceVariable, previous);
            }

            Environment.SetEnvironmentVariable(ConnectionSettings.SourceVariable, null);
            var none = await RunAsync("diff", "--project", ProjectFile(projectDirectory), "--source-connection", "--target-connection", target);
            Assert.AreEqual(1, none.ExitCode, none.Output);
            StringAssert.Contains(none.Output, "source connection string is required");
        }
        finally
        {
            await DropDatabaseAsync(sourceName);
            await DropDatabaseAsync(targetName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task TheJsonNamesTheTwoDatabasesAndAProjectWithTenantsIsRefused()
    {
        var (sourceName, source) = await NewDatabaseAsync("g304b_json_src", "CREATE TABLE t (id int);");
        var (targetName, target) = await NewDatabaseAsync("g304b_json_tgt");
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g304b-json-" + Guid.NewGuid().ToString("N"));
        var tenantProject = CreateProjectDirectory("g304b-tenants", """, "tenants": { "template": "./sql/Tenant", "prefix": "shop" }""");
        try
        {
            Directory.CreateDirectory(Path.Combine(tenantProject, "sql", "Tenant"));
            var run = await RunAsync("diff", "--project", NoProject(directory), "--source-connection", source, "--target-connection", target, "--format", "json");
            Assert.AreEqual(0, run.ExitCode, run.Output);
            using var document = JsonDocument.Parse(run.Output[run.Output.IndexOf('{', StringComparison.Ordinal)..]);
            Assert.AreEqual(sourceName, document.RootElement.GetProperty("source").GetProperty("database").GetString());
            Assert.AreEqual(targetName, document.RootElement.GetProperty("target").GetProperty("database").GetString());
            Assert.AreEqual(1, document.RootElement.GetProperty("summary").GetProperty("added").GetInt32() > 0 ? 1 : 0);

            foreach (var command in new[] { "diff", "publish" })
            {
                var refused = await RunAsync(command, "--project", ProjectFile(tenantProject), "--source-connection", source, "--target-connection", target);
                Assert.AreEqual(1, refused.ExitCode, $"{command}: {refused.Output}");
                StringAssert.Contains(refused.Output, "tenants");
            }
        }
        finally
        {
            await DropDatabaseAsync(sourceName);
            await DropDatabaseAsync(targetName);
            TryDeleteDirectory(directory);
            TryDeleteDirectory(tenantProject);
        }
    }

    private static async Task PromoteAsync(string project, string source, string target)
    {
        var published = await RunAsync("publish", "--project", project, "--source-connection", source, "--target-connection", target, "--execute", "--allow-destructive");
        Assert.AreEqual(0, published.ExitCode, published.Output);
    }

    private static async Task AssertCleanAsync(string project, string source, string target)
    {
        var parity = await RunAsync("diff", "--project", project, "--source-connection", source, "--target-connection", target, "--exit-code");
        Assert.AreEqual(0, parity.ExitCode, parity.Output);
    }
}
