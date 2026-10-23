using PgProj.Core;
using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// The pre- and post-deploy scripts composed into the script <see cref="ScriptComposition"/> writes. Moved from the CLI's
/// <c>DeploymentScriptCompositionCliTests</c> and <c>DeploymentScriptCheckTests</c> (G-124) with the forwards they called (G-320).
/// </summary>
[TestClass]
public sealed class DeploymentScriptCompositionTests
{
    [TestMethod]
    public void GenerateComposesProjectPreAndPostDeployContent()
    {
        var project = new PgProject(
            "test",
            "1.0.0",
            [],
            Environment.CurrentDirectory,
            null,
            null,
            [])
        {
            PreDeployScriptContent = "SELECT 'pre';",
            PostDeployScriptContent = "SELECT 'post';",
        };
        var changes = new ChangeSet
        {
            Added = [],
            Removed = [],
            Modified = [],
        };

        var batch = ScriptComposition.Generate(changes, new DependencyGraph(), project);
        var script = ScriptComposition.GenerateSqlScript(batch);

        Assert.HasCount(1, batch.PreDeploymentScripts);
        Assert.HasCount(1, batch.PostDeploymentScripts);
        Assert.AreEqual(
            "SELECT 'pre';" + Environment.NewLine + Environment.NewLine + "SELECT 'post';",
            script);
    }

    [TestMethod]
    public void GenerateWithoutProjectScriptsMatchesExistingGenerationPath()
    {
        var project = new PgProject(
            "test",
            "1.0.0",
            [],
            Environment.CurrentDirectory,
            null,
            null,
            []);
        var changes = new ChangeSet
        {
            Added = [],
            Removed = [],
            Modified = [],
        };
        var graph = new DependencyGraph();

        var existingBatch = ScriptComposition.Generate(changes, graph);
        var projectBatch = ScriptComposition.Generate(changes, graph, project);

        Assert.AreEqual(ScriptComposition.GenerateSqlScript(existingBatch), ScriptComposition.GenerateSqlScript(projectBatch));
    }

    /// <summary>G-124: the script carries the post-deploy script with its <c>:r</c> include expanded.</summary>
    [TestMethod]
    public void TheDeploymentScriptTextCarriesTheExpandedInclude()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "pgproj-g124-" + Guid.NewGuid().ToString("N"));
        Write(root, "pgproj.json", """{ "name": "g124", "version": "1.0", "source": "./sql", "postdeploy": "post.sql" }""");
        Write(root, "sql/a.sql", "CREATE TABLE public.a (x int);\n");
        Write(root, "post.sql", ":r sql/Scripts/seed.sql\n");
        Write(root, "sql/Scripts/seed.sql", "INSERT INTO public.a VALUES (1);\n");
        try
        {
            // Act
            var snapshot = ProjectSnapshot.Read(DiskFileSource.Instance, Path.Combine(root, "pgproj.json"), null, null, CancellationToken.None);
            var built = ProjectModelBuilder.Build(snapshot, null, null, null, null, CancellationToken.None);
            var batch = ScriptComposition.Generate(
                ScriptComposition.Diff(new SemanticBuilder().Build([]), built.Model), ScriptComposition.BuildDependencyGraph(built.Model), built.Project);
            var script = ScriptComposition.GenerateSqlScript(batch);

            // Assert
            StringAssert.Contains(script, "-- :r sql/Scripts/seed.sql");
            StringAssert.Contains(script, "INSERT INTO public.a VALUES (1);");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(string root, string relativePath, string content)
    {
        var filePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, content);
    }
}
