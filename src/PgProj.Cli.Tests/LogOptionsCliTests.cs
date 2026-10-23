using System.CommandLine;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-143: <c>--verbose</c> and <c>--quiet</c>. The default output is unchanged; <c>--quiet</c> leaves out progress, notices, and warnings but keeps errors
/// (on standard error) and the exit code; <c>--verbose</c> adds <c>[verbose]</c> lines on standard error; the two cannot be combined; every log line is
/// passed through the secret redactor.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LogOptionsCliTests
{
    private const string CleanSql = "CREATE TABLE public.t (id bigint NOT NULL);";

    // An unterminated statement: a syntax error, so the model has an error.
    private const string BrokenSql = "CREATE TABLE public.t (id bigint NOT NULL";

    [TestMethod]
    public void TheDefaultOutputIsUnchanged()
    {
        using var project = TempProject.Create(CleanSql);

        var run = Run(project, "build");

        Assert.AreEqual(0, run.ExitCode);
        StringAssert.Contains(run.Out, "Loaded project: log_options_test");
        StringAssert.Contains(run.Out, "SQL files: 1");
        StringAssert.Contains(run.Out, "No diagnostics.");
        Assert.AreEqual(string.Empty, run.Error);
    }

    [TestMethod]
    public void QuietWritesNothingWhenThereIsNothingWrong()
    {
        using var project = TempProject.Create(CleanSql);

        var run = Run(project, "build", "--quiet");

        Assert.AreEqual(0, run.ExitCode);
        Assert.AreEqual(string.Empty, run.Out);
        Assert.AreEqual(string.Empty, run.Error);
    }

    [TestMethod]
    public void QuietStillReportsAnErrorOnStandardErrorAndKeepsTheExitCode()
    {
        using var project = TempProject.Create(BrokenSql);

        var run = Run(project, "build", "--quiet");

        Assert.AreEqual(1, run.ExitCode);
        Assert.AreEqual(string.Empty, run.Out);
        StringAssert.Contains(run.Error, "[Error]");
    }

    [TestMethod]
    public void QuietStillReportsAMissingProjectFile()
    {
        var run = Run(null, "build", "--project", Path.Combine(Path.GetTempPath(), "pgproj-no-such-" + Guid.NewGuid().ToString("N"), "pgproj.json"), "--quiet");

        Assert.AreEqual(1, run.ExitCode);
        StringAssert.Contains(run.Error, "Build failed: project file was not found.");
    }

    [TestMethod]
    public void VerboseAddsDetailLinesOnStandardErrorOnly()
    {
        using var project = TempProject.Create(CleanSql);

        var run = Run(project, "build", "--verbose");

        Assert.AreEqual(0, run.ExitCode);
        StringAssert.Contains(run.Error, "[verbose] Reading project file:");
        StringAssert.Contains(run.Error, "[verbose] Read: ");
        StringAssert.Contains(run.Error, "t.sql");
        StringAssert.Contains(run.Error, "[verbose] Parsed 1 SQL file(s)");
        StringAssert.Contains(run.Error, "[verbose] Diagnostics: none");
        Assert.IsFalse(run.Out.Contains("[verbose]", StringComparison.Ordinal), run.Out);
        StringAssert.Contains(run.Out, "No diagnostics.");
    }

    [TestMethod]
    public void VerboseAndQuietCannotBeCombined()
    {
        using var project = TempProject.Create(CleanSql);

        var run = Run(project, "build", "--verbose", "--quiet");

        Assert.AreNotEqual(0, run.ExitCode);
        StringAssert.Contains(run.Error, "--verbose and --quiet cannot be used together.");
        Assert.IsFalse(run.Out.Contains("Loaded project", StringComparison.Ordinal), run.Out);
    }

    [TestMethod]
    public void EveryCommandTakesBothOptionsAndExplainsThemInHelp()
    {
        var context = new CliContext();
        foreach (var command in new[] { InitCommand.Create(context), BuildCommand.Create(context), DiffCommand.Create(context), PublishCommand.Create(context) })
        {
            Assert.IsTrue(command.Options.Any(static o => o.Name == "--verbose"), $"{command.Name} has no --verbose");
            Assert.IsTrue(command.Options.Any(static o => o.Name == "--quiet"), $"{command.Name} has no --quiet");
            StringAssert.Contains(command.Description!, "--verbose");
            StringAssert.Contains(command.Description!, "--quiet");
        }
    }

    [TestMethod]
    public void TheLogMasksASecretAtEveryLevel()
    {
        foreach (var verbosity in new[] { Verbosity.Quiet, Verbosity.Normal, Verbosity.Verbose })
        {
            var info = new StringWriter();
            var error = new StringWriter();
            var log = new CliLog(verbosity, info, error);

            log.Info("Connecting with Host=h;Password=hunter2;Database=d");
            log.Warning("Warning: password=hunter2");
            log.Error("Failed: postgresql://user:hunter2@host/db");
            log.Detail("CONNECTION 'host=h password=hunter2'");

            var all = info.ToString() + error;
            Assert.IsFalse(all.Contains("hunter2", StringComparison.Ordinal), $"{verbosity}: {all}");
        }
    }

    [TestMethod]
    public void TheLevelsFilterAsDocumented()
    {
        var quiet = Write(Verbosity.Quiet);
        var normal = Write(Verbosity.Normal);
        var verbose = Write(Verbosity.Verbose);

        Assert.AreEqual(("", "error\n"), quiet);
        Assert.AreEqual(("info\n", "warning\nerror\n"), normal);
        Assert.AreEqual(("info\n", "warning\nerror\n[verbose] detail\n"), verbose);

        static (string Info, string Error) Write(Verbosity verbosity)
        {
            var info = new StringWriter { NewLine = "\n" };
            var error = new StringWriter { NewLine = "\n" };
            var log = new CliLog(verbosity, info, error);
            log.Info("info");
            log.Warning("warning");
            log.Error("error");
            log.Detail("detail");
            return (info.ToString(), error.ToString());
        }
    }

    private static (int ExitCode, string Out, string Error) Run(TempProject? project, string command, params string[] args)
    {
        var context = new CliContext();
        var root = new RootCommand("pgproj") { BuildCommand.Create(context) };
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            string[] arguments = project is null ? [command, .. args] : [command, "--project", project.ProjectFile, .. args];
            var exitCode = root.Parse(arguments).Invoke();
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
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

        public static TempProject Create(string sql)
        {
            var directory = Path.Combine(Path.GetTempPath(), "pgproj-cli-tests-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "pgproj.json"), """{ "name": "log_options_test", "version": "1.0", "source": "./" }""");
            File.WriteAllText(Path.Combine(directory, "t.sql"), sql);
            return new TempProject(directory);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
