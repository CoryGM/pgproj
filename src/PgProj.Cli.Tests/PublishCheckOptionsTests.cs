using System.CommandLine;

namespace PgProj.Cli.Tests;

/// <summary>
/// The options and help of <c>publish</c> and <c>diff</c> for the checks that moved into the engine (G-309c, G-316): the lock timeout and warnings (G-270), the lock
/// limit (G-280), superuser statements (G-265), and PL/pgSQL body notes (G-303b). The checks themselves are tested in <c>PgProj.Engine.Tests</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PublishCheckOptionsTests
{
    [TestMethod]
    [DataRow("5s", true)]
    [DataRow("500ms", true)]
    [DataRow("2min", true)]
    [DataRow("1500", true)]
    [DataRow("1.5s", true)]
    [DataRow("five seconds", false)]
    [DataRow("5s; DROP TABLE t", false)]
    [DataRow("'5s'", false)]
    public void ALockTimeoutIsAPostgreSqlDuration(string value, bool valid)
        => Assert.AreEqual(valid, PublishCommand.IsDuration(value));

    [TestMethod]
    public void ThePublishHelpDescribesTheLockTimeoutAndTheWarnings()
    {
        var command = PublishCommand.Create(new CliContext());
        var option = command.Options.Single(static option => option.Name == "--lock-timeout");

        Assert.AreEqual("duration", option.HelpName);
        StringAssert.Contains(option.Description, "rolled back");
        StringAssert.Contains(command.Description, "--lock-timeout 5s");
        StringAssert.Contains(command.Description, "rewrites, scans, or blocks writes to a table that already exists");
    }

    [TestMethod]
    public void PublishTakesTheOptionAndItsHelpSaysWhatItDoes()
    {
        var command = PublishCommand.Create(new CliContext());
        var root = new RootCommand("pgproj") { command };

        Assert.IsEmpty(root.Parse(["publish", "--execute", "--allow-lock-limit"]).Errors);
        StringAssert.Contains(command.Description, "unless --allow-lock-limit is also given");
        StringAssert.Contains(command.Options.Single(static option => option.Name == "--allow-lock-limit").Description, "out of shared memory");
    }

    [TestMethod]
    public void ThePublishHelpDescribesTheOption()
    {
        var command = PublishCommand.Create(new CliContext());
        var option = command.Options.Single(static option => option.Name == "--allow-superuser-statements");

        StringAssert.Contains(option.Description, "event trigger");
        StringAssert.Contains(option.Description, "LEAKPROOF");
        StringAssert.Contains(option.Description, "TRIGGER ALL");
        StringAssert.Contains(option.Description, "untrusted language");
        StringAssert.Contains(command.Description, "--execute --allow-superuser-statements");
    }

    [TestMethod]
    public void TheHelpOfDiffAndPublishMentionsIt()
    {
        foreach (var command in new System.CommandLine.Command[] { DiffCommand.Create(new CliContext()), PublishCommand.Create(new CliContext()) })
        {
            StringAssert.Contains(command.Description, "something the body of a PL/pgSQL routine names", command.Name);
        }
    }
}
