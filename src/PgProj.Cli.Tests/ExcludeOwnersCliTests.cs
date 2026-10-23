using System.CommandLine;
using System.Text.Json;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-146: <c>--exclude-owners</c> overrides <c>pgproj.json</c>'s <c>excludeOwners</c> for one run, in either direction, as <c>--exclude-grants</c> does (G-87).
/// The model carries the owners a project states (and dumps as JSON), and a project that states some and not all is told how many it leaves out.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ExcludeOwnersCliTests
{
    private const string OwnerSql = """
        CREATE SCHEMA app;
        CREATE TABLE app.t (id bigint NOT NULL);
        CREATE TABLE app.u (id bigint NOT NULL);
        ALTER TABLE app.t OWNER TO lc_owner;
        """;

    [TestMethod]
    public void ByDefaultTheStatedOwnerIsModeledAndTheModelStillDumps()
    {
        using var project = TempProject.Create(OwnerSql, excludeOwnersInJson: false);

        var (owners, _) = Run(project);

        Assert.AreEqual(1, owners);
    }

    [TestMethod]
    public void TheExcludeOwnersFlagRemovesTheOwnersAndTheNotice()
    {
        using var project = TempProject.Create(OwnerSql, excludeOwnersInJson: false);

        var (owners, output) = Run(project, "--exclude-owners");

        Assert.AreEqual(0, owners);
        Assert.IsFalse(output.Contains("state no owner", StringComparison.Ordinal), output);
    }

    [TestMethod]
    public void PgProjJsonExcludeOwnersIsHonoredWithoutTheCliFlag()
    {
        using var project = TempProject.Create(OwnerSql, excludeOwnersInJson: true);

        Assert.AreEqual(0, Run(project).Owners);
    }

    /// <summary>The flag is a tri-state override: <c>--exclude-owners false</c> forces owners back on although <c>pgproj.json</c> sets <c>excludeOwners: true</c>.</summary>
    [TestMethod]
    public void TheCliFlagOverridesPgProjJsonInEitherDirection()
    {
        using var project = TempProject.Create(OwnerSql, excludeOwnersInJson: true);

        Assert.AreEqual(1, Run(project, "--exclude-owners", "false").Owners);
    }

    [TestMethod]
    public void AProjectThatStatesSomeOwnersIsToldHowManyObjectsItLeavesOut()
    {
        using var project = TempProject.Create(OwnerSql, excludeOwnersInJson: false);

        var (_, output) = Run(project);

        // The schema and app.u.
        StringAssert.Contains(output, "[Info] 2 objects state no owner (ALTER ... OWNER TO); their owners are not compared.");
    }

    [TestMethod]
    public void AProjectThatStatesNoOwnerIsToldNothing()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id bigint NOT NULL);", excludeOwnersInJson: false);

        var (owners, output) = Run(project);

        Assert.AreEqual(0, owners);
        Assert.IsFalse(output.Contains("state no owner", StringComparison.Ordinal), output);
        StringAssert.Contains(output, "No diagnostics.");
    }

    private static (int Owners, string Output) Run(TempProject project, params string[] extraArgs)
    {
        var dumpPath = Path.Combine(project.Directory, "model-" + Guid.NewGuid().ToString("N") + ".json");
        var root = new RootCommand("pgproj") { BuildCommand.Create(new CliContext()) };
        var originalOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int exitCode;
        try
        {
            exitCode = root.Parse(["build", "--project", project.ProjectFile, "--dump-model", dumpPath, .. extraArgs]).Invoke();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.AreEqual(0, exitCode, captured.ToString());
        using var document = JsonDocument.Parse(File.ReadAllText(dumpPath));
        return (document.RootElement.GetProperty("Owners").EnumerateObject().Count(), captured.ToString());
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

        public static TempProject Create(string sql, bool excludeOwnersInJson)
        {
            var directory = Path.Combine(Path.GetTempPath(), "pgproj-cli-tests-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "pgproj.json"),
                $$"""{ "name": "exclude_owners_test", "version": "1.0", "source": "./"{{(excludeOwnersInJson ? ", \"excludeOwners\": true" : string.Empty)}} }""");
            File.WriteAllText(Path.Combine(directory, "t.sql"), sql);
            return new TempProject(directory);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
