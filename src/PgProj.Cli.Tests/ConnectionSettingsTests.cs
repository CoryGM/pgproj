using System.CommandLine;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-304a: a connection has a direction. <c>init</c> and <c>document</c> read a <b>source</b>, <c>diff</c> and <c>publish</c> compare with and change a <b>target</b>, and each is
/// taken from the option, then an environment variable (so a CI job keeps a password off the command line), then <c>pgproj.json</c>. <c>--connection</c> is gone, with no alias.
/// </summary>
[TestClass]
public sealed class ConnectionSettingsTests
{
    private static Func<string, string?> Env(string? source = null, string? target = null)
        => name => name switch
        {
            ConnectionSettings.SourceVariable => source,
            ConnectionSettings.TargetVariable => target,
            _ => null,
        };

    [TestMethod]
    public void TheTargetIsTheOptionThenTheVariableThenTheProject()
    {
        Assert.AreEqual("option", ConnectionSettings.ResolveTarget("option", "project", Env(target: "variable")));
        Assert.AreEqual("variable", ConnectionSettings.ResolveTarget(null, "project", Env(target: "variable")));
        Assert.AreEqual("project", ConnectionSettings.ResolveTarget(null, "project", Env()));
    }

    [TestMethod]
    public void ABlankValueIsSkippedAtEachStep()
    {
        Assert.AreEqual("variable", ConnectionSettings.ResolveTarget("  ", "project", Env(target: "variable")));
        Assert.AreEqual("project", ConnectionSettings.ResolveTarget("", "project", Env(target: " ")));
    }

    [TestMethod]
    public void TheSourceVariableIsNotTheTargetVariable()
    {
        Assert.AreEqual("project", ConnectionSettings.ResolveTarget(null, "project", Env(source: "the source")));
        Assert.AreEqual("project", ConnectionSettings.ResolveSource(null, "project", Env(target: "the target")));
    }

    [TestMethod]
    public void TheSourceIsTheOptionThenTheVariableThenTheProject()
    {
        Assert.AreEqual("option", ConnectionSettings.ResolveSource("option", "project", Env(source: "variable")));
        Assert.AreEqual("variable", ConnectionSettings.ResolveSource(null, "project", Env(source: "variable")));
        Assert.AreEqual("project", ConnectionSettings.ResolveSource(null, "project", Env()));
    }

    [TestMethod]
    public void InitHasNoProjectFileSoItTakesTheOptionThenTheVariable()
    {
        Assert.AreEqual("option", ConnectionSettings.ResolveInitSource("option", Env(source: "variable")));
        Assert.AreEqual("variable", ConnectionSettings.ResolveInitSource(null, Env(source: "variable")));
    }

    [TestMethod]
    public void WithNothingToGoOnEachSaysWhatWouldSupplyOne()
    {
        var target = Assert.Throws<InvalidOperationException>(() => ConnectionSettings.ResolveTarget(null, null, Env()));
        StringAssert.Contains(target.Message, "target connection string is required");
        StringAssert.Contains(target.Message, "--target-connection");
        StringAssert.Contains(target.Message, "PGPROJ_TARGET_CONNECTION");
        StringAssert.Contains(target.Message, "targetConnectionString");

        var source = Assert.Throws<InvalidOperationException>(() => ConnectionSettings.ResolveSource(null, null, Env()));
        StringAssert.Contains(source.Message, "source connection string is required");
        StringAssert.Contains(source.Message, "--source-connection");
        StringAssert.Contains(source.Message, "PGPROJ_SOURCE_CONNECTION");
        StringAssert.Contains(source.Message, "sourceConnectionString");

        var init = Assert.Throws<InvalidOperationException>(() => ConnectionSettings.ResolveInitSource(null, Env()));
        StringAssert.Contains(init.Message, "--source-connection");
        StringAssert.Contains(init.Message, "PGPROJ_SOURCE_CONNECTION");
        Assert.DoesNotContain("pgproj.json", init.Message, StringComparison.Ordinal, "init has no project file to read yet");
    }

    [TestMethod]
    public void TheVariablesHaveTheNamesTheDocumentsGive()
    {
        Assert.AreEqual("PGPROJ_SOURCE_CONNECTION", ConnectionSettings.SourceVariable);
        Assert.AreEqual("PGPROJ_TARGET_CONNECTION", ConnectionSettings.TargetVariable);
    }

    [TestMethod]
    public void EachCommandTakesItsOwnDirectionAndNotTheRemovedOption()
    {
        var context = new CliContext();
        var root = new RootCommand("pgproj")
        {
            InitCommand.Create(context),
            BuildCommand.Create(context),
            DiffCommand.Create(context),
            PublishCommand.Create(context),
            DocumentCommand.Create(context),
        };

        foreach (var command in new[] { "init", "document" })
        {
            Assert.IsEmpty(root.Parse([command, "--source-connection", "Host=x"]).Errors, command);
            Assert.IsEmpty(root.Parse([command, "--source-connection"]).Errors, command + ": a bare option reads the variable");
            Assert.IsNotEmpty(root.Parse([command, "--target-connection", "Host=x"]).Errors, command + " never has a target");
        }

        foreach (var command in new[] { "diff", "publish" })
        {
            Assert.IsEmpty(root.Parse([command, "--target-connection", "Host=x"]).Errors, command);
            Assert.IsEmpty(root.Parse([command, "--source-connection", "Host=x"]).Errors, command + " takes a source as the desired state (G-304b)");
            Assert.IsEmpty(root.Parse([command, "--source-connection"]).Errors, command + ": a bare option reads the variable");
        }

        foreach (var command in new[] { "init", "build", "diff", "publish", "document" })
        {
            Assert.IsNotEmpty(root.Parse([command, "--connection", "Host=x"]).Errors, command + ": --connection was removed, with no alias");
        }

        Assert.IsEmpty(root.Parse(["build", "--source-connection", "Host=x"]).Errors, "build can model a live database (G-304c)");
        Assert.IsEmpty(root.Parse(["build", "--source-connection"]).Errors, "a bare option reads the variable");
        Assert.IsNotEmpty(root.Parse(["build", "--target-connection", "Host=x"]).Errors, "build never takes a target");
    }

    [TestMethod]
    public void TheHelpNamesTheOptionTheVariableAndThePropertyOfEachCommand()
    {
        var targets = new[] { DiffCommand.Create(new CliContext()), PublishCommand.Create(new CliContext()) };
        foreach (var command in targets)
        {
            var option = command.Options.Single(static option => option.Name == "--target-connection");
            StringAssert.Contains(option.Description, "PGPROJ_TARGET_CONNECTION", command.Name);
            StringAssert.Contains(option.Description, "targetConnectionString", command.Name);
            StringAssert.Contains(command.Description, "PGPROJ_TARGET_CONNECTION", command.Name);
            StringAssert.Contains(command.Description, "targetConnectionString", command.Name);
            Assert.IsFalse(command.Options.Any(static option => option.Name == "--connection"), command.Name);
            StringAssert.Contains(command.Description, "Source A -> Target B", command.Name);
            StringAssert.Contains(command.Description, "--source-connection", command.Name);
            StringAssert.Contains(command.Options.Single(static option => option.Name == "--source-connection").Description, "read-only transaction", command.Name);
        }

        var init = InitCommand.Create(new CliContext());
        var source = init.Options.Single(static option => option.Name == "--source-connection");
        StringAssert.Contains(source.Description, "PGPROJ_SOURCE_CONNECTION");
        Assert.IsFalse(source.Required, "the variable can supply it");
        StringAssert.Contains(init.Description, "sourceConnectionString");
        StringAssert.Contains(init.Description, "--target-connection");

        var document = DocumentCommand.Create(new CliContext());
        var documentSource = document.Options.Single(static option => option.Name == "--source-connection");
        StringAssert.Contains(documentSource.Description, "PGPROJ_SOURCE_CONNECTION");
        StringAssert.Contains(documentSource.Description, "sourceConnectionString");
    }
}
