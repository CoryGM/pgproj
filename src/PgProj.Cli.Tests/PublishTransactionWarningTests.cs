namespace PgProj.Cli.Tests;

/// <summary>
/// G-165 (deferred, #16): the script <c>publish</c> prints or writes is not wrapped in a transaction, so the command warns and its help says so;
/// <c>--execute</c> runs the script in a single transaction and does not warn. The warning's wording is what the Docker test
/// <c>TablespaceEndToEndTests</c> and the README quote.
/// </summary>
[TestClass]
public sealed class PublishTransactionWarningTests
{
    [TestMethod]
    public void TheHelpSaysOnlyExecuteRunsInATransaction()
    {
        var command = PublishCommand.Create(new CliContext());

        StringAssert.Contains(command.Description!, "--execute runs the script in a single transaction");
        StringAssert.Contains(command.Description!, "psql --single-transaction -f <file>");
    }

    [TestMethod]
    public void TheWarningNamesBothWaysToRunTheScriptAtomically()
    {
        StringAssert.StartsWith(PublishCommand.ScriptNotTransactionalWarning, "Warning: the script is not wrapped in a transaction;");
        StringAssert.Contains(PublishCommand.ScriptNotTransactionalWarning, "psql --single-transaction -f <file>");
        StringAssert.Contains(PublishCommand.ScriptNotTransactionalWarning, "pgproj publish --execute");
    }
}
