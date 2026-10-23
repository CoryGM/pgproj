namespace PgProj.Core.Tests;

/// <summary>
/// G-304a: <c>connectionString</c> in <c>pgproj.json</c> is replaced by two directional properties. <c>targetConnectionString</c> is the default database for
/// <c>diff</c> and <c>publish</c> and is never written by a tool; <c>sourceConnectionString</c> is where <c>init</c> scaffolded the project from. The old property
/// is refused, because an unknown property is otherwise ignored and a project that still had it would silently lose its connection.
/// </summary>
[TestClass]
public sealed class PgProjectLoaderConnectionStringTests
{
    [TestMethod]
    public void ANewProjectHasNeitherConnection()
    {
        var project = Load(string.Empty);

        Assert.IsNull(project.SourceConnectionString);
        Assert.IsNull(project.TargetConnectionString);
    }

    [TestMethod]
    public void TheTwoConnectionsAreReadSeparately()
    {
        var project = Load(""", "sourceConnectionString": " Host=a;Database=prod;Password=<password> ", "targetConnectionString": "Host=b;Database=qa" """);

        Assert.AreEqual("Host=a;Database=prod;Password=<password>", project.SourceConnectionString);
        Assert.AreEqual("Host=b;Database=qa", project.TargetConnectionString);
    }

    [TestMethod]
    public void ABlankConnectionIsNone()
    {
        var project = Load(""", "sourceConnectionString": "  ", "targetConnectionString": "" """);

        Assert.IsNull(project.SourceConnectionString);
        Assert.IsNull(project.TargetConnectionString);
    }

    [TestMethod]
    public void TheNamesAreCaseInsensitiveLikeEveryOtherProperty()
        => Assert.AreEqual("Host=b", Load(""", "TargetConnectionString": "Host=b" """).TargetConnectionString);

    [TestMethod]
    public void TheRemovedConnectionStringIsRefusedAndTheMessageNamesTheTwoThatReplaceIt()
    {
        var exception = Assert.Throws<PgProjectValidationException>(() => Load(""", "connectionString": "Host=a;Database=prod" """));

        StringAssert.Contains(exception.Message, "'connectionString' property, which is no longer read");
        StringAssert.Contains(exception.Message, "targetConnectionString");
        StringAssert.Contains(exception.Message, "sourceConnectionString");
        Assert.DoesNotContain("Host=a", exception.Message, StringComparison.Ordinal, "the message never repeats a connection string");
    }

    [TestMethod]
    public void ABlankRemovedConnectionStringIsRefusedToo()
        => Assert.Throws<PgProjectValidationException>(() => Load(""", "connectionString": "" """));

    private static PgProject Load(string extraProperties)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pgproj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "sql"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "pgproj.json"), $$"""{ "name": "test", "version": "1.0.0", "source": "sql"{{extraProperties}} }""");
            return new PgProjectLoader().Load(Path.Combine(directory, "pgproj.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
