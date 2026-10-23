namespace PgProj.Cli.Tests;

/// <summary>
/// G-300: the kinds <c>init</c> cannot read are printed in its help and after a run from <see cref="InitCommand.UnreadObjectKinds"/>; the command reference's
/// copy of that output (docs/commands.md, moved there from the README on 2026-10-22) must list each one, so it cannot fall behind the code.
/// </summary>
[TestClass]
public sealed class InitCoverageListTests
{
    [TestMethod]
    public void TheCommandReferenceListsEveryKindInitCannotRead()
    {
        var reference = File.ReadAllText(Path.Combine(SchemaPerTenantSampleTests.FindRepositoryRoot(), "docs", "commands.md"));

        var missing = InitCommand.UnreadObjectKinds.Where(kind => !reference.Contains("  - " + kind, StringComparison.Ordinal)).ToArray();

        Assert.AreEqual(0, missing.Length, "Not in docs/commands.md's init output: " + string.Join(" | ", missing));
    }
}
