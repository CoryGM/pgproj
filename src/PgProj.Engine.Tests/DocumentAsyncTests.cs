using System.Text.Json;
using PgProj.Engine.Documentation;

namespace PgProj.Engine.Tests;

/// <summary>G-309d: <see cref="PgProjEngine.DocumentAsync"/> renders a project's documentation and returns it, writing nothing.</summary>
[TestClass]
public sealed class DocumentAsyncTests
{
    private static readonly DateTime Generated = new(2026, 10, 20, 9, 30, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task AProjectInMemoryIsRenderedInEachFormatWithItsTagProblems()
    {
        var fixture = new ProjectFixture().With(Path.Combine("sql", "Schema", "notes.sql"), "COMMENT ON TABLE app.customer IS $$A buyer.\n@pii: maybe$$;\n");
        var events = new List<EngineEvent>();
        BuildResult? built = null;

        var result = await new PgProjEngine().DocumentAsync(
            new DocumentRequest
            {
                Files = fixture.InMemory(),
                ProjectFilePath = fixture.ProjectFile,
                Formats = [DocFormat.Markdown, DocFormat.Html, DocFormat.Json, DocFormat.Html],
                GeneratedUtc = Generated,
                Progress = new Recorder(events),
                AfterBuild = (build, _) =>
                {
                    built = build;
                    return Task.CompletedTask;
                },
            },
            TestContext.CancellationToken);

        Assert.AreEqual(DocumentStatus.Succeeded, result.Status, result.Message);
        Assert.AreEqual(fixture.ProjectFile, result.ProjectFilePath);
        Assert.AreSame(built, result.Build);
        Assert.AreEqual(Generated, result.GeneratedUtc);
        CollectionAssert.AreEqual(new[] { DocFormat.Markdown, DocFormat.Html, DocFormat.Json }, result.Files.Select(static file => file.Format).ToArray(), "a repeated format is rendered once");
        CollectionAssert.AreEqual(
            new[] { "database-documentation.md", "database-documentation.html", "database-documentation.json" },
            result.Files.Select(static file => file.FileName).ToArray());
        StringAssert.StartsWith(result.Files[0].Text, "<!-- " + DocumentationFiles.Marker + " on 2026-10-20 09:30:00 UTC");
        StringAssert.Contains(result.Files[1].Text, "<!doctype html>");
        using (var json = JsonDocument.Parse(result.Files[2].Text))
        {
            Assert.AreEqual("fixture", json.RootElement.GetProperty("source").GetProperty("name").GetString());
        }

        var problem = result.Problems.Single();
        Assert.AreEqual(DocSeverity.Error, problem.Severity);
        Assert.AreEqual("app.customer", problem.Object);
        Assert.AreEqual("pii", problem.Tag);
        Assert.AreEqual(1, result.Counts!.Errors);
        Assert.AreEqual(2, result.Counts.Tables);
        Assert.IsTrue(events.Any(static e => e.Kind == EngineEventKind.DocumentationBuilt && e.Message!.StartsWith("Built the documentation in ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AProjectWithErrorsIsNotDocumentedAndTheBuildSaysWhy()
    {
        var fixture = new ProjectFixture().With(Path.Combine("sql", "Schema", "broken.sql"), "CREATE TABLE b (a int, ;\n");

        var result = await new PgProjEngine().DocumentAsync(new DocumentRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, TestContext.CancellationToken);

        Assert.AreEqual(DocumentStatus.Failed, result.Status);
        Assert.IsEmpty(result.Files);
        Assert.AreEqual(1, result.Build!.ErrorCount);
    }

    [TestMethod]
    public async Task AMissingProjectAndASchemaWithNothingDocumentedAreStatusesNotExceptions()
    {
        var fixture = new ProjectFixture();

        var missing = await new PgProjEngine().DocumentAsync(new DocumentRequest { Files = fixture.InMemory(), ProjectFilePath = Path.Combine(fixture.Root, "other.json") }, TestContext.CancellationToken);
        var refused = await new PgProjEngine().DocumentAsync(
            new DocumentRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, Filter = new ErdFilter(["nope"], Exclude: false) },
            TestContext.CancellationToken);

        Assert.AreEqual(DocumentStatus.ProjectNotFound, missing.Status);
        Assert.AreEqual(Path.Combine(fixture.Root, "other.json"), missing.ProjectFilePath);
        Assert.AreEqual(DocumentStatus.Refused, refused.Status);
        Assert.AreEqual("--schemas names a schema with no documented object: nope.", refused.Message);
    }

    [TestMethod]
    public async Task AMalformedConnectionStringIsRefusedBeforeAnythingIsRead()
    {
        var result = await new PgProjEngine().DocumentAsync(new DocumentRequest { SourceConnectionString = "Host=localhost;Nonsense Key=1" }, TestContext.CancellationToken);

        Assert.AreEqual(DocumentStatus.Refused, result.Status);
        Assert.IsNull(result.ProjectFilePath);
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class Recorder(List<EngineEvent> events) : IProgress<EngineEvent>
    {
        public void Report(EngineEvent value) => events.Add(value);
    }
}
