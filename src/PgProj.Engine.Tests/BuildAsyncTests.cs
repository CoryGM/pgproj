using System.Text.Json;
using PgProj.Core;

namespace PgProj.Engine.Tests;

/// <summary>G-309a: <see cref="PgProjEngine.BuildAsync"/> and what it returns.</summary>
[TestClass]
public sealed class BuildAsyncTests
{
    [TestMethod]
    public async Task AProjectInMemoryBuildsTheSameResultAsTheSameFilesOnDisk()
    {
        var fixture = new ProjectFixture().With(Path.Combine("sql", "Schema", "broken.sql"), "CREATE TABLE b (a int, ;\n");
        try
        {
            fixture.WriteToDisk();

            var onDisk = await new PgProjEngine().BuildAsync(new BuildRequest { ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);
            var inMemory = await new PgProjEngine().BuildAsync(new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);

            Assert.AreEqual(BuildStatus.Failed, onDisk.Status);
            Assert.AreEqual(onDisk.Status, inMemory.Status);
            Assert.AreEqual(onDisk.ProjectName, inMemory.ProjectName);
            Assert.AreEqual(onDisk.SourcePath, inMemory.SourcePath);
            CollectionAssert.AreEqual(onDisk.Documents.ToArray(), inMemory.Documents.ToArray());
            CollectionAssert.AreEqual(onDisk.Diagnostics.ToArray(), inMemory.Diagnostics.ToArray());
            Assert.AreEqual(onDisk.ErrorCount, inMemory.ErrorCount);
            Assert.AreEqual(onDisk.WarningCount, inMemory.WarningCount);
            Assert.AreEqual(JsonSerializer.Serialize(onDisk.Model), JsonSerializer.Serialize(inMemory.Model));
            Assert.IsTrue(inMemory.Model!.Tables.Values.Any(static table => table.Name == "customer"));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [TestMethod]
    public async Task ADiagnosticHasItsFileAndRange()
    {
        var fixture = new ProjectFixture().With(Path.Combine("sql", "Schema", "broken.sql"), "CREATE TABLE b (a int, ;\n");

        var result = await new PgProjEngine().BuildAsync(new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);

        var error = result.Diagnostics.Single(static diagnostic => diagnostic.Severity == EngineSeverity.Error);
        Assert.AreEqual(Path.Combine(fixture.Root, "sql", "Schema", "broken.sql"), error.File);
        Assert.AreEqual(1, error.Line);
        Assert.AreEqual(24, error.Column);
        Assert.AreEqual(1, error.EndLine);
        Assert.AreEqual(25, error.EndColumn);
        Assert.AreEqual(1, result.ErrorCount);
        Assert.IsGreaterThan(0, result.WarningCount, "the INSERT is not modeled");
    }

    [TestMethod]
    public async Task AnOverlayBufferIsWhatIsBuiltAndItsVersionIsReported()
    {
        var fixture = new ProjectFixture();
        var overlay = new OverlayFileSource(fixture.InMemory())
            .Set(Path.Combine(fixture.Root, "sql", "Schema", "orders.sql"), "CREATE TABLE app.orders (id bigint PRIMARY KEY, total numeric);\nCREATE TABLE app.fresh (a int);\n", version: 12)
            .Set(Path.Combine(fixture.Root, "sql", "Schema", "unsaved.sql"), "CREATE TABLE app.unsaved (b int);\n", version: 3);

        var result = await new PgProjEngine().BuildAsync(new BuildRequest { Files = overlay, ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);

        var tables = result.Model!.Tables.Values.Select(static table => table.Name).ToArray();
        CollectionAssert.Contains(tables, "fresh");
        CollectionAssert.Contains(tables, "unsaved");
        Assert.AreEqual(12, result.Documents.Single(static document => document.Path.EndsWith("orders.sql", StringComparison.Ordinal)).Version);
        Assert.AreEqual(3, result.Documents.Single(static document => document.Path.EndsWith("unsaved.sql", StringComparison.Ordinal)).Version);
        Assert.IsNull(result.Documents.Single(static document => document.Path.EndsWith("app.sql", StringComparison.Ordinal)).Version);
    }

    [TestMethod]
    public async Task ProjectSettingsAndARootBuildWithoutAProjectFile()
    {
        var fixture = new ProjectFixture();
        var files = fixture.InMemory();
        files.Remove(fixture.ProjectFile);

        var result = await new PgProjEngine().BuildAsync(
            new BuildRequest { Files = files, Settings = new ProjectSettings { Name = "from settings", Version = "1", Source = "./sql" }, RootDirectory = fixture.Root },
            TestContext.CancellationToken);

        Assert.AreEqual(BuildStatus.Succeeded, result.Status);
        Assert.AreEqual("from settings", result.ProjectName);
        Assert.AreEqual(Path.Combine(fixture.Root, "sql"), result.SourcePath);
        Assert.HasCount(2, result.Documents);
    }

    [TestMethod]
    public async Task AMissingProjectFileIsAStatusNotAnException()
    {
        var path = Path.Combine(Path.GetTempPath(), "pgproj-engine-none", "pgproj.json");

        var result = await new PgProjEngine().BuildAsync(new BuildRequest { Files = new InMemoryFileSource(), ProjectFilePath = path }, TestContext.CancellationToken);

        Assert.AreEqual(BuildStatus.ProjectNotFound, result.Status);
        Assert.AreEqual(Path.GetFullPath(path), result.ProjectFilePath);
        Assert.IsNull(result.Model);
    }

    [TestMethod]
    public async Task AnInvalidProjectIsAStatusWithTheLoadersMessage()
    {
        var fixture = new ProjectFixture().With("pgproj.json", """{ "version": "1", "source": "./sql" }""");

        var result = await new PgProjEngine().BuildAsync(new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);

        Assert.AreEqual(BuildStatus.ProjectInvalid, result.Status);
        Assert.AreEqual($"Project file '{fixture.ProjectFile}' is missing required property 'name'.", result.Message);
    }

    [TestMethod]
    public async Task AProjectWithWarningsOnlySucceeds()
    {
        var fixture = new ProjectFixture();

        var result = await new PgProjEngine().BuildAsync(new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);

        Assert.AreEqual(BuildStatus.Succeeded, result.Status);
        Assert.AreEqual(0, result.ErrorCount);
        Assert.AreEqual(BuildResult.CurrentSchemaVersion, result.SchemaVersion);
        Assert.IsNotNull(result.Model);
    }

    [TestMethod]
    public async Task TheExcludeOverridesWorkInEitherDirection()
    {
        var fixture = new ProjectFixture().With(Path.Combine("sql", "Schema", "grants.sql"), "GRANT SELECT ON app.customer TO reader;\n");

        var excluded = await new PgProjEngine().BuildAsync(new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);
        var included = await new PgProjEngine().BuildAsync(new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, ExcludeGrants = false }, TestContext.CancellationToken);

        Assert.IsEmpty(excluded.Model!.Grants);
        Assert.IsNotEmpty(included.Model!.Grants);
    }

    [TestMethod]
    public void ARunWorksFromItsSnapshotNotFromTheFilesAfterwards()
    {
        var fixture = new ProjectFixture();
        var files = fixture.InMemory();
        var snapshot = ProjectSnapshot.Read(files, fixture.ProjectFile, settings: null, rootDirectory: null, TestContext.CancellationToken);

        files.Set(Path.Combine(fixture.Root, "sql", "Schema", "orders.sql"), "CREATE TABLE app.changed_after (a int);\n");
        var built = ProjectModelBuilder.Build(snapshot, null, null, null, null, TestContext.CancellationToken);

        var tables = built.Model.Tables.Values.Select(static table => table.Name).ToArray();
        CollectionAssert.Contains(tables, "orders");
        CollectionAssert.DoesNotContain(tables, "changed_after");
    }

    [TestMethod]
    public async Task ACancelledTokenCancelsTheTask()
    {
        var fixture = new ProjectFixture();
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var task = new PgProjEngine().BuildAsync(new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, source.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        Assert.IsTrue(task.IsCanceled);
    }

    [TestMethod]
    public async Task CancellingWhileTheFilesAreParsedStopsTheRun()
    {
        var fixture = new ProjectFixture();
        using var source = new CancellationTokenSource();
        // The first file read cancels the run; it is checked between the files it parses.
        var progress = new SynchronousProgress(value =>
        {
            if (value.Kind == EngineEventKind.DocumentRead)
            {
                source.Cancel();
            }
        });

        var task = new PgProjEngine().BuildAsync(new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, Progress = progress }, source.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
    }

    [TestMethod]
    public async Task TheRunReportsTheProjectEachFileAndTheModel()
    {
        var fixture = new ProjectFixture();
        var events = new List<EngineEvent>();

        var result = await new PgProjEngine().BuildAsync(
            new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, Progress = new SynchronousProgress(events.Add) },
            TestContext.CancellationToken);

        Assert.AreEqual(EngineEventKind.ProjectRead, events[0].Kind);
        Assert.AreEqual(fixture.ProjectFile, events[0].Path);
        CollectionAssert.AreEqual(result.Documents.Select(static document => document.Path).ToArray(), events.Where(static e => e.Kind == EngineEventKind.DocumentRead).Select(static e => e.Path!).ToArray());
        Assert.AreEqual(EngineEventKind.ModelBuilt, events[^1].Kind);
    }

    [TestMethod]
    public void ARequestNamesTheProjectOnceAndCompletely()
    {
        var engine = new PgProjEngine();

        Assert.Throws<ArgumentException>(() => engine.BuildAsync(new BuildRequest()));
        Assert.Throws<ArgumentException>(() => engine.BuildAsync(new BuildRequest { ProjectFilePath = "a", Settings = new ProjectSettings(), RootDirectory = "b" }));
        Assert.Throws<ArgumentException>(() => engine.BuildAsync(new BuildRequest { Settings = new ProjectSettings() }));
        Assert.Throws<ArgumentNullException>(() => engine.BuildAsync(null!));
    }

    [TestMethod]
    public async Task TheResultSerializesWithoutTheModel()
    {
        var fixture = new ProjectFixture();

        var result = await new PgProjEngine().BuildAsync(new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.AreEqual(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("fixture", json.RootElement.GetProperty("projectName").GetString());
        Assert.IsFalse(json.RootElement.TryGetProperty("model", out _));
        Assert.IsTrue(json.RootElement.GetProperty("diagnostics").GetArrayLength() > 0);
    }

    [TestMethod]
    public async Task TheJsonDocumentIsTheEnvelopeDiffWritesWithOnlyTheBuildPart()
    {
        var fixture = new ProjectFixture().With(Path.Combine("sql", "Schema", "broken.sql"), "CREATE TABLE b (a int, ;\n");

        var result = await new PgProjEngine().BuildAsync(new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);
        var text = result.ToJson(static path => Path.GetFileName(path));
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;

        CollectionAssert.AreEqual(
            new[] { "schemaVersion", "status", "project", "documents", "diagnostics", "counts" },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual("failed", root.GetProperty("status").GetString());
        Assert.AreEqual("pgproj.json", root.GetProperty("project").GetProperty("file").GetString(), "every path goes through displayPath");
        var error = root.GetProperty("diagnostics").EnumerateArray().Single(static d => d.GetProperty("severity").GetString() == "Error");
        Assert.AreEqual("broken.sql", error.GetProperty("file").GetString());
        Assert.AreEqual(24, error.GetProperty("column").GetInt32());
        Assert.AreEqual(25, error.GetProperty("endColumn").GetInt32());

        // diff's document of a run that did not compare is the same envelope, byte for byte (decision 3 on #213).
        var compare = new CompareResult
        {
            Status = CompareStatus.Failed,
            ProjectFilePath = result.ProjectFilePath,
            ProjectName = result.ProjectName,
            SourcePath = result.SourcePath,
            Documents = result.Documents,
            Diagnostics = result.Diagnostics,
            ErrorCount = result.ErrorCount,
            WarningCount = result.WarningCount,
            InformationCount = result.InformationCount,
        };
        Assert.AreEqual(compare.ToJson(static path => Path.GetFileName(path)), text);
    }

    public TestContext TestContext { get; set; } = null!;

    /// <summary><see cref="Progress{T}"/> posts to a context and returns before the handler runs; a test wants it called when it is reported.</summary>
    private sealed class SynchronousProgress(Action<EngineEvent> handler) : IProgress<EngineEvent>
    {
        public void Report(EngineEvent value) => handler(value);
    }
}
