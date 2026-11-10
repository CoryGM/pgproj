namespace PgProj.Cli.Tests;

/// <summary>
/// G-165 (the user's design of 2026-11-09): the script <c>publish</c> prints or writes carries its own transaction (<c>BEGIN;</c> ... <c>COMMIT;</c>), so a plain
/// script is no longer warned about and the help no longer advises <c>psql --single-transaction</c>; a <c>--concurrently</c> script is still warned about,
/// since its last part runs after the commit. These replace the tests of the earlier warning (G-165 deferred, when the warning was the resolution).
/// </summary>
[TestClass]
public sealed class PublishTransactionWarningTests
{
    [TestMethod]
    public void TheHelpSaysAPrintedOrWrittenScriptCarriesItsTransaction()
    {
        var command = PublishCommand.Create(new CliContext());

        StringAssert.Contains(command.Description!, "--execute runs the script in a single transaction");
        StringAssert.Contains(command.Description!, "carries the same transaction (BEGIN; ... COMMIT;)");
        StringAssert.Contains(command.Description!, "do not add psql's --single-transaction");
        Assert.IsFalse(command.Description!.Contains("psql --single-transaction -f", StringComparison.Ordinal), "the old advice is gone from the help");
    }

    [TestMethod]
    public void TheConcurrentlyWarningSaysItsLastPartRunsAfterTheCommit()
    {
        StringAssert.Contains(PublishCommand.ConcurrentScriptWarning, "run outside the script's transaction, after its COMMIT");
        StringAssert.Contains(PublishCommand.ConcurrentScriptWarning, "psql -v ON_ERROR_STOP=1 -f <file>");
        StringAssert.Contains(PublishCommand.ConcurrentScriptWarning, "pgproj publish --execute --concurrently");
    }
}
