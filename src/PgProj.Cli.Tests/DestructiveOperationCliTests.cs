namespace PgProj.Cli.Tests;

/// <summary>
/// G-35: <c>publish --execute</c> fails closed unless <c>--allow-destructive</c> is passed, and the option's help names what it allows. The
/// engine's tests (<c>DestructiveOperationTests</c>, moved there in G-320) cover which operations are destructive, and
/// <c>PgProj.Integration.Tests.DestructiveOperationEndToEndTests</c> the real CLI/--allow-destructive behavior.
/// </summary>
[TestClass]
public sealed class DestructiveOperationCliTests
{
    /// <summary>G-179: the help names every kind of change <c>OperationBatch.GetDestructiveOperations</c> flags.</summary>
    [TestMethod]
    public void TheAllowDestructiveHelpNamesEveryKindOfDestructiveChange()
    {
        var option = PublishCommand.Create(new CliContext()).Options.Single(static option => option.Name == "--allow-destructive");

        foreach (var kind in new[]
        {
            "table", "column", "materialized view", "extension", "composite type field", "changed column type that can lose a value", "a widening",
            "cast", "operator", "aggregate", "rule", "publication", "event trigger",
            "revoked grant or role membership", "changed owner",
        })
        {
            StringAssert.Contains(option.Description!, kind);
        }
    }
}
