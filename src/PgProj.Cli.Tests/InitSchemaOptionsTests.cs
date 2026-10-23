using System.CommandLine;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-157: <c>init --schemas</c>, <c>--exclude-schemas</c> (and the undocumented <c>--excludeSchemas</c>), and <c>--report</c> as the command line sees them: the two schema
/// options cannot be combined, lists are comma-separated or repeated, help documents the kebab-case names, and the list of kinds <c>init</c> cannot read is one bullet per line.
/// The scaffold itself needs a database and is covered by the Docker tests.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class InitSchemaOptionsTests
{
    private const string UnreachableConnection = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1";

    [TestMethod]
    public async Task GivingBothSchemaOptionsIsAnErrorAndNothingIsWritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-init-schemas-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exitCode, output) = await RunAsync(["--project", Path.Combine(directory, "pgproj.json"), "--source-connection", UnreachableConnection, "--schemas", "app", "--exclude-schemas", "audit"]);

            Assert.AreNotEqual(0, exitCode);
            StringAssert.Contains(output, "--schemas and --exclude-schemas cannot be used together.");
            Assert.IsFalse(Directory.Exists(directory), "Nothing is written when the options are refused.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task AnEmptyListIsAnError()
    {
        var (exitCode, output) = await RunAsync(["--source-connection", UnreachableConnection, "--schemas", ","]);

        Assert.AreNotEqual(0, exitCode);
        StringAssert.Contains(output, "A list of schema names is needed");
    }

    [TestMethod]
    public void ListsAreCommaSeparatedOrRepeatedAndBlanksAndRepeatsAreDropped()
    {
        CollectionAssert.AreEqual(new[] { "app", "reporting", "public" }, InitCommand.SplitSchemaNames(["app, reporting", "public", "app", " "]).ToArray());
        Assert.IsEmpty(InitCommand.SplitSchemaNames([",", " , "]).ToArray());
        Assert.IsEmpty(InitCommand.SplitSchemaNames(null).ToArray());
    }

    [TestMethod]
    public void TheCamelCaseSpellingIsAcceptedAsTheKebabCaseOne()
    {
        CollectionAssert.AreEqual(
            new[] { "init", "--exclude-schemas", "a,b", "--exclude-schemas=c", "--exclude-schemas-other", "--schemas", "x" },
            CommandLineAliases.Normalize(["init", "--excludeSchemas", "a,b", "--excludeSchemas=c", "--exclude-schemas-other", "--schemas", "x"]));
        CollectionAssert.AreEqual(new[] { "--excludeSchemasX" }, CommandLineAliases.Normalize(["--excludeSchemasX"]));
    }

    [TestMethod]
    public async Task HelpDocumentsTheKebabCaseNamesOnly()
    {
        var (_, output) = await RunAsync(["--help"]);

        StringAssert.Contains(output, "--schemas");
        StringAssert.Contains(output, "--exclude-schemas");
        StringAssert.Contains(output, "--report");
        Assert.IsFalse(output.Contains("excludeSchemas", StringComparison.Ordinal), output);
    }

    [TestMethod]
    public async Task TheKindsInitCannotReadAreOneBulletPerLineInHelpAndInTheNotice()
    {
        var (_, help) = await RunAsync(["--help"]);
        var helpLines = help.Split('\n').Select(static line => line.TrimEnd('\r')).ToArray();

        foreach (var kind in InitCommand.UnreadObjectKinds)
        {
            Assert.IsTrue(helpLines.Any(line => line.Trim() == "- " + kind), $"Help has no bullet for: {kind}");
        }

        var noticeLines = InitCommand.UnreadObjectKindsNotice.Split(["\r\n", "\n"], StringSplitOptions.None);
        Assert.AreEqual(InitCommand.UnreadObjectKinds.Length + 2, noticeLines.Length);
        Assert.IsTrue(noticeLines[0].EndsWith("even if the database has them:", StringComparison.Ordinal));
        Assert.IsTrue(noticeLines[1..^1].All(static line => line.StartsWith("  - ", StringComparison.Ordinal)));
        Assert.IsTrue(noticeLines[^1].StartsWith("Check for them", StringComparison.Ordinal));
        Assert.IsTrue(InitCommand.UnreadObjectKinds.All(static kind => !kind.Contains("; ", StringComparison.Ordinal)), "A kind that is several kinds belongs on several lines.");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string[] args)
    {
        var root = new RootCommand("pgproj") { InitCommand.Create(new CliContext()) };
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            return (await root.Parse(["init", .. args]).InvokeAsync(), output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
