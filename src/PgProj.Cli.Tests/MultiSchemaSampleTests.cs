namespace PgProj.Cli.Tests;

/// <summary>
/// The sample project <c>samples/MultiSchema</c> builds without an error, as <c>samples/README.md</c> says, and since G-307, which stopped
/// reading the <c>FOR UPDATE OF</c> alias in <c>sales.place_order</c> as a missing table, without any diagnostic, as <c>SchemaPerTenantSampleTests</c> expects.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MultiSchemaSampleTests
{
    [TestMethod]
    public async Task TheSampleBuildsWithoutAnError()
    {
        var project = Path.Combine(SchemaPerTenantSampleTests.FindRepositoryRoot(), "samples", "MultiSchema", "pgproj.json");

        var (exitCode, output) = await SchemaPerTenantSampleTests.RunAsync("build", "--project", project);

        Assert.AreEqual(0, exitCode, output);
        StringAssert.Contains(output, "SQL files: 80");
        Assert.IsFalse(output.Contains("[Error]", StringComparison.Ordinal), output);
        Assert.IsFalse(output.Contains("[Warning]", StringComparison.Ordinal), output);
        StringAssert.Contains(output, "No diagnostics.");
    }
}
