using System.Text.Json;
using PgProj.Core;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-318: <see cref="BuildRequest.SourceConnectionString"/>, a live database built through the public API (what <c>build --source-connection</c> does). A database
/// is not available to these tests, so they cover what is decided before one is reached; the Docker suite builds a real one (<c>BuildFromDatabaseEndToEndTests</c>).
/// </summary>
[TestClass]
public sealed class BuildFromDatabaseTests
{
    [TestMethod]
    public async Task AMalformedConnectionStringIsRefusedInWordsAndTheJsonSaysSo()
    {
        var result = await new PgProjEngine().BuildAsync(new BuildRequest { SourceConnectionString = "Host=localhost;Port=notaport;Database=app" }, TestContext.CancellationToken);

        Assert.AreEqual(BuildStatus.Refused, result.Status);
        StringAssert.StartsWith(result.Message, "The connection string is not valid: the value of 'port'");
        Assert.IsNull(result.Model);
        Assert.IsNull(result.Source);
        using var json = JsonDocument.Parse(result.ToJson());
        Assert.AreEqual("refused", json.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(result.Message, json.RootElement.GetProperty("message").GetString());
        Assert.IsFalse(json.RootElement.TryGetProperty("source", out _), "nothing was read");
    }

    [TestMethod]
    public async Task AProjectWithTenantsIsRefusedBeforeAnyDatabaseIsTouched()
    {
        var fixture = new ProjectFixture()
            .With("pgproj.json", """{ "name": "shop", "version": "1", "source": "./sql", "tenants": { "template": "./sql/Tenant", "prefix": "tenant" } }""")
            .With(Path.Combine("sql", "Tenant", "orders.sql"), "CREATE TABLE orders (id bigint PRIMARY KEY);\n");

        // Port 1 is never reached: the project is read, and refused, first.
        var result = await new PgProjEngine().BuildAsync(
            new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, SourceConnectionString = "Host=127.0.0.1;Port=1;Database=app" },
            TestContext.CancellationToken);

        Assert.AreEqual(BuildStatus.Refused, result.Status);
        StringAssert.Contains(result.Message, "--source-connection cannot be used with a project that has tenants");
    }

    [TestMethod]
    public async Task AProjectFileThatIsNotValidIsProjectInvalidBeforeAnyDatabaseIsTouched()
    {
        var fixture = new ProjectFixture().With("pgproj.json", "{ not json");

        var result = await new PgProjEngine().BuildAsync(
            new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, SourceConnectionString = "Host=127.0.0.1;Port=1;Database=app" },
            TestContext.CancellationToken);

        Assert.AreEqual(BuildStatus.ProjectInvalid, result.Status);
        StringAssert.Contains(result.Message, "contains invalid JSON");
        Assert.AreEqual(fixture.ProjectFile, result.ProjectFilePath);
    }

    [TestMethod]
    public void ARequestForADatabaseNeedsNoProjectButNotTwo()
    {
        var engine = new PgProjEngine();

        Assert.Throws<ArgumentException>(() => engine.BuildAsync(new BuildRequest { ProjectFilePath = "a", Settings = new ProjectSettings(), RootDirectory = "b", SourceConnectionString = "Host=h" }));
        Assert.Throws<ArgumentException>(() => engine.BuildAsync(new BuildRequest { Settings = new ProjectSettings(), SourceConnectionString = "Host=h" }));
    }

    public TestContext TestContext { get; set; } = null!;
}
