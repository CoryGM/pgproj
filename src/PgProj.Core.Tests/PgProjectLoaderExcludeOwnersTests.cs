namespace PgProj.Core.Tests;

/// <summary>G-146: excludeOwners is an optional, additive pgproj.json field; it defaults to false (today's behavior: owners a project states are modeled).</summary>
[TestClass]
public sealed class PgProjectLoaderExcludeOwnersTests
{
    [TestMethod]
    public void LoadDefaultsToFalseWhenAbsent()
    {
        using var projectDirectory = TemporaryProjectDirectory.Create();
        var projectPath = projectDirectory.WriteProject(
            """
            {
              "name": "test",
              "version": "1.0.0",
              "source": "sql"
            }
            """);

        var project = new PgProjectLoader().Load(projectPath);

        Assert.IsFalse(project.ExcludeOwners);
    }

    [TestMethod]
    public void LoadReadsExcludeOwnersIndependentlyOfTheGrantOptions()
    {
        using var projectDirectory = TemporaryProjectDirectory.Create();
        var projectPath = projectDirectory.WriteProject(
            """
            {
              "name": "test",
              "version": "1.0.0",
              "source": "sql",
              "excludeOwners": true
            }
            """);

        var project = new PgProjectLoader().Load(projectPath);

        Assert.IsTrue(project.ExcludeOwners);
        Assert.IsFalse(project.ExcludeGrants);
        Assert.IsFalse(project.ExcludeRoleMemberships);
    }

    private sealed class TemporaryProjectDirectory : IDisposable
    {
        private TemporaryProjectDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(System.IO.Path.Combine(path, "sql"));
        }

        public string Path { get; }

        public static TemporaryProjectDirectory Create()
            => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pgproj-{Guid.NewGuid():N}"));

        public string WriteProject(string content)
        {
            var filePath = System.IO.Path.Combine(Path, "pgproj.json");
            File.WriteAllText(filePath, content);
            return filePath;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
