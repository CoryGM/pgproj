using PgProj.Core;
using PgProj.Diff;
using PgProj.Engine;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-309c against a real server: <see cref="PgProjEngine.PublishAsync"/> called as a library. A plan runs nothing and carries the script and the warnings; executing
/// plans again and runs what it planned; a refusal and a failed statement are results (nothing ran, or the transaction was rolled back); a project with tenants is
/// planned and published in parts; <c>--comments-only</c> and <c>--concurrently</c> work through the same call.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PublishAsyncEndToEndTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task APlanRunsNothingAndExecutePublishesWhatWasPlanned()
    {
        var databaseName = await CreateDatabaseAsync("g309c");
        var directory = CreateProjectDirectory("g309c");
        try
        {
            var connection = ConnectionStringFor(databaseName);
            WriteSchema(directory, "CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY);");
            var engine = new PgProjEngine();

            var plan = await engine.PublishAsync(Request(directory, connection), TestContext.CancellationToken);

            Assert.AreEqual(PublishStatus.Planned, plan.Status, plan.Message);
            Assert.IsNull(plan.Execution);
            var part = plan.Parts.Single();
            Assert.IsNull(part.Label);
            StringAssert.Contains(part.Script, "CREATE TABLE");
            Assert.AreEqual(part.Script, plan.Script, "no lock timeout, so the script as written is the part's");
            CollectionAssert.AreEqual(new[] { PublishNoticeKind.NotTransactional }, plan.Warnings.Select(static warning => warning.Kind).ToArray());
            StringAssert.StartsWith(plan.Warnings.Single().Text, "Warning: the script is not wrapped in a transaction;");
            Assert.IsTrue(plan.Warnings.Single().ForKeptScript, "a shell that runs the script and keeps none leaves it out");
            Assert.IsEmpty(plan.Refusals);
            Assert.IsGreaterThan(0, plan.Compare.AddedCount, "the project differs from the empty database");
            Assert.AreEqual(string.Empty, await ScalarTextAsync(connection, "select coalesce(to_regclass('s.t')::text, '')"), "a plan runs nothing");

            var events = new List<EngineEvent>();
            var planned = new List<PublishResult>();
            var published = await engine.PublishAsync(
                Request(directory, connection) with
                {
                    Mode = PublishMode.Execute,
                    Progress = new Collect(events),
                    AfterPlan = (result, _) =>
                    {
                        planned.Add(result);
                        return Task.CompletedTask;
                    },
                },
                TestContext.CancellationToken);

            Assert.AreEqual(PublishStatus.Published, published.Status, published.Message);
            Assert.AreEqual("s.t", await ScalarTextAsync(connection, "select coalesce(to_regclass('s.t')::text, '')"));
            Assert.AreEqual(1, planned.Count, "the hook was called once, before anything ran");
            Assert.AreEqual(PublishStatus.Planned, planned[0].Status);
            Assert.AreEqual(part.Script, planned[0].Parts.Single().Script);
            CollectionAssert.Contains(events.Select(static e => e.Kind).ToArray(), EngineEventKind.ScriptExecuted);
            Assert.AreEqual("Executed deployment script.", events.Single(static e => e.Kind == EngineEventKind.ScriptExecuted).Message);
            Assert.IsNotNull(published.Execution);
            Assert.IsNull(published.Execution.Failure);

            var again = await engine.PublishAsync(Request(directory, connection), TestContext.CancellationToken);
            Assert.AreEqual(PublishStatus.Planned, again.Status);
            Assert.IsFalse(again.Parts.Single().HasStatements, "a second publish finds nothing to do");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ARefusalIsAResultAndNothingRuns()
    {
        var databaseName = await CreateDatabaseAsync("g309c_ref");
        var directory = CreateProjectDirectory("g309c_ref");
        try
        {
            var connection = ConnectionStringFor(databaseName);
            await ExecuteAsync(connection, "CREATE SCHEMA s; CREATE TABLE s.t (id int); CREATE TABLE s.gone (id int); INSERT INTO s.gone VALUES (1);");
            WriteSchema(directory, "CREATE SCHEMA s; CREATE TABLE s.t (id int, note text);");
            var engine = new PgProjEngine();

            var refused = await engine.PublishAsync(Request(directory, connection) with { Mode = PublishMode.Execute }, TestContext.CancellationToken);

            Assert.AreEqual(PublishStatus.Refused, refused.Status);
            var refusal = refused.Refusals.Single();
            Assert.AreEqual(PublishNoticeKind.Destructive, refusal.Kind);
            StringAssert.StartsWith(refusal.Text, "Publish failed: 1 operation(s) can discard data and were not executed.");
            StringAssert.Contains(refusal.Text, "pass --allow-destructive to run it anyway.");
            var item = refusal.Items.Single();
            StringAssert.StartsWith(item.Text, "[Table] s.gone:");
            Assert.AreEqual(PublishNoticeKind.Destructive, item.Kind);
            Assert.AreEqual(ChangeNodeKind.Table, item.NodeKind);
            Assert.AreEqual("s.gone", item.NodeId);
            Assert.IsNotNull(item.Detail);
            Assert.AreEqual("s.gone", await ScalarTextAsync(connection, "select to_regclass('s.gone')::text"), "nothing ran");
            Assert.AreEqual(string.Empty, await ScalarTextAsync(connection, "select coalesce((select column_name from information_schema.columns where table_schema = 's' and table_name = 't' and column_name = 'note'), '')"));

            var allowed = await engine.PublishAsync(Request(directory, connection) with { Mode = PublishMode.Execute, AllowDestructive = true }, TestContext.CancellationToken);
            Assert.AreEqual(PublishStatus.Published, allowed.Status, allowed.Message);
            Assert.AreEqual(string.Empty, await ScalarTextAsync(connection, "select coalesce(to_regclass('s.gone')::text, '')"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task AFailedStatementIsAResultAndTheTransactionIsRolledBack()
    {
        var databaseName = await CreateDatabaseAsync("g309c_fail");
        var directory = CreateProjectDirectory("g309c_fail");
        try
        {
            var connection = ConnectionStringFor(databaseName);
            await ExecuteAsync(connection, "CREATE SCHEMA s; CREATE TABLE s.t (id int); INSERT INTO s.t VALUES (1);");
            WriteSchema(directory, "CREATE SCHEMA s; CREATE TABLE s.t (id int, must_have text NOT NULL); CREATE TABLE s.extra (id int);");

            var result = await new PgProjEngine().PublishAsync(Request(directory, connection) with { Mode = PublishMode.Execute }, TestContext.CancellationToken);

            Assert.AreEqual(PublishStatus.ExecutionFailed, result.Status);
            var failure = result.Execution!.Failure!;
            Assert.AreEqual("23502", failure.SqlState);
            Assert.IsFalse(failure.AfterCommit);
            StringAssert.StartsWith(failure.Message, "23502:");
            StringAssert.Contains(failure.Message, "Nothing was applied: the script was rolled back.");
            StringAssert.Contains(failure.Statement, "must_have");
            Assert.AreEqual(failure.Message, result.Message);
            Assert.AreEqual(string.Empty, await ScalarTextAsync(connection, "select coalesce(to_regclass('s.extra')::text, '')"), "the whole script was rolled back");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task TheWarningsOfAPlanAreTypedAndWordedAsPublishWordsThem()
    {
        var databaseName = await CreateDatabaseAsync("g309c_warn");
        var directory = CreateProjectDirectory("g309c_warn");
        try
        {
            var connection = ConnectionStringFor(databaseName);
            await ExecuteAsync(connection, "CREATE SCHEMA s; CREATE TABLE s.t (id int);");
            WriteSchema(directory, "CREATE SCHEMA s; CREATE TABLE s.t (id int NOT NULL); GRANT USAGE ON SCHEMA s TO pgproj_g309c_missing_role;");

            var events = new List<EngineEvent>();
            var plan = await new PgProjEngine().PublishAsync(Request(directory, connection) with { Progress = new Collect(events) }, TestContext.CancellationToken);
            var warningEvents = events.Where(static e => e.Kind == EngineEventKind.Notice).ToArray();

            Assert.AreEqual(PublishStatus.Planned, plan.Status, plan.Message);
            var kinds = plan.Warnings.Select(static warning => warning.Kind).ToArray();
            CollectionAssert.AreEqual(
                new[] { PublishNoticeKind.LockRewrite, PublishNoticeKind.NotTransactional, PublishNoticeKind.MissingRole },
                kinds,
                "in the order publish prints them: " + string.Join(", ", kinds));
            StringAssert.Contains(plan.Warnings[0].Text, "SET NOT NULL");
            var locked = plan.Warnings[0].Items.Single();
            Assert.AreEqual(ChangeNodeKind.Table, locked.NodeKind);
            Assert.AreEqual("s.t", locked.NodeId);
            StringAssert.Contains(locked.Detail, "SET NOT NULL");
            StringAssert.Contains(plan.Warnings[2].Text, "role \"pgproj_g309c_missing_role\"");
            CollectionAssert.AreEqual(new[] { "pgproj_g309c_missing_role" }, plan.Warnings[2].Items.Select(static item => item.NodeId).ToArray());
            CollectionAssert.AreEqual(
                plan.Warnings.Select(static warning => warning.Text).ToArray(),
                warningEvents.Select(static e => e.Message).ToArray(),
                "each warning is reported as an event as it is found, in the same order");
            CollectionAssert.AreEqual(
                new[] { "LockRewrite", "Compared", "ScriptPlanned", "NotTransactional", "MissingRole" },
                events.Where(static e => e.Kind is EngineEventKind.Notice or EngineEventKind.Compared or EngineEventKind.ScriptPlanned)
                    .Select(static e => e.Notice?.Kind.ToString() ?? e.Kind.ToString())
                    .ToArray(),
                "the script is reported where publish has always shown it: after the warnings about the changes, before those about the script");
            Assert.AreEqual(plan.Script, events.Single(static e => e.Kind == EngineEventKind.ScriptPlanned).Script);

            var refused = await new PgProjEngine().PublishAsync(Request(directory, connection) with { Mode = PublishMode.Execute }, TestContext.CancellationToken);
            Assert.AreEqual(PublishStatus.Refused, refused.Status);
            Assert.AreEqual(PublishNoticeKind.MissingRole, refused.Refusals.Single().Kind);
            StringAssert.StartsWith(refused.Refusals.Single().Text, "Publish failed: the project's grants and owners name roles the server does not have, so the script was not run:");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ACommentsOnlyPublishDeploysTheCommentsAndReportsTheOnesItSkipped()
    {
        var databaseName = await CreateDatabaseAsync("g309c_cmt");
        var directory = CreateProjectDirectory("g309c_cmt");
        try
        {
            var connection = ConnectionStringFor(databaseName);
            await ExecuteAsync(connection, "CREATE SCHEMA s; CREATE TABLE s.t (id int);");
            WriteSchema(directory, "CREATE SCHEMA s; CREATE TABLE s.t (id int, note text); COMMENT ON TABLE s.t IS 'orders'; CREATE TABLE s.u (id int); COMMENT ON TABLE s.u IS 'new';");
            var events = new List<EngineEvent>();

            var result = await new PgProjEngine().PublishAsync(
                Request(directory, connection) with { Mode = PublishMode.Execute, CommentsOnly = true, Progress = new Collect(events) },
                TestContext.CancellationToken);

            Assert.AreEqual(PublishStatus.Published, result.Status, result.Message);
            Assert.AreEqual("orders", await ScalarTextAsync(connection, "select obj_description('s.t'::regclass)"));
            Assert.AreEqual(string.Empty, await ScalarTextAsync(connection, "select coalesce(to_regclass('s.u')::text, '')"), "only comments are deployed");
            Assert.AreEqual(string.Empty, await ScalarTextAsync(connection, "select coalesce((select column_name from information_schema.columns where table_schema = 's' and table_name = 't' and column_name = 'note'), '')"));
            Assert.IsTrue(events.Any(static e => e.Kind == EngineEventKind.CommentsOnlyPlanned && e.Level == EngineEventLevel.Information));
            var skipped = events.Single(static e => e.Notice?.Kind == PublishNoticeKind.ServerNotice);
            Assert.AreEqual(EngineEventLevel.Warning, skipped.Level);
            StringAssert.StartsWith(skipped.Message, "Warning: pgproj publish --comments-only skipped");
            Assert.AreEqual(PublishNoticeKind.ServerNotice, result.Warnings.Last().Kind);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ConcurrentlyBuildsTheIndexAfterTheCommitAndReportsEachStep()
    {
        var databaseName = await CreateDatabaseAsync("g309c_conc");
        var directory = CreateProjectDirectory("g309c_conc");
        try
        {
            var connection = ConnectionStringFor(databaseName);
            await ExecuteAsync(connection, "CREATE SCHEMA s; CREATE TABLE s.t (id int, v int);");
            WriteSchema(directory, "CREATE SCHEMA s; CREATE TABLE s.t (id int, v int); CREATE INDEX t_v_idx ON s.t (v);");
            var events = new List<EngineEvent>();

            var plan = await new PgProjEngine().PublishAsync(Request(directory, connection) with { Concurrently = true }, TestContext.CancellationToken);
            Assert.AreEqual(1, plan.Parts.Single().AfterCommit.Count);
            StringAssert.Contains(plan.Script, "CREATE INDEX CONCURRENTLY");
            Assert.AreEqual(PublishNoticeKind.NotTransactional, plan.Warnings.Single().Kind);
            StringAssert.Contains(plan.Warnings.Single().Text, "Run the part above the line", "the concurrent script has its own warning");

            var result = await new PgProjEngine().PublishAsync(
                Request(directory, connection) with { Mode = PublishMode.Execute, Concurrently = true, Progress = new Collect(events) },
                TestContext.CancellationToken);

            Assert.AreEqual(PublishStatus.Published, result.Status, result.Message);
            Assert.AreEqual("t", await ScalarTextAsync(connection, "select indisvalid::text::char from pg_index where indexrelid = 's.t_v_idx'::regclass"));
            CollectionAssert.AreEqual(
                new[] { EngineEventKind.ScriptExecuted, EngineEventKind.AfterCommitStarted, EngineEventKind.AfterCommitStep, EngineEventKind.AfterCommitDone },
                events.Where(static e => e.Level == EngineEventLevel.Information).Select(static e => e.Kind).ToArray());
            Assert.AreEqual("Running 1 statement(s) outside the transaction (--concurrently):", events.Single(static e => e.Kind == EngineEventKind.AfterCommitStarted).Message);
            StringAssert.StartsWith(events.Single(static e => e.Kind == EngineEventKind.AfterCommitStep).Message, "  [1/1] ");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task AProjectWithTenantsIsPlannedAndPublishedInParts()
    {
        var databaseName = await CreateDatabaseAsync("g309c_ten");
        var directory = CreateProjectDirectory("g309c_ten", """, "searchPath": ["platform", "public"], "tenants": { "template": "./sql/Tenant", "prefix": "tenant", "file": "./tenants.csv" }""");
        try
        {
            var connection = ConnectionStringFor(databaseName);
            Directory.CreateDirectory(Path.Combine(directory, "sql", "Tenant"));
            File.WriteAllText(Path.Combine(directory, "sql", "platform.sql"), "CREATE SCHEMA platform; CREATE TABLE platform.currencies (code char(3) PRIMARY KEY);");
            File.WriteAllText(Path.Combine(directory, "sql", "Tenant", "orders.sql"), "CREATE TABLE orders (id bigint PRIMARY KEY, currency char(3) REFERENCES platform.currencies (code));");
            File.WriteAllText(Path.Combine(directory, "tenants.csv"), "tenant\nacme\nglobex\n");
            var engine = new PgProjEngine();

            var plan = await engine.PublishAsync(Request(directory, connection), TestContext.CancellationToken);

            Assert.AreEqual(PublishStatus.Planned, plan.Status, plan.Message);
            CollectionAssert.AreEqual(new[] { "shared schemas", "tenant_acme", "tenant_globex" }, plan.Parts.Select(static part => part.Label).ToArray());
            StringAssert.Contains(plan.Script, "-- pgproj: tenant tenant_acme (a transaction of its own)");
            StringAssert.Contains(plan.Script, "CREATE SCHEMA IF NOT EXISTS \"tenant_globex\"");
            Assert.AreEqual(PublishNoticeKind.NotTransactional, plan.Warnings.Single().Kind);
            Assert.AreEqual(2, plan.Compare.Tenants!.Tenants.Count);
            Assert.AreEqual(string.Empty, await ScalarTextAsync(connection, "select coalesce(to_regnamespace('tenant_acme')::text, '')"), "a plan runs nothing");

            var events = new List<EngineEvent>();
            var result = await engine.PublishAsync(Request(directory, connection) with { Mode = PublishMode.Execute, Progress = new Collect(events) }, TestContext.CancellationToken);

            Assert.AreEqual(PublishStatus.Published, result.Status, result.Message);
            CollectionAssert.AreEqual(new[] { "the shared schemas", "tenant_acme", "tenant_globex" }, result.Execution!.Published.ToArray());
            Assert.AreEqual("tenant_acme.orders", await ScalarTextAsync(connection, "select to_regclass('tenant_acme.orders')::text"));
            Assert.AreEqual("Published: the shared schemas and 2 tenant(s), 2 of them with changes.", events.Single(static e => e.Kind == EngineEventKind.PublishFinished).Message);

            var again = await engine.PublishAsync(Request(directory, connection), TestContext.CancellationToken);
            Assert.IsTrue(again.Parts.All(static part => !part.HasStatements), "a second publish finds nothing to do");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    private static PublishRequest Request(string directory, string connection)
        => new() { ProjectFilePath = ProjectFile(directory), TargetConnectionString = connection };

    private sealed class Collect(List<EngineEvent> events) : IProgress<EngineEvent>
    {
        public void Report(EngineEvent value) => events.Add(value);
    }
}
