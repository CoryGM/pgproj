using System.CommandLine;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-168: <c>--help</c> was a wall of text. The root listed every command with its whole description, and a command's help put each option
/// on one long line with nothing between options or sections. The layout now prints the root's commands one summary each, a command's
/// description, options, and examples as separate sections, each option on its own line with its description wrapped and indented under it
/// and a blank line after it, and the examples last.
/// </summary>
[TestClass]
public sealed class HelpLayoutTests
{
    private const int Width = 100;

    private static readonly string[] CommandNames = ["init", "build", "diff", "publish", "document", "about"];

    [TestMethod]
    public void TheRootListsEachCommandOnOneLineWithItsSummaryAndABlankLineBetween()
    {
        var run = VersionOptionTests.Run("--help");
        var lines = Lines(run.Out);

        Assert.AreEqual(0, run.ExitCode, run.Error);
        var commands = lines.IndexOf("Commands:");
        Assert.IsTrue(commands > 0, run.Out);
        var block = lines[(commands + 1)..lines.IndexOf("Options:")];
        var named = block.Select((line, index) => (line, index)).Where(static entry => entry.line.Length > 2 && entry.line[2] != ' ').ToList();
        CollectionAssert.AreEqual(CommandNames, named.Select(static entry => entry.line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).ToArray(), run.Out);
        foreach (var (line, index) in named)
        {
            Assert.IsTrue(index == 0 || block[index - 1].Length == 0, $"No blank line before: {line}");
            Assert.IsTrue(line.Trim().Split(' ', 2)[1].Trim().Length > 0, $"No summary: {line}");
        }

        // A command's long description is in its own help, not the root's.
        Assert.IsFalse(run.Out.Contains("Not read by pgproj", StringComparison.Ordinal), run.Out);
        Assert.IsFalse(run.Out.Contains("pgproj init --source-connection \"Host=localhost;Database=app\" --force", StringComparison.Ordinal), run.Out);
        StringAssert.Contains(run.Out, "Run 'pgproj <command> --help'");
        StringAssert.Contains(run.Out, "--version");
    }

    [TestMethod]
    public void ACommandsHelpHasItsSectionsApartWithTheExamplesLast()
    {
        foreach (var name in CommandNames)
        {
            var run = VersionOptionTests.Run(name, "--help");
            var lines = Lines(run.Out);

            Assert.AreEqual(0, run.ExitCode, $"{name}: {run.Error}");
            var headings = new[] { "Usage:", "Description:", "Options:", "Examples:" }.Select(heading => lines.IndexOf(heading)).ToArray();
            Assert.IsTrue(headings.All(static index => index > 0), $"{name}:\n{run.Out}");
            CollectionAssert.AreEqual(headings.Order().ToArray(), headings, $"{name}: the sections are out of order");
            foreach (var heading in headings)
            {
                Assert.AreEqual(string.Empty, lines[heading - 1], $"{name}: no blank line before '{lines[heading]}'");
            }

            StringAssert.StartsWith(lines[headings[1] - 2], "  pgproj " + name, $"{name}: the usage line");
            Assert.IsTrue(lines.Skip(headings[3] + 1).Where(static line => line.Length > 0).All(line => line.StartsWith("  pgproj " + name, StringComparison.Ordinal)), $"{name}: an example is not a command line");
        }
    }

    [TestMethod]
    public void EachOptionIsOnItsOwnLineWithItsDescriptionIndentedUnderItAndABlankLineAfter()
    {
        var context = new CliContext();
        foreach (var command in new[] { InitCommand.Create(context), BuildCommand.Create(context), DiffCommand.Create(context), PublishCommand.Create(context), DocumentCommand.Create(context) })
        {
            var lines = Lines(HelpLayout.Render(command, Width));
            var start = lines.IndexOf("Options:") + 1;
            var end = lines.IndexOf("Examples:") - 1;
            var block = lines[start..end];

            foreach (var option in command.Options)
            {
                var signature = block.FindIndex(line => line.StartsWith("  " + HelpLayout.OptionSignature(option), StringComparison.Ordinal));
                Assert.IsTrue(signature >= 0, $"{command.Name}: no line for {option.Name}");
                Assert.IsTrue(signature == 0 || block[signature - 1].Length == 0, $"{command.Name}: no blank line before {option.Name}");
                StringAssert.StartsWith(block[signature + 1], "      ", $"{command.Name}: {option.Name}'s description is not under it");
                Assert.IsFalse(string.IsNullOrWhiteSpace(block[signature + 1]), $"{command.Name}: {option.Name} has no description");
            }

            Assert.IsTrue(block.All(static line => line.Length == 0 || line.StartsWith("  -", StringComparison.Ordinal) || line.StartsWith("      ", StringComparison.Ordinal)), command.Name);
        }
    }

    [TestMethod]
    public void NoLineIsWiderThanTheWidthUnlessOneWordIs()
    {
        var context = new CliContext();
        foreach (var command in new[] { InitCommand.Create(context), BuildCommand.Create(context), DiffCommand.Create(context), PublishCommand.Create(context), DocumentCommand.Create(context) })
        {
            foreach (var line in Lines(HelpLayout.Render(command, Width)).Where(static line => line.Length > Width))
            {
                Assert.IsFalse(line.Trim().Contains(' ', StringComparison.Ordinal) && !line.TrimStart().StartsWith("pgproj ", StringComparison.Ordinal), $"{command.Name}: {line}");
            }
        }
    }

    [TestMethod]
    public void TheExamplesAndTheDescriptionKeepEveryLineOfTheCommandsText()
    {
        var command = DiffCommand.Create(new CliContext());
        var help = HelpLayout.Render(command, 1000);
        var lines = Lines(help);

        foreach (var line in command.Description!.Split('\n').Select(static line => line.Trim()).Where(static line => line.Length > 0 && line != "Examples:"))
        {
            Assert.IsTrue(lines.Any(rendered => rendered.Trim() == line), $"Missing: {line}");
        }
    }

    [TestMethod]
    public void AListItemAndASecondColumnWrapUnderTheirText()
    {
        var item = HelpLayout.WrapLine("  - one two three four five six seven eight nine ten eleven", 30).ToArray();
        CollectionAssert.AreEqual(new[] { "  - one two three four five", "    six seven eight nine ten", "    eleven" }, item);

        var term = HelpLayout.WrapLine("  @pii    true or false, yes and no, and more words here", 34).ToArray();
        CollectionAssert.AreEqual(new[] { "  @pii    true or false, yes and", "          no, and more words here" }, term);

        var plain = HelpLayout.WrapLine("    one two three four five six seven", 28).ToArray();
        CollectionAssert.AreEqual(new[] { "    one two three four five", "    six seven" }, plain);
    }

    [TestMethod]
    public void AnOptionShowsItsShortAliasItsValueAndWhetherItIsRequired()
    {
        var init = InitCommand.Create(new CliContext());
        var help = string.Join('\n', Lines(HelpLayout.Render(init, Width)));

        StringAssert.Contains(help, "  -o, --output <directory>\n");
        StringAssert.Contains(help, "  --source-connection [<connection-string>]\n");
        StringAssert.Contains(help, "  --erd [<path>]\n");
        StringAssert.Contains(help, "  --force\n");
        StringAssert.Contains(help, "init [options]\n");

        // No command has a required option now (G-304a: a connection can come from an environment variable), so the marker is checked on a command of its own.
        var needy = new Command("needy") { new Option<string>("--needed") { HelpName = "thing", Required = true } };
        StringAssert.Contains(string.Join('\n', Lines(HelpLayout.Render(needy, Width))), "  --needed <thing>  (required)\n");
    }

    [TestMethod]
    public void TheHelpEndsWithTwoBlankLinesBeforeTheNextPrompt()
    {
        foreach (var args in new[] { new[] { "--help" }, new[] { "init", "--help" }, new[] { "document", "--help" } })
        {
            var run = VersionOptionTests.Run(args);
            var lines = Lines(run.Out);

            // The text ends "last line\n\n\n": the last three entries are the two blank lines and what follows the final newline.
            CollectionAssert.AreEqual(new[] { string.Empty, string.Empty, string.Empty }, lines[^3..], string.Join(' ', args));
            Assert.AreNotEqual(string.Empty, lines[^4], string.Join(' ', args));
        }
    }

    [TestMethod]
    public void AParseErrorStillFailsAndShowsTheCommandsHelp()
    {
        var run = VersionOptionTests.Run("init", "--no-such-option");

        Assert.AreEqual(1, run.ExitCode);
        StringAssert.Contains(run.Error + run.Out, "--no-such-option");
        StringAssert.Contains(run.Out, "Options:");
    }

    private static List<string> Lines(string text) => [.. text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')];
}
