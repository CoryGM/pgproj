namespace PgProj.Core.Tests;

[TestClass]
public sealed class PgProjectLoaderDeployScriptTests
{
    [TestMethod]
    public void LoadReadsConfiguredPreAndPostDeployScripts()
    {
        using var projectDirectory = TemporaryProjectDirectory.Create();
        var preDeployPath = projectDirectory.WriteFile("scripts/pre.sql", "SELECT 'pre';\n");
        var postDeployPath = projectDirectory.WriteFile("scripts/post.sql", "SELECT 'post';\n");
        var projectPath = projectDirectory.WriteProject(
            """
            {
              "name": "test",
              "version": "1.0.0",
              "source": "sql",
              "predeploy": "scripts/pre.sql",
              "postdeploy": "scripts/post.sql"
            }
            """);

        var project = new PgProjectLoader().Load(projectPath);

        Assert.AreEqual(Path.GetFullPath(preDeployPath), project.PreDeployScript);
        Assert.AreEqual(Path.GetFullPath(postDeployPath), project.PostDeployScript);
        Assert.AreEqual("SELECT 'pre';\n", project.PreDeployScriptContent);
        Assert.AreEqual("SELECT 'post';\n", project.PostDeployScriptContent);
    }

    [TestMethod]
    public void LoadLeavesUnconfiguredDeployScriptsNull()
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

        Assert.IsNull(project.PreDeployScript);
        Assert.IsNull(project.PostDeployScript);
        Assert.IsNull(project.PreDeployScriptContent);
        Assert.IsNull(project.PostDeployScriptContent);
    }

    [TestMethod]
    public void LoadTreatsWhitespaceOnlyDeployScriptsAsAbsentContent()
    {
        using var projectDirectory = TemporaryProjectDirectory.Create();
        var preDeployPath = projectDirectory.WriteFile("pre.sql", " \r\n\t");
        var projectPath = projectDirectory.WriteProject(
            """
            {
              "name": "test",
              "version": "1.0.0",
              "source": "sql",
              "predeploy": "pre.sql"
            }
            """);

        var project = new PgProjectLoader().Load(projectPath);

        Assert.AreEqual(Path.GetFullPath(preDeployPath), project.PreDeployScript);
        Assert.IsNull(project.PreDeployScriptContent);
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
