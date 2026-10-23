using PgProj.Core;
using System.Text.Json;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-309b: <see cref="PgProjEngine.CompareAsync"/> and its result, as far as they can be checked without a server: the statuses a user can cause, the hook after
/// the build, the result's JSON document, and the paths of a source database's in-memory project. The comparison with a database is in the Integration tests.
/// </summary>
[TestClass]
public sealed class CompareAsyncTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AMissingProjectFileIsAStatus()
    {
        var fixture = new ProjectFixture();

        var result = await new PgProjEngine().CompareAsync(new CompareRequest { Files = new InMemoryFileSource(), ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);

        Assert.AreEqual(CompareStatus.ProjectNotFound, result.Status);
        Assert.AreEqual(fixture.ProjectFile, result.ProjectFilePath);
    }

    [TestMethod]
    public async Task AnInvalidProjectFileIsAStatusWithTheLoadersMessage()
    {
        var files = new InMemoryFileSource().Set(Path.Combine(Path.GetTempPath(), "pgproj-compare", "pgproj.json"), "{ not json");

        var result = await new PgProjEngine().CompareAsync(new CompareRequest { Files = files, ProjectFilePath = Path.Combine(Path.GetTempPath(), "pgproj-compare", "pgproj.json") }, TestContext.CancellationToken);

        Assert.AreEqual(CompareStatus.ProjectInvalid, result.Status);
        StringAssert.Contains(result.Message, "contains invalid JSON");
    }

    [TestMethod]
    public async Task AProjectWithErrorsFailsBeforeAnyDatabaseIsTouched()
    {
        var fixture = new ProjectFixture().With(Path.Combine("sql", "Schema", "broken.sql"), "CREATE TABLE b (a int, ;\n");

        var result = await new PgProjEngine().CompareAsync(
            new CompareRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, TargetConnectionString = "Host=127.0.0.1;Port=1" },
            TestContext.CancellationToken);

        Assert.AreEqual(CompareStatus.Failed, result.Status);
        Assert.AreEqual(1, result.ErrorCount);
        Assert.AreEqual("fixture", result.ProjectName);
        Assert.IsNull(result.Message);
    }

    [TestMethod]
    public async Task TheHookIsGivenTheBuildBeforeTheDatabaseIsRead()
    {
        var fixture = new ProjectFixture();
        BuildResult? seen = null;

        var result = await new PgProjEngine().CompareAsync(
            new CompareRequest
            {
                Files = fixture.InMemory(),
                ProjectFilePath = fixture.ProjectFile,
                AfterBuild = (build, _) =>
                {
                    seen = build;
                    return Task.CompletedTask;
                },
            },
            TestContext.CancellationToken);

        Assert.AreEqual(CompareStatus.Refused, result.Status, "no target connection string was given");
        Assert.IsNotNull(seen);
        Assert.AreEqual(BuildStatus.Succeeded, seen.Status);
        Assert.AreEqual(2, seen.Documents.Count);
        Assert.IsNotNull(seen.Model);
    }

    [TestMethod]
    public async Task AMissingTargetConnectionStringIsRefusedWithTheWordsTheCliAlwaysUsed()
    {
        var fixture = new ProjectFixture();

        var result = await new PgProjEngine().CompareAsync(new CompareRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);

        Assert.AreEqual(CompareStatus.Refused, result.Status);
        Assert.AreEqual("A PostgreSQL target connection string is required. Specify --target-connection, set PGPROJ_TARGET_CONNECTION, or add targetConnectionString to pgproj.json.", result.Message);
    }

    [TestMethod]
    public async Task AMalformedConnectionStringIsRefusedInWords()
    {
        var fixture = new ProjectFixture();

        var result = await new PgProjEngine().CompareAsync(
            new CompareRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, TargetConnectionString = "Host=localhost;Port=notaport" },
            TestContext.CancellationToken);

        Assert.AreEqual(CompareStatus.Refused, result.Status);
        StringAssert.StartsWith(result.Message, "The connection string is not valid: the value of 'port'");
    }

    [TestMethod]
    public async Task TenantsAreRefusedForAProjectWithoutTenants()
    {
        var fixture = new ProjectFixture();

        var result = await new PgProjEngine().CompareAsync(
            new CompareRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, Tenants = ["acme"], TargetConnectionString = "Host=127.0.0.1;Port=1" },
            TestContext.CancellationToken);

        Assert.AreEqual(CompareStatus.Refused, result.Status);
        Assert.AreEqual("--tenants is only for a project with tenants ('tenants' in pgproj.json).", result.Message);
    }

    [TestMethod]
    public async Task ADatabaseThatCannotBeReachedIsAStatus()
    {
        var fixture = new ProjectFixture();

        var result = await new PgProjEngine().CompareAsync(
            new CompareRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, TargetConnectionString = "Host=127.0.0.1;Port=1;Timeout=3;Pooling=false" },
            TestContext.CancellationToken);

        Assert.AreEqual(CompareStatus.ConnectionFailed, result.Status);
        Assert.IsNotNull(result.Message);
    }

    [TestMethod]
    public async Task ACancelledTokenIsACancelledTask()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var fixture = new ProjectFixture();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new PgProjEngine().CompareAsync(new CompareRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, cancelled.Token));
    }

    [TestMethod]
    public void TheJsonIsOneEnvelopeWithTheBuildPartAndTheComparePart()
    {
        var result = new CompareResult
        {
            Status = CompareStatus.Succeeded,
            ProjectName = "app",
            ProjectFilePath = Path.Combine(Path.GetTempPath(), "pgproj.json"),
            SourcePath = Path.Combine(Path.GetTempPath(), "sql"),
            Documents = [new DocumentInfo(Path.Combine(Path.GetTempPath(), "sql", "a.sql"), 3)],
            Diagnostics = [new EngineDiagnostic(EngineSeverity.Warning, "look", Path.Combine(Path.GetTempPath(), "sql", "a.sql"), 2, 5, 2, 9)],
            WarningCount = 1,
            Changes = Diff("CREATE TABLE public.t (id int);", "CREATE TABLE public.t (id int, name text);"),
            ModifiedCount = 1,
            UndroppedSchemas = ["old"],
        };

        using var json = JsonDocument.Parse(result.ToJson(static path => Path.GetFileName(path)));
        var root = json.RootElement;

        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("succeeded", root.GetProperty("status").GetString());
        Assert.AreEqual("app", root.GetProperty("project").GetProperty("name").GetString());
        Assert.AreEqual("pgproj.json", root.GetProperty("project").GetProperty("file").GetString());
        Assert.AreEqual("a.sql", root.GetProperty("documents")[0].GetProperty("path").GetString());
        Assert.AreEqual(3, root.GetProperty("documents")[0].GetProperty("version").GetInt32());
        var diagnostic = root.GetProperty("diagnostics")[0];
        Assert.AreEqual("Warning", diagnostic.GetProperty("severity").GetString());
        Assert.AreEqual("a.sql", diagnostic.GetProperty("file").GetString());
        Assert.AreEqual(9, diagnostic.GetProperty("endColumn").GetInt32());
        Assert.AreEqual(1, root.GetProperty("counts").GetProperty("warnings").GetInt32());
        Assert.AreEqual(1, root.GetProperty("added").GetArrayLength());
        var modified = root.GetProperty("modified")[0];
        Assert.AreEqual("public.t", modified.GetProperty("id").GetString());
        Assert.AreEqual("Columns", modified.GetProperty("changes")[0].GetProperty("property").GetString());
        Assert.AreEqual(1, root.GetProperty("summary").GetProperty("modified").GetInt32());
        Assert.AreEqual("old", root.GetProperty("undroppedSchemas")[0].GetString());
        Assert.IsFalse(root.TryGetProperty("source", out _), "only source mode has a source and a target");
        Assert.IsFalse(root.TryGetProperty("standInSchema", out _), "only a project with tenants has the tenant blocks");
    }

    [TestMethod]
    public void TheJsonOfAProjectWithTenantsHasTheTenantBlocksAndTheSharedChangesAtTheTop()
    {
        var empty = new ChangeSet { Added = [], Removed = [], Modified = [] };
        var tenants = new TenantsComparison(
            "tenant",
            new TenantList([new TenantTarget("acme", "tenant_acme", true), new TenantTarget("newco", "tenant_newco", false)], ["tenant_old"], null),
            [new TenantDifference(new TenantTarget("acme", "tenant_acme", true), empty), new TenantDifference(new TenantTarget("newco", "tenant_newco", false), empty)]);

        using var json = JsonDocument.Parse(new CompareResult { Status = CompareStatus.Succeeded, Changes = empty, Tenants = tenants }.ToJson());
        var root = json.RootElement;

        Assert.AreEqual("tenant", root.GetProperty("standInSchema").GetString());
        Assert.AreEqual(0, root.GetProperty("tenantGroups").GetArrayLength());
        Assert.AreEqual("tenant_newco", root.GetProperty("newTenants")[0].GetProperty("schema").GetString());
        Assert.AreEqual("tenant_acme", root.GetProperty("upToDate")[0].GetString());
        Assert.AreEqual("tenant_old", root.GetProperty("formerTenants")[0].GetString());
        Assert.IsFalse(root.TryGetProperty("tenantsNotRead", out _));
        Assert.AreEqual(0, root.GetProperty("added").GetArrayLength());
    }

    [TestMethod]
    public void ASourceDatabasesInMemoryProjectIsShownAsSourceSlash()
    {
        var path = Path.Combine(SourceProject.VirtualRoot, "app", "Tables", "t.sql");

        Assert.AreEqual("source:/app/Tables/t.sql", SourceProject.Display(path));
        Assert.AreEqual("source:/", SourceProject.Display(SourceProject.VirtualRoot) + (SourceProject.Display(SourceProject.VirtualRoot).EndsWith('/') ? string.Empty : "/"));
        var other = Path.Combine(Path.GetTempPath(), "x.sql");
        Assert.AreEqual(other, SourceProject.Display(other));
        Assert.IsTrue(SourceProject.IsDisplayPath("source:/app/t.sql"));
    }

    [TestMethod]
    public void TenantsWithTheSameChangesAreOneGroup()
    {
        var one = Diff(string.Empty, "CREATE TABLE tenant_acme.audit (id int);");
        var two = Diff(string.Empty, "CREATE TABLE tenant_globex.audit (id int);");
        var other = Diff(string.Empty, "CREATE TABLE tenant_initech.extra (id int);");

        var comparison = new TenantsComparison(
            "tenant",
            new TenantList([], [], null),
            [
                new TenantDifference(new TenantTarget("acme", "tenant_acme", true), one),
                new TenantDifference(new TenantTarget("globex", "tenant_globex", true), two),
                new TenantDifference(new TenantTarget("initech", "tenant_initech", true), other),
            ]);

        Assert.HasCount(2, comparison.Groups);
        CollectionAssert.AreEqual(new[] { "tenant_acme", "tenant_globex" }, comparison.Groups[0].Members.Select(static member => member.Tenant.Schema).ToArray());
        Assert.AreEqual((6, 0, 0), comparison.Totals);
    }

    private static ChangeSet Diff(string oldSql, string newSql)
        => new DiffEngine().Diff(Build(oldSql), Build(newSql));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
