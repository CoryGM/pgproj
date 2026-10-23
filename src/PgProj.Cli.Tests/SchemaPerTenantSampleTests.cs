using System.CommandLine;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-290f: the sample project <c>samples/SchemaPerTenant</c> builds without a diagnostic, as its README says. The Docker suite
/// publishes it (<c>SchemaPerTenantSampleEndToEndTests</c>).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SchemaPerTenantSampleTests
{
    [TestMethod]
    public async Task TheSampleBuildsWithoutADiagnostic()
    {
        var project = Path.Combine(FindRepositoryRoot(), "samples", "SchemaPerTenant", "pgproj.json");

        var (exitCode, output) = await RunAsync("build", "--project", project);

        Assert.AreEqual(0, exitCode, output);
        StringAssert.Contains(output, "No diagnostics.");
        StringAssert.Contains(output, "(11 SQL file(s)), checked in the stand-in schema shop");
        StringAssert.Contains(output, "Tenants: 3 listed in");
    }

    /// <summary>The folder holding <c>pgproj.slnx</c>, found from the test's own folder upwards.</summary>
    internal static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "pgproj.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("pgproj.slnx was not found above " + AppContext.BaseDirectory);
    }

    internal static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var context = new CliContext();
        var root = new RootCommand("pgproj") { BuildCommand.Create(context) };
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            return (await root.Parse(args).InvokeAsync(), output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
