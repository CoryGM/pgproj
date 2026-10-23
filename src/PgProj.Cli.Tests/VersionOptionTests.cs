using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-167: <c>pgproj --version</c> prints the version and exits 0. The option is System.CommandLine's default one on the root command, and the
/// version is the assembly's informational version, which MinVer sets from the git tag (G-81). The option reads the version from the entry
/// assembly, which inside a test is the test host, so these tests start pgproj (<c>pgproj.dll</c>, copied next to the tests) as its own
/// process. That also runs the real root command of <c>Program.cs</c> rather than one built for the test.
/// </summary>
[TestClass]
public sealed class VersionOptionTests
{
    // A semantic version, with an optional pre-release and build metadata (the commit the SDK appends).
    private static readonly Regex SemanticVersion = new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$");

    [TestMethod]
    public void VersionPrintsTheInformationalVersionAndExitsZero()
    {
        var expected = typeof(CliContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var run = Run("--version");

        Assert.AreEqual(0, run.ExitCode, run.Error);
        Assert.IsNotNull(expected);
        Assert.AreEqual(expected, run.Out.Trim());
        Assert.IsTrue(SemanticVersion.IsMatch(run.Out.Trim()), run.Out);
        Assert.AreEqual(string.Empty, run.Error);
    }

    [TestMethod]
    public void RootHelpListsTheVersionOption()
    {
        var run = Run("--help");

        Assert.AreEqual(0, run.ExitCode, run.Error);
        StringAssert.Contains(run.Out, "--version");
    }

    // Runs pgproj.dll (the real Program.cs) through the dotnet host that runs the tests (DOTNET_HOST_PATH, set by `dotnet test`), or `dotnet` on the path.
    internal static (int ExitCode, string Out, string Error) Run(params string[] args)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path ? path : "dotnet";
        var startInfo = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "pgproj.dll"));
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.Result);
    }
}
