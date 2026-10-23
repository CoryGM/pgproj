using PgProj.Engine;
using PgProj.Semantics;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-124: <c>build</c> syntax-checks the pre- and post-deployment scripts and each file they include, naming the file and the line in it,
/// and never models them: a data script's <c>INSERT</c> is neither an error nor a warning, and a <c>Scripts</c> folder is not schema. The model is the one
/// <see cref="PgProjEngine.BuildAsync"/> returns (G-320); that the script carries an expanded include is the engine's <c>DeploymentScriptCompositionTests</c>.
/// </summary>
[TestClass]
public sealed class DeploymentScriptCheckTests
{
    [TestMethod]
    public async Task ASyntaxErrorInAnIncludedScriptNamesThatFileAndItsLine()
    {
        // Arrange
        using var project = ProjectDirectory.Create(
            null, "sql/post.sql",
            ("sql/a.sql", "CREATE TABLE public.a (x int);\n"),
            ("sql/post.sql", "SELECT 1;\n:r Scripts/seed.sql\nSELECT 2;\n"),
            ("sql/Scripts/seed.sql", "-- seed\n\nINSERT INTO public.a VALUES (1,, 2);\n"));
        var context = new CliContext();

        // Act
        var model = await BuildModelAsync(project.ProjectFile);
        var output = new StringWriter();
        context.WriteDiagnostics(model, output);

        // Assert
        var error = model.Diagnostics.Single(static d => d.Severity == SemanticDiagnosticSeverity.Error);
        Assert.AreEqual(Path.Combine(project.Path, "sql", "Scripts", "seed.sql"), error.Location?.FilePath);
        Assert.AreEqual(3, error.Location?.Line);
        Assert.IsTrue(model.HasErrors);
        StringAssert.Contains(output.ToString(), $"{Path.Combine("sql", "Scripts", "seed.sql")}:3:");
    }

    [TestMethod]
    public async Task ASyntaxErrorInThePreDeploymentScriptItselfIsReportedAtItsLine()
    {
        // Arrange
        using var project = ProjectDirectory.Create(
            "pre.sql", null,
            ("sql/a.sql", "CREATE TABLE public.a (x int);\n"),
            ("pre.sql", "SELECT 1;\n\nSELEC 2;\n"));

        // Act
        var model = await BuildModelAsync(project.ProjectFile);

        // Assert
        var error = model.Diagnostics.Single(static d => d.Severity == SemanticDiagnosticSeverity.Error);
        Assert.AreEqual(Path.Combine(project.Path, "pre.sql"), error.Location?.FilePath);
        Assert.AreEqual(3, error.Location?.Line);

        // G-308: the end of the misspelt word, from the script's own diagnostic.
        Assert.AreEqual(1, error.Location?.Column);
        Assert.AreEqual(3, error.Location?.EndLine);
        Assert.AreEqual(6, error.Location?.EndColumn);
    }

    [TestMethod]
    public async Task AScriptOfDataStatementsBuildsCleanAndIsNotModeled()
    {
        // Arrange: an INSERT, a DO block, and a MERGE are all fine in a script, and none becomes part of the model.
        using var project = ProjectDirectory.Create(
            null, "post.sql",
            ("sql/a.sql", "CREATE TABLE public.a (x int);\n"),
            ("post.sql", ":r sql/Scripts/seed.sql\nDO $$ BEGIN PERFORM 1; END $$;\n"),
            ("sql/Scripts/seed.sql", "INSERT INTO public.a VALUES (1);\nCREATE TABLE public.only_in_a_script (y int);\n"));

        // Act
        var result = await new PgProjEngine().BuildAsync(new BuildRequest { ProjectFilePath = project.ProjectFile });
        var model = result.Model!;

        // Assert
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
        Assert.AreEqual(1, result.Documents.Count);
        CollectionAssert.AreEqual(new[] { "public.a" }, model.Tables.Keys.ToArray());
    }

    private static async Task<DatabaseModel> BuildModelAsync(string projectFile)
        => (await new PgProjEngine().BuildAsync(new BuildRequest { ProjectFilePath = projectFile })).Model!;

    private sealed class ProjectDirectory : IDisposable
    {
        private ProjectDirectory(string path) => Path = path;

        public string Path { get; }

        public string ProjectFile => System.IO.Path.Combine(Path, "pgproj.json");

        public static ProjectDirectory Create(string? predeploy, string? postdeploy, params (string Path, string Content)[] files)
        {
            var directory = new ProjectDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pgproj-g124-" + Guid.NewGuid().ToString("N")));
            var json = $$"""{ "name": "g124", "version": "1.0", "source": "./sql"{{(predeploy is null ? "" : $", \"predeploy\": \"{predeploy}\"")}}{{(postdeploy is null ? "" : $", \"postdeploy\": \"{postdeploy}\"")}} }""";
            Write(directory.Path, "pgproj.json", json);
            foreach (var (path, content) in files)
            {
                Write(directory.Path, path, content);
            }

            return directory;
        }

        private static void Write(string root, string relativePath, string content)
        {
            var filePath = System.IO.Path.Combine(root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, content);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
