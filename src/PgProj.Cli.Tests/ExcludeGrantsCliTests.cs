using System.CommandLine;
using System.Text.Json;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-87: <c>--exclude-grants</c>/<c>--exclude-role-memberships</c> override <c>pgproj.json</c>'s
/// <c>excludeGrants</c>/<c>excludeRoleMemberships</c> for a single run, the same override relationship
/// <c>--target-connection</c> has with <c>targetConnectionString</c>. Uses <c>build --dump-model</c> (no live database
/// needed) to inspect what actually reached the model.
/// </summary>
[TestClass]
public sealed class ExcludeGrantsCliTests
{
    private const string GrantSql = """
        CREATE TABLE public.t (id bigint NOT NULL);
        GRANT SELECT ON public.t TO reader;
        GRANT admin TO app_user;
        """;

    [TestMethod]
    public void ByDefaultBothGrantAndRoleMembershipAreModeled()
    {
        using var project = TempProject.Create(GrantSql, excludeGrantsInJson: false, excludeRoleMembershipsInJson: false);

        Assert.AreEqual(2, GrantCount(project));
    }

    [TestMethod]
    public void ExcludeGrantsFlagRemovesOnlyTheObjectGrant()
    {
        using var project = TempProject.Create(GrantSql, excludeGrantsInJson: false, excludeRoleMembershipsInJson: false);

        Assert.AreEqual(1, GrantCount(project, "--exclude-grants"));
    }

    [TestMethod]
    public void ExcludeRoleMembershipsFlagRemovesOnlyTheRoleMembership()
    {
        using var project = TempProject.Create(GrantSql, excludeGrantsInJson: false, excludeRoleMembershipsInJson: false);

        Assert.AreEqual(1, GrantCount(project, "--exclude-role-memberships"));
    }

    [TestMethod]
    public void BothFlagsTogetherRemoveEverything()
    {
        using var project = TempProject.Create(GrantSql, excludeGrantsInJson: false, excludeRoleMembershipsInJson: false);

        Assert.AreEqual(0, GrantCount(project, "--exclude-grants", "--exclude-role-memberships"));
    }

    [TestMethod]
    public void PgProjJsonExcludeGrantsIsHonoredWithoutTheCliFlag()
    {
        using var project = TempProject.Create(GrantSql, excludeGrantsInJson: true, excludeRoleMembershipsInJson: false);

        Assert.AreEqual(1, GrantCount(project));
    }

    /// <summary>
    /// The CLI flag is a tri-state override, not a one-way switch: passing <c>--exclude-grants false</c> must
    /// force grants back on even though <c>pgproj.json</c> sets <c>excludeGrants: true</c>.
    /// </summary>
    [TestMethod]
    public void CliFlagOverridesPgProjJsonInEitherDirection()
    {
        using var project = TempProject.Create(GrantSql, excludeGrantsInJson: true, excludeRoleMembershipsInJson: false);

        Assert.AreEqual(2, GrantCount(project, "--exclude-grants", "false"));
    }

    private static int GrantCount(TempProject project, params string[] extraArgs)
    {
        var dumpPath = Path.Combine(project.Directory, "model-" + Guid.NewGuid().ToString("N") + ".json");
        var root = new RootCommand("pgproj") { BuildCommand.Create(new CliContext()) };
        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        int exitCode;
        try
        {
            exitCode = root.Parse(["build", "--project", project.ProjectFile, "--dump-model", dumpPath, .. extraArgs]).Invoke();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.AreEqual(0, exitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(dumpPath));
        return document.RootElement.GetProperty("Grants").EnumerateObject().Count();
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

        public static TempProject Create(string sql, bool excludeGrantsInJson, bool excludeRoleMembershipsInJson)
        {
            var directory = Path.Combine(Path.GetTempPath(), "pgproj-cli-tests-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var extraFields = string.Empty;
            if (excludeGrantsInJson)
            {
                extraFields += ", \"excludeGrants\": true";
            }

            if (excludeRoleMembershipsInJson)
            {
                extraFields += ", \"excludeRoleMemberships\": true";
            }

            File.WriteAllText(
                Path.Combine(directory, "pgproj.json"),
                $$"""{ "name": "exclude_grants_test", "version": "1.0", "source": "./"{{extraFields}} }""");
            File.WriteAllText(Path.Combine(directory, "t.sql"), sql);
            return new TempProject(directory);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
