namespace PgProj.Core.Tests;

/// <summary>G-87: excludeGrants/excludeRoleMemberships are optional, additive pgproj.json fields; both default to false (today's behavior: included).</summary>
[TestClass]
public sealed class PgProjectLoaderExcludeGrantsTests
{
    [TestMethod]
    public void LoadDefaultsBothToFalseWhenAbsent()
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

        Assert.IsFalse(project.ExcludeGrants);
        Assert.IsFalse(project.ExcludeRoleMemberships);
    }

    [TestMethod]
    public void LoadReadsExcludeGrantsIndependentlyOfExcludeRoleMemberships()
    {
        using var projectDirectory = TemporaryProjectDirectory.Create();
        var projectPath = projectDirectory.WriteProject(
            """
            {
              "name": "test",
              "version": "1.0.0",
              "source": "sql",
              "excludeGrants": true
            }
            """);

        var project = new PgProjectLoader().Load(projectPath);

        Assert.IsTrue(project.ExcludeGrants);
        Assert.IsFalse(project.ExcludeRoleMemberships);
    }

    [TestMethod]
    public void LoadReadsExcludeRoleMembershipsIndependentlyOfExcludeGrants()
    {
        using var projectDirectory = TemporaryProjectDirectory.Create();
        var projectPath = projectDirectory.WriteProject(
            """
            {
              "name": "test",
              "version": "1.0.0",
              "source": "sql",
              "excludeRoleMemberships": true
            }
            """);

        var project = new PgProjectLoader().Load(projectPath);

        Assert.IsFalse(project.ExcludeGrants);
        Assert.IsTrue(project.ExcludeRoleMemberships);
    }

    [TestMethod]
    public void LoadReadsBothWhenBothAreSet()
    {
        using var projectDirectory = TemporaryProjectDirectory.Create();
        var projectPath = projectDirectory.WriteProject(
            """
            {
              "name": "test",
              "version": "1.0.0",
              "source": "sql",
              "excludeGrants": true,
              "excludeRoleMemberships": true
            }
            """);

        var project = new PgProjectLoader().Load(projectPath);

        Assert.IsTrue(project.ExcludeGrants);
        Assert.IsTrue(project.ExcludeRoleMemberships);
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
            => WriteFile("pgproj.json", content);

        public string WriteFile(string relativePath, string content)
        {
            var filePath = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
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
