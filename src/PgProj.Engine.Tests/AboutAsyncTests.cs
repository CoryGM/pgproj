using PgProj.Core;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-332: <see cref="PgProjEngine.AboutAsync"/> describes the environment, the project as it was read (not built), and each database with where its connection
/// string came from. Nothing it cannot read is an error, and no password reaches the result. Connecting to a server is tested in the Docker suite
/// (<c>AboutEndToEndTests</c>); these tests do not connect.
/// </summary>
[TestClass]
public sealed class AboutAsyncTests
{
    private const string Root = "/about";

    public TestContext TestContext { get; set; } = null!;

    private static string ProjectFile => Path.GetFullPath(Path.Combine(Root, "pgproj.json"));

    [TestMethod]
    public async Task WithoutAProjectItDescribesTheEnvironmentAndNothingElse()
    {
        // Act
        var result = await new PgProjEngine().AboutAsync(new AboutRequest(), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(AboutProjectStatus.NotRequested, result.ProjectStatus);
        Assert.IsNull(result.ProjectFilePath);
        Assert.IsNull(result.Project);
        Assert.IsNull(result.Target);
        Assert.IsNull(result.Source);
        Assert.IsTrue(result.ParserLoaded, result.ParserMessage);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.EngineVersion));
        Assert.AreNotEqual("unknown", result.EngineVersion);
        StringAssert.StartsWith(result.Runtime, ".NET");
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.OperatingSystem));
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.RuntimeIdentifier));
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.OperatingSystemArchitecture));
    }

    [TestMethod]
    public async Task AProjectIsReadAndDescribedWithoutBeingBuilt()
    {
        // Arrange: a project whose SQL does not parse, which about must still describe.
        var files = new InMemoryFileSource()
            .Set(ProjectFile, """{ "name": "shop", "version": "2.1", "source": "./sql", "schemas": ["app", "billing"], "searchPath": ["app", "public"], "excludeOwners": true, "predeploy": "./scripts/pre.sql" }""")
            .Set(Path.Combine(Root, "sql", "a.sql"), "CREATE TABLE app.a (id int);")
            .Set(Path.Combine(Root, "sql", "b.sql"), "CREATE TABL broken;")
            .Set(Path.Combine(Root, "scripts", "pre.sql"), "SELECT 1;");

        // Act
        var result = await new PgProjEngine().AboutAsync(new AboutRequest { Files = files, ProjectFilePath = ProjectFile }, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(AboutProjectStatus.Found, result.ProjectStatus, result.ProjectMessage);
        Assert.AreEqual(ProjectFile, result.ProjectFilePath);
        var project = result.Project!;
        Assert.AreEqual("shop", project.Name);
        Assert.AreEqual("2.1", project.Version);
        Assert.AreEqual(2, project.SqlFileCount);
        CollectionAssert.AreEqual(new[] { "app", "billing" }, project.Schemas.ToArray());
        CollectionAssert.AreEqual(new[] { "app", "public" }, project.SearchPath.ToArray());
        Assert.IsTrue(project.ExcludeOwners);
        Assert.IsFalse(project.ExcludeGrants);
        Assert.IsNotNull(project.PreDeployScript);
        Assert.IsNull(project.PostDeployScript);
        Assert.IsEmpty(project.TenantLines);
    }

    [TestMethod]
    public async Task AMissingOrInvalidProjectIsReportedNotThrown()
    {
        // Act
        var missing = await new PgProjEngine().AboutAsync(new AboutRequest { Files = new InMemoryFileSource(), ProjectFilePath = ProjectFile }, TestContext.CancellationToken);
        var invalid = await new PgProjEngine().AboutAsync(
            new AboutRequest { Files = new InMemoryFileSource().Set(ProjectFile, """{ "name": "shop", "version": "1", "source": "./sql", "connectionString": "Host=x;Password=hunter2" }"""), ProjectFilePath = ProjectFile },
            TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(AboutProjectStatus.NotFound, missing.ProjectStatus);
        Assert.AreEqual(ProjectFile, missing.ProjectFilePath);
        Assert.IsNull(missing.Project);
        Assert.AreEqual(AboutProjectStatus.Invalid, invalid.ProjectStatus);
        Assert.IsFalse(string.IsNullOrWhiteSpace(invalid.ProjectMessage));
        Assert.IsNull(invalid.Project);
        Assert.IsFalse(invalid.ProjectMessage!.Contains("hunter2", StringComparison.Ordinal), invalid.ProjectMessage);
    }

    [TestMethod]
    public async Task EachConnectionSaysWhereItCameFromAndWhatItNamesWithoutItsPassword()
    {
        // Arrange: the project names both databases; the shell overrides the target only.
        var files = new InMemoryFileSource()
            .Set(ProjectFile, """{ "name": "shop", "version": "1", "source": "./sql", "targetConnectionString": "Host=db.example;Port=5433;Database=shop;Username=app;Password=fromfile", "sourceConnectionString": "Host=prod.example;Database=shop;Username=reader;Password=<password>" }""")
            .AddDirectory(Path.Combine(Root, "sql"));
        var request = new AboutRequest
        {
            Files = files,
            ProjectFilePath = ProjectFile,
            TargetConnectionString = "Host=localhost;Database=shop_test;Username=postgres;Password=fromoption",
            TargetConnectionOrigin = "--target-connection",
            Connect = false,
        };

        // Act
        var result = await new PgProjEngine().AboutAsync(request, TestContext.CancellationToken);

        // Assert: the shell's target wins and keeps its words; the source falls back to the file.
        var target = result.Target!;
        Assert.AreEqual("--target-connection", target.Origin);
        Assert.AreEqual(AboutConnectionStatus.NotTried, target.Status);
        Assert.AreEqual("localhost", target.Host);
        Assert.AreEqual(5432, target.Port);
        Assert.AreEqual("shop_test", target.Database);
        Assert.AreEqual("postgres", target.Username);
        var source = result.Source!;
        Assert.AreEqual("sourceConnectionString in pgproj.json", source.Origin);
        Assert.AreEqual("prod.example", source.Host);
        Assert.AreEqual("reader", source.Username);
        Assert.IsNull(source.ServerVersion);
        Assert.IsNull(source.IsSuperuser);
        foreach (var secret in new[] { "fromfile", "fromoption", "<password>" })
        {
            Assert.IsFalse(result.ToString().Contains(secret, StringComparison.Ordinal), secret);
            Assert.IsFalse(target.ToString().Contains(secret, StringComparison.Ordinal), secret);
            Assert.IsFalse(source.ToString().Contains(secret, StringComparison.Ordinal), secret);
        }
    }

    [TestMethod]
    public async Task ATargetFromTheProjectIsNamedAsTheProjectsAndNoneIsNull()
    {
        // Arrange
        var files = new InMemoryFileSource()
            .Set(ProjectFile, """{ "name": "shop", "version": "1", "source": "./sql", "targetConnectionString": "Host=db.example;Database=shop" }""")
            .AddDirectory(Path.Combine(Root, "sql"));

        // Act
        var result = await new PgProjEngine().AboutAsync(new AboutRequest { Files = files, ProjectFilePath = ProjectFile, Connect = false }, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("targetConnectionString in pgproj.json", result.Target!.Origin);
        Assert.AreEqual("db.example", result.Target.Host);
        Assert.IsNull(result.Target.Username);
        Assert.IsNull(result.Source);
    }

    [TestMethod]
    public async Task AConnectionStringThatIsNotValidFailsWithItsReasonAndNoPassword()
    {
        // Act
        var result = await new PgProjEngine().AboutAsync(
            new AboutRequest { TargetConnectionString = "Host=localhost;Password=hunter2;Bogus=1", TargetConnectionOrigin = "--target-connection" },
            TestContext.CancellationToken);

        // Assert
        var target = result.Target!;
        Assert.AreEqual(AboutConnectionStatus.Failed, target.Status);
        StringAssert.StartsWith(target.Message, "The connection string is not valid");
        Assert.IsFalse(target.ToString().Contains("hunter2", StringComparison.Ordinal), target.ToString());
    }
}
