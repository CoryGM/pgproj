using System.CommandLine;

namespace PgProj.Cli.Tests;

/// <summary>G-169: <c>publish</c> takes <c>--comments-only</c>, and its help explains what it deploys and what it skips.</summary>
[TestClass]
public sealed class CommentsOnlyOptionTests
{
    [TestMethod]
    public void PublishTakesTheOptionAndNoOtherCommandDoes()
    {
        Assert.IsTrue(PublishCommand.Create(new CliContext()).Options.Any(static option => option.Name == "--comments-only"));
        Assert.IsFalse(DiffCommand.Create(new CliContext()).Options.Any(static option => option.Name == "--comments-only"));
        Assert.IsFalse(BuildCommand.Create(new CliContext()).Options.Any(static option => option.Name == "--comments-only"));
    }

    [TestMethod]
    public void TheOptionIsOptional()
    {
        var option = PublishCommand.Create(new CliContext()).Options.Single(static option => option.Name == "--comments-only");

        Assert.IsFalse(option.Required);
        Assert.IsFalse(option.Description!.Length == 0);
    }

    [TestMethod]
    public void TheHelpSaysWhatIsDeployedWhatIsSkippedAndGivesExamples()
    {
        var command = PublishCommand.Create(new CliContext());

        StringAssert.Contains(command.Description!, "--comments-only deploys just the COMMENT ON statements");
        StringAssert.Contains(command.Description!, "the predeploy and postdeploy scripts are not included");
        StringAssert.Contains(command.Description!, "skipped a comment");
        StringAssert.Contains(command.Description!, "--comments-only --execute");
        StringAssert.Contains(command.Description!, "--comments-only --output ./comments.sql");
        var option = command.Options.Single(static option => option.Name == "--comments-only");
        StringAssert.Contains(option.Description!, "A comment on an object the database does not have yet");
    }
}
