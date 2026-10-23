namespace PgProj.Core.Tests;

/// <summary>
/// G-124: a <c>Scripts</c> folder directly under <c>source</c> (any case) and the pre- and post-deployment scripts are never read as
/// schema, and a deployment script's <c>:r</c> includes are expanded when the project is loaded.
/// </summary>
[TestClass]
public sealed class PgProjectLoaderScriptsFolderTests
{
    private const string Project = """{ "name": "t", "version": "1.0", "source": "sql", "postdeploy": "sql/post.sql" }""";

    [TestMethod]
    [DataRow("Scripts")]
    [DataRow("scripts")]
    [DataRow("SCRIPTS")]
    public void AScriptsFolderUnderSourceIsNotReadAsSchema(string folder)
    {
        // Arrange
        using var directory = TempDirectory.Create();
        directory.Write("sql/a.sql", "CREATE TABLE public.a (x int);\n");
        directory.Write($"sql/{folder}/seed.sql", "INSERT INTO public.a VALUES (1);\n");
        directory.Write($"sql/{folder}/deeper/more.sql", "INSERT INTO public.a VALUES (2);\n");
        var projectFile = directory.Write("pgproj.json", """{ "name": "t", "version": "1.0", "source": "sql" }""");

        // Act
        var project = new PgProjectLoader().Load(projectFile);

        // Assert
        CollectionAssert.AreEqual(new[] { Path.Combine(directory.Path, "sql", "a.sql") }, project.SqlFiles.ToArray());
    }

    [TestMethod]
    public void OnlyTheFolderDirectlyUnderSourceIsSkipped()
    {
        // Arrange: a schema folder that happens to be called Scripts deeper down is still schema.
        using var directory = TempDirectory.Create();
        directory.Write("sql/app/Scripts/t.sql", "CREATE TABLE app.t (x int);\n");
        directory.Write("sql/ScriptsExtra/u.sql", "CREATE TABLE app.u (x int);\n");
        var projectFile = directory.Write("pgproj.json", """{ "name": "t", "version": "1.0", "source": "sql" }""");

        // Act
        var project = new PgProjectLoader().Load(projectFile);

        // Assert
        Assert.AreEqual(2, project.SqlFiles.Count);
    }

    [TestMethod]
    public void ThePreAndPostDeploymentScriptsAreNotReadAsSchemaWhereverTheyAre()
    {
        // Arrange: both are inside source, outside a Scripts folder.
        using var directory = TempDirectory.Create();
        directory.Write("sql/a.sql", "CREATE TABLE public.a (x int);\n");
        directory.Write("sql/post.sql", "INSERT INTO public.a VALUES (1);\n");
        directory.Write("sql/deploy/pre.sql", "SELECT 1;\n");
        var projectFile = directory.Write("pgproj.json", """{ "name": "t", "version": "1.0", "source": "sql", "predeploy": "sql/deploy/pre.sql", "postdeploy": "sql/post.sql" }""");

        // Act
        var project = new PgProjectLoader().Load(projectFile);

        // Assert
        CollectionAssert.AreEqual(new[] { Path.Combine(directory.Path, "sql", "a.sql") }, project.SqlFiles.ToArray());
        Assert.AreEqual("INSERT INTO public.a VALUES (1);\n", project.PostDeployScriptContent);
    }

    [TestMethod]
    public void AnIncludeInThePostDeploymentScriptIsExpandedIntoItsContent()
    {
        // Arrange
        using var directory = TempDirectory.Create();
        directory.Write("sql/a.sql", "CREATE TABLE public.a (x int);\n");
        directory.Write("sql/post.sql", "SELECT 1;\n:r Scripts/seed.sql\n");
        directory.Write("sql/Scripts/seed.sql", "INSERT INTO public.a VALUES (1);\n");
        var projectFile = directory.Write("pgproj.json", Project);

        // Act
        var project = new PgProjectLoader().Load(projectFile);

        // Assert
        Assert.AreEqual("SELECT 1;\n-- :r Scripts/seed.sql\nINSERT INTO public.a VALUES (1);\n", project.PostDeployScriptContent);
        CollectionAssert.AreEqual(
            new[] { Path.Combine(directory.Path, "sql", "post.sql"), Path.Combine(directory.Path, "sql", "Scripts", "seed.sql") },
            project.PostDeployScriptFiles.Select(static f => f.FilePath).ToArray());
        Assert.AreEqual(0, project.PreDeployScriptFiles.Count);
        Assert.AreEqual(1, project.SqlFiles.Count);
    }

    [TestMethod]
    public void AMissingIncludeStopsTheLoadWithAValidationError()
    {
        // Arrange
        using var directory = TempDirectory.Create();
        directory.Write("sql/post.sql", ":r Scripts/none.sql\n");
        var projectFile = directory.Write("pgproj.json", Project);

        // Act / Assert
        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => new PgProjectLoader().Load(projectFile));
        StringAssert.Contains(exception.Message, "none.sql");
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var directory = new TempDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pgproj-g124-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "sql"));
            return directory;
        }

        public string Write(string relativePath, string content)
        {
            var filePath = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, content);
            return filePath;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
