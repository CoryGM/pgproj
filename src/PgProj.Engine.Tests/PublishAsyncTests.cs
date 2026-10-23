using PgProj.Core;
using PgProj.Dependencies;
using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-309c: <see cref="PgProjEngine.PublishAsync"/> and what it plans, as far as they can be checked without a server: the statuses a user can cause, the hooks, the
/// words of the warnings and refusals, and a part planned from in-memory models (its script, <c>--comments-only</c>, <c>--concurrently</c>, a tenant's search path).
/// The publish against a database (plan, run, refusals, failed statements, tenants) is in the Integration tests.
/// </summary>
[TestClass]
public sealed class PublishAsyncTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ARequestNeedsTheProjectNamedOnce()
    {
        var engine = new PgProjEngine();

        await Assert.ThrowsAsync<ArgumentException>(() => engine.PublishAsync(new PublishRequest(), TestContext.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.PublishAsync(
            new PublishRequest { ProjectFilePath = "pgproj.json", Settings = new ProjectSettings { Name = "x", Version = "1", Source = "./sql" }, RootDirectory = "." },
            TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task OptionsThatCannotBeUsedTogetherAreRefusedBeforeTheProjectIsRead()
    {
        var fixture = new ProjectFixture();
        var engine = new PgProjEngine();

        var both = await engine.PublishAsync(
            new PublishRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, Concurrently = true, CommentsOnly = true },
            TestContext.CancellationToken);
        var timeout = await engine.PublishAsync(
            new PublishRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, LockTimeout = "soon" },
            TestContext.CancellationToken);

        Assert.AreEqual(PublishStatus.Refused, both.Status);
        Assert.AreEqual("--concurrently and --comments-only cannot be used together (a comments-only script builds nothing).", both.Message);
        Assert.AreEqual(PublishStatus.Refused, timeout.Status);
        Assert.AreEqual("--lock-timeout takes a duration such as 5s, 500ms, or 2min, not 'soon'.", timeout.Message);
        Assert.IsEmpty(both.Compare.Documents, "nothing was read");
    }

    [TestMethod]
    public async Task AMissingProjectFileIsAStatus()
    {
        var fixture = new ProjectFixture();

        var result = await new PgProjEngine().PublishAsync(new PublishRequest { Files = new InMemoryFileSource(), ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);

        Assert.AreEqual(PublishStatus.ProjectNotFound, result.Status);
        Assert.AreEqual(fixture.ProjectFile, result.Compare.ProjectFilePath);
    }

    [TestMethod]
    public async Task AnInvalidProjectFileIsAStatusWithTheLoadersMessage()
    {
        var path = Path.Combine(Path.GetTempPath(), "pgproj-publish", "pgproj.json");

        var result = await new PgProjEngine().PublishAsync(
            new PublishRequest { Files = new InMemoryFileSource().Set(path, "{ not json"), ProjectFilePath = path },
            TestContext.CancellationToken);

        Assert.AreEqual(PublishStatus.ProjectInvalid, result.Status);
        StringAssert.Contains(result.Message, "contains invalid JSON");
    }

    [TestMethod]
    public async Task AProjectWithErrorsFailsBeforeAnyDatabaseIsTouched()
    {
        var fixture = new ProjectFixture().With(Path.Combine("sql", "Schema", "broken.sql"), "CREATE TABLE b (a int, ;\n");
        BuildResult? seen = null;

        var result = await new PgProjEngine().PublishAsync(
            new PublishRequest
            {
                Files = fixture.InMemory(),
                ProjectFilePath = fixture.ProjectFile,
                TargetConnectionString = "Host=127.0.0.1;Port=1",
                AfterBuild = (build, _) =>
                {
                    seen = build;
                    return Task.CompletedTask;
                },
            },
            TestContext.CancellationToken);

        Assert.AreEqual(PublishStatus.Failed, result.Status);
        Assert.AreEqual(1, result.Compare.ErrorCount);
        Assert.IsNull(result.Message);
        Assert.IsEmpty(result.Parts);
        Assert.AreEqual(BuildStatus.Failed, seen!.Status);
    }

    [TestMethod]
    public async Task TenantsOnAProjectWithoutTenantsAreRefusedAndSoIsAMissingConnectionString()
    {
        var fixture = new ProjectFixture();
        var engine = new PgProjEngine();

        var tenants = await engine.PublishAsync(
            new PublishRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, Tenants = ["acme"], TargetConnectionString = "Host=127.0.0.1;Port=1" },
            TestContext.CancellationToken);
        var noTarget = await engine.PublishAsync(new PublishRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);

        Assert.AreEqual(PublishStatus.Refused, tenants.Status);
        StringAssert.Contains(tenants.Message, "--tenants is only for a project with tenants");
        Assert.AreEqual(PublishStatus.Refused, noTarget.Status);
        Assert.IsNotNull(noTarget.Message);
    }

    [TestMethod]
    public async Task AServerThatCannotBeReachedIsConnectionFailed()
    {
        var fixture = new ProjectFixture();

        var result = await new PgProjEngine().PublishAsync(
            new PublishRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, TargetConnectionString = "Host=127.0.0.1;Port=1;Timeout=2;Pooling=false" },
            TestContext.CancellationToken);

        Assert.AreEqual(PublishStatus.ConnectionFailed, result.Status);
        Assert.IsNotNull(result.Message);
        Assert.AreEqual("fixture", result.Compare.ProjectName, "the build part says what was read before the connection failed");
    }

    [TestMethod]
    public void TheWordsOfTheChecksAreWhatPublishHasAlwaysSaid()
    {
        StringAssert.StartsWith(PublishTexts.ScriptNotTransactionalWarning, "Warning: the script is not wrapped in a transaction;");
        StringAssert.Contains(PublishTexts.ConcurrentScriptWarning, ConcurrentPublishPlan.Marker);
        StringAssert.Contains(PublishTexts.TenantScriptWarning, "-- pgproj: tenant");

        var features = new[] { new ServerFeature(99, "a thing") };
        StringAssert.StartsWith(PublishTexts.DescribeNewerThanServer(features, 16, executing: false), "Warning: the project uses features the server (PostgreSQL 16) does not have");
        StringAssert.StartsWith(PublishTexts.DescribeNewerThanServer(features, 16, executing: true), "the project uses features the server (PostgreSQL 16) does not have, so the script was not run:");
        StringAssert.Contains(PublishTexts.DescribeNewerThanServer(features, 16, executing: true), "a thing (needs PostgreSQL 99)");

        var tablespaces = new[] { new MissingTablespace("fast", ["public.t"]) };
        StringAssert.Contains(PublishTexts.DescribeMissingTablespaces(tablespaces, executing: true), "tablespace \"fast\", named by public.t");
        StringAssert.Contains(PublishTexts.DescribeMissingTablespaces(tablespaces, executing: false), "pgproj does not create tablespaces");

        var roles = new[] { new MissingRole("app", ["public.t"]) };
        StringAssert.Contains(PublishTexts.DescribeMissingRoles(roles, executing: true), "role \"app\", named by public.t");
        StringAssert.Contains(PublishTexts.DescribeMissingRoles(roles, executing: false), "pgproj does not create roles");

        StringAssert.Contains(PublishTexts.DescribeFormerTenants(["tenant_old"], forPublish: true), "tenant_old is no longer a tenant");
    }

    [TestMethod]
    [DataRow("5s", true)]
    [DataRow("500ms", true)]
    [DataRow("2min", true)]
    [DataRow("1.5 s", true)]
    [DataRow("100", true)]
    [DataRow("soon", false)]
    [DataRow("5 weeks", false)]
    [DataRow("", false)]
    public void ALockTimeoutIsADurationPostgreSqlAccepts(string value, bool valid)
        => Assert.AreEqual(valid, PublishTexts.IsDuration(value));

    [TestMethod]
    public void APartIsPlannedFromModelsWithItsScriptAndNoAfterCommitStepsByDefault()
    {
        var project = Project();
        var wanted = Build("CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY);");

        var unit = Plan(project, DatabaseModel.Empty, wanted, label: null, commentsOnly: false, concurrently: false);

        StringAssert.Contains(unit.Script, "CREATE TABLE");
        Assert.AreEqual(unit.TransactionalScript, unit.Script);
        Assert.IsEmpty(unit.AfterCommit);
        Assert.IsEmpty(unit.SuperuserStatements);
        Assert.IsTrue(unit.HasStatements);
        Assert.IsNull(unit.Label);
    }

    [TestMethod]
    public void NothingToDoIsAPartWithNoStatements()
    {
        var wanted = Build("CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY);");

        var unit = Plan(Project(), wanted, wanted, label: null, commentsOnly: false, concurrently: false);

        Assert.IsFalse(unit.HasStatements);
    }

    [TestMethod]
    public void CommentsOnlyKeepsTheCommentsAndSaysWhatItLeftOut()
    {
        var live = Build("CREATE SCHEMA s; CREATE TABLE s.t (id int);");
        var wanted = Build("CREATE SCHEMA s; CREATE TABLE s.t (id int, note text); COMMENT ON TABLE s.t IS 'orders';");
        var events = new List<EngineEvent>();

        var unit = Plan(Project(), live, wanted, label: null, commentsOnly: true, concurrently: false, new Collect(events));

        StringAssert.Contains(unit.Script, "COMMENT ON TABLE");
        Assert.DoesNotContain("ADD COLUMN", unit.Script, "a column is not a comment");
        var said = events.Single(static e => e.Kind == EngineEventKind.CommentsOnlyPlanned);
        Assert.AreEqual(EngineEventLevel.Information, said.Level);
        StringAssert.StartsWith(said.Message, "--comments-only: deploying 1 COMMENT ON statement(s);");
    }

    [TestMethod]
    public void ConcurrentlyMovesANewIndexOnAnExistingTableAfterTheCommit()
    {
        var live = Build("CREATE SCHEMA s; CREATE TABLE s.t (id int, v int);");
        var wanted = Build("CREATE SCHEMA s; CREATE TABLE s.t (id int, v int); CREATE INDEX t_v_idx ON s.t (v);");

        var unit = Plan(Project(), live, wanted, label: null, commentsOnly: false, concurrently: true);

        var step = unit.AfterCommit.Single();
        StringAssert.Contains(step.Sql, "CREATE INDEX CONCURRENTLY");
        Assert.DoesNotContain("t_v_idx", unit.TransactionalScript, "the transaction does not build it");
        StringAssert.Contains(unit.Script, ConcurrentPublishPlan.Marker);
        StringAssert.EndsWith(unit.Script, Environment.NewLine);
    }

    [TestMethod]
    public void ATenantsPartIsHeadedByItsSearchPath()
    {
        var wanted = Build("CREATE SCHEMA tenant_acme; CREATE TABLE tenant_acme.t (id int);");

        var unit = Plan(Project(), DatabaseModel.Empty, wanted, label: "tenant_acme", commentsOnly: false, concurrently: false, searchPath: ["tenant_acme", "public"]);

        StringAssert.StartsWith(unit.TransactionalScript, "SET search_path TO tenant_acme, public;");
        Assert.AreEqual("tenant_acme", unit.Label);
    }

    [TestMethod]
    public void AnEventTriggerNeedsASuperuserAndTheScriptSaysSo()
    {
        var wanted = Build("CREATE FUNCTION f() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN END $$; CREATE EVENT TRIGGER e ON ddl_command_start EXECUTE FUNCTION f();");

        var unit = Plan(Project(), DatabaseModel.Empty, wanted, label: null, commentsOnly: false, concurrently: false);

        Assert.IsNotEmpty(unit.SuperuserStatements);
        StringAssert.StartsWith(unit.TransactionalScript, "-- pgproj: this script has statements that only a superuser can run");
    }

    [TestMethod]
    public void ABlockedChangeIsAnItemOfItsOwnKindInTheUnsupportedRefusal()
    {
        var blocked = new BlockedOperation { NodeId = "public.c", NodeKind = ChangeNodeKind.Collation, Message = "collation public.c is used by public.t.a" };
        var placeholder = new Placeholder { NodeId = "public.f", NodeKind = ChangeNodeKind.Function };

        var mixed = PublishItems.Unsupported([("shared schemas", blocked), (null, placeholder)]);
        var onlyBlocked = PublishItems.Unsupported([(null, blocked)]);

        Assert.AreEqual(PublishNoticeKind.BlockedChange, mixed[0].Kind);
        Assert.AreEqual(ChangeNodeKind.Collation, mixed[0].NodeKind);
        Assert.AreEqual("public.c", mixed[0].NodeId);
        Assert.AreEqual("collation public.c is used by public.t.a", mixed[0].Detail);
        Assert.AreEqual("shared schemas", mixed[0].Part);
        StringAssert.StartsWith(mixed[0].Text, "shared schemas: [Collation] ");
        Assert.AreEqual(PublishNoticeKind.Unsupported, mixed[1].Kind);
        StringAssert.StartsWith(mixed[1].Text, "[Function] ");
        Assert.AreEqual(PublishNoticeKind.Unsupported, PublishItems.UnsupportedKind(mixed), "a refusal with other placeholders is Unsupported");
        Assert.AreEqual(PublishNoticeKind.BlockedChange, PublishItems.UnsupportedKind(onlyBlocked), "a refusal of blocked changes alone is BlockedChange");
    }

    [TestMethod]
    public void TheItemsOfAWarningNameTheObjects()
    {
        var locks = PublishItems.LockRewrites([new LockWarning("public.t", "SET NOT NULL a", "scans the whole table")], "tenant_acme");
        var notes = PublishItems.BodyNotes([new PlpgsqlBodyNote("procedure", "public.p()", "table 'public.t' is dropped")]);
        var roles = PublishItems.Roles(PublishNoticeKind.MissingRole, [new MissingRole("app", ["public.t", "public.u"])]);
        var features = PublishItems.Features(PublishNoticeKind.NewerThanServer, [new ServerFeature(18, "a virtual generated column")]);

        Assert.AreEqual(new PublishItem(PublishNoticeKind.LockRewrite, "public.t: SET NOT NULL a", ChangeNodeKind.Table, "public.t", "SET NOT NULL a: scans the whole table", "tenant_acme"), locks.Single());
        Assert.AreEqual(ChangeNodeKind.Procedure, notes.Single().NodeKind);
        Assert.AreEqual("public.p()", notes.Single().NodeId);
        Assert.AreEqual("app", roles.Single().NodeId);
        Assert.AreEqual("named by public.t, public.u", roles.Single().Detail);
        Assert.IsNull(features.Single().NodeId);
        Assert.AreEqual("needs PostgreSQL 18", features.Single().Detail);
    }

    private sealed record Placeholder : SqlOperation
    {
        public override string GenerateSql() => "-- TODO: not supported";
    }

    private static PgProject Project()
    {
        var fixture = new ProjectFixture();
        return new PgProjectLoader().Load(fixture.InMemory(), fixture.ProjectFile);
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static PublishUnit Plan(
        PgProject project,
        DatabaseModel live,
        DatabaseModel wanted,
        string? label,
        bool commentsOnly,
        bool concurrently,
        IProgress<EngineEvent>? progress = null,
        IReadOnlyList<string>? searchPath = null)
    {
        var changes = new DiffEngine().Diff(live, wanted);
        var relationNames = concurrently ? new HashSet<(string, string)>() : null;
        return PublishUnit.Build(project, label, wanted, live, changes, (null, null), searchPath, commentsOnly, concurrently, 18, relationNames, [], progress);
    }

    private sealed class Collect(List<EngineEvent> events) : IProgress<EngineEvent>
    {
        public void Report(EngineEvent value) => events.Add(value);
    }
}
