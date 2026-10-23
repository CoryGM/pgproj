using System.CommandLine;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-304d: the guide to promoting between environments is linked from where a reader looks, and what it tells them to type exists: every option and variable it names is
/// one the commands have.
/// </summary>
[TestClass]
public sealed class PromotionGuideTests
{
    private static string Root => SchemaPerTenantSampleTests.FindRepositoryRoot();

    private static string Guide => File.ReadAllText(Path.Combine(Root, "docs", "notes", "promoting-environments.md")).ReplaceLineEndings("\n"); // a Windows checkout has CRLF

    [TestMethod]
    public void TheGuideIsLinkedFromTheReadmeAndTheNotesIndex()
    {
        StringAssert.Contains(File.ReadAllText(Path.Combine(Root, "README.md")), "docs/notes/promoting-environments.md");
        StringAssert.Contains(File.ReadAllText(Path.Combine(Root, "docs", "notes", "README.md")), "(promoting-environments.md)");
    }

    [TestMethod]
    public void EveryOptionTheGuideShowsOnACommandLineExistsOnThatCommand()
    {
        var context = new CliContext();
        var commands = new Dictionary<string, Command>
        {
            ["publish"] = PublishCommand.Create(context),
            ["diff"] = DiffCommand.Create(context),
            ["build"] = BuildCommand.Create(context),
            ["document"] = DocumentCommand.Create(context),
        };

        var shown = 0;
        foreach (var line in Guide.Split('\n').Where(static line => line.TrimStart().StartsWith("$ pgproj ", StringComparison.Ordinal)))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = commands[words[2]];
            foreach (var option in words.Where(static word => word.StartsWith("--", StringComparison.Ordinal)).Select(static word => word.Split('=')[0]))
            {
                Assert.IsTrue(command.Options.Any(candidate => candidate.Name == option), $"{words[2]} has no {option}: {line}");
                shown++;
            }
        }

        Assert.IsGreaterThan(10, shown);
    }

    [TestMethod]
    public void TheVariablesTheGuideNamesAreTheOnesTheCommandsRead()
    {
        StringAssert.Contains(Guide, ConnectionSettings.SourceVariable);
        StringAssert.Contains(Guide, ConnectionSettings.TargetVariable);
        StringAssert.Contains(Guide, "--allow-destructive");
        StringAssert.Contains(Guide, "--exit-code");
        Assert.IsTrue(DiffCommand.DifferencesExitCode == 2 && Guide.Contains("2 when they differ", StringComparison.Ordinal));
    }
}
