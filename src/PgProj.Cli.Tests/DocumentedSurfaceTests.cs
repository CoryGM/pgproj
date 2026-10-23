using System.CommandLine;
using System.Text.RegularExpressions;
using PgProj.Core;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-305: the user documents say what the tool does, so they are checked against it. Every option a command has is described under that command in the command reference
/// (docs/commands.md, moved there from the README on 2026-10-22), every property the project file reads is in the property tables, and the removed <c>--connection</c> and
/// <c>connectionString</c> are named nowhere except to say they are gone.
/// </summary>
[TestClass]
public sealed partial class DocumentedSurfaceTests
{
    private static string Root => SchemaPerTenantSampleTests.FindRepositoryRoot();

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root, .. path])).ReplaceLineEndings("\n"); // a Windows checkout has CRLF

    /// <summary>The options every command shares, described once in the command reference.</summary>
    private static readonly string[] s_shared = ["--erd", "--erd-schemas", "--erd-exclude-schemas", "--erd-html", "--erd-existing", "--verbose", "--quiet", "--help", "--version"];

    [TestMethod]
    public void EveryOptionOfACommandIsDescribedUnderThatCommandInTheCommandReference()
    {
        var reference = Read("docs", "commands.md");
        var context = new CliContext();
        var commands = new Dictionary<string, Command>
        {
            ["init"] = InitCommand.Create(context),
            ["build"] = BuildCommand.Create(context),
            ["diff"] = DiffCommand.Create(context),
            ["publish"] = PublishCommand.Create(context),
            ["document"] = DocumentCommand.Create(context),
        };

        foreach (var (name, command) in commands)
        {
            var section = Section(reference, name);
            foreach (var option in command.Options.Select(static option => option.Name).Where(option => !s_shared.Contains(option, StringComparer.Ordinal)))
            {
                Assert.IsTrue(section.Contains($"`{option}", StringComparison.Ordinal), $"docs/commands.md, under `{name}`, does not describe {option}.");
            }
        }
    }

    [TestMethod]
    public void EveryPropertyTheProjectFileReadsIsInThePropertyTables()
    {
        var configuration = typeof(PgProjectLoader).GetNestedType("PgProjectConfiguration", System.Reflection.BindingFlags.NonPublic)!;
        var properties = configuration.GetProperties().Select(static property => char.ToLowerInvariant(property.Name[0]) + property.Name[1..]).Where(static name => name != "connectionString").ToArray();
        Assert.IsGreaterThan(10, properties.Length);

        var readme = Read("README.md");
        var reference = Read("pgproj.json.md");
        foreach (var property in properties)
        {
            Assert.IsTrue(readme.Contains($"| `{property}` |", StringComparison.OrdinalIgnoreCase), $"README's project file table lacks {property}.");
            Assert.IsTrue(reference.Contains($"| `{property}`", StringComparison.OrdinalIgnoreCase), $"pgproj.json.md's fields table lacks {property}.");
        }
    }

    [TestMethod]
    public void TheRemovedConnectionIsNamedOnlyToSayItIsGone()
    {
        var files = new List<string> { "README.md", "pgproj.json.md", "documentation.md", "DEVELOPING.md", Path.Combine("docs", "ai", "copilot-cli.md") };
        var ruleDocs = Path.Combine(Root, "docs", "ai") + Path.DirectorySeparatorChar;
        files.AddRange(Directory.GetFiles(Path.Combine(Root, "docs"), "*.md", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(ruleDocs, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root, path)));
        files.AddRange(Directory.GetFiles(Path.Combine(Root, "samples"), "README.md", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(Root, path)));

        foreach (var file in files)
        {
            foreach (var (line, index) in Read(file).Split('\n').Select(static (line, index) => (line, index)))
            {
                var removed = RemovedOption().IsMatch(line) || line.Contains("`connectionString`", StringComparison.Ordinal) || line.Contains("'connectionString'", StringComparison.Ordinal);
                if (!removed)
                {
                    continue;
                }

                Assert.IsTrue(
                    line.Contains("gone", StringComparison.Ordinal) || line.Contains("removed", StringComparison.Ordinal) || line.Contains("no longer", StringComparison.Ordinal) || line.Contains("replace", StringComparison.Ordinal) || line.Contains("refused", StringComparison.Ordinal) || line.Contains("used to", StringComparison.Ordinal),
                    $"{file}:{index + 1} names the removed --connection or connectionString as if it still exists: {line.Trim()}");
            }
        }
    }

    [TestMethod]
    public void TheReadmeNamesTheVariablesAndThePropertiesOfBothDirections()
    {
        var connections = Read("README.md");
        foreach (var text in new[] { ConnectionSettings.SourceVariable, ConnectionSettings.TargetVariable, "sourceConnectionString", "targetConnectionString", "--source-connection", "--target-connection" })
        {
            StringAssert.Contains(connections, text);
        }

        StringAssert.Contains(connections, "## Connections");
        StringAssert.Contains(connections, "## Three ways to use it");
    }

    [TestMethod]
    public void TheCommandReferenceDescribesTheJsonOfBuildAndDiffAndTheReadmeLinksTheLibraryPages()
    {
        // G-309g: build and diff write the engine's document alike, and the library and the shell guide are reached from the README.
        var reference = Read("docs", "commands.md");
        var readme = Read("README.md");
        foreach (var command in new[] { "build", "diff" })
        {
            var section = Section(reference, command);
            StringAssert.Contains(section, "`--format text|json`", $"docs/commands.md, under `{command}`");
            Assert.IsTrue(section.Split('\n').Any(line => line.StartsWith($"pgproj {command} ", StringComparison.Ordinal) && line.TrimEnd().EndsWith("--format json", StringComparison.Ordinal)), $"docs/commands.md, under `{command}`, has no --format json example");
        }

        StringAssert.Contains(readme, "## Using pgproj as a library");
        foreach (var page in new[] { "docs/library.md", "docs/shells.md" })
        {
            StringAssert.Contains(readme, $"]({page})", $"README links {page}");
            Assert.IsTrue(File.Exists(Path.Combine(Root, page)), page);
        }
    }

    private static string Section(string document, string command)
    {
        var start = document.IndexOf($"\n### {command}\n", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, $"The command reference has no section for {command}.");
        var next = document.IndexOf("\n### ", start + 5, StringComparison.Ordinal);
        var chapter = document.IndexOf("\n## ", start + 5, StringComparison.Ordinal);
        var end = new[] { next, chapter }.Where(static position => position >= 0).DefaultIfEmpty(document.Length).Min();
        return document[start..end];
    }

    [GeneratedRegex(@"--connection\b")]
    private static partial Regex RemovedOption();
}
