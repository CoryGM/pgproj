using System.CommandLine;

namespace PgProj.Cli.Tests;

/// <summary>
/// The JSON model dump is a debugging aid: <c>build</c> must write nothing extra by default and
/// only write the model when <c>--dump-model</c> is supplied.
/// </summary>
[TestClass]
public sealed class BuildCommandDumpModelTests
{
    [TestMethod]
    public void BuildWithoutDumpModelWritesNoFiles()
    {
        using var project = TempProject.Create();

        var exitCode = Invoke(project.ProjectFile);

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(Directory.GetFiles(Directory.GetCurrentDirectory(), "model-*.json"));
        Assert.IsEmpty(Directory.GetFiles(project.Directory, "*.json").Where(static file => !file.EndsWith("pgproj.json", StringComparison.Ordinal)).ToArray());
    }

    [TestMethod]
    public void BuildWithDumpModelWritesModelJson()
    {
        using var project = TempProject.Create();
        var dumpPath = Path.Combine(project.Directory, "model.json");

        var exitCode = Invoke(project.ProjectFile, "--dump-model", dumpPath);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(dumpPath));
        Assert.IsGreaterThan(0, new FileInfo(dumpPath).Length);
    }

    private static int Invoke(string projectFile, params string[] extraArgs)
    {
        var root = new RootCommand("pgproj") { BuildCommand.Create(new CliContext()) };
        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            return root.Parse(["build", "--project", projectFile, .. extraArgs]).Invoke();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private sealed class TempProject : IDisposable
    {
        private TempProject(string directory)
        {
            Directory = directory;
            ProjectFile = Path.Combine(directory, "pgproj.json");
        }

        public string Directory { get; }

        public string ProjectFile { get; }

        public static TempProject Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "pgproj-cli-tests-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "pgproj.json"), """{ "name": "dump_test", "version": "1.0", "source": "./" }""");
            File.WriteAllText(Path.Combine(directory, "t.sql"), "CREATE TABLE public.t (id bigint NOT NULL);");
            return new TempProject(directory);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
