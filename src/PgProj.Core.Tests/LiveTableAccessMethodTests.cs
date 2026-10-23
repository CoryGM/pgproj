namespace PgProj.Core.Tests;

/// <summary>G-223: a live table's access method is always in the database read, and in a scaffold only when it is not the server's default.</summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveTableAccessMethodTests
{
    [TestMethod]
    public void TheDefaultAccessMethodIsScaffoldedOnlyWhenItIsNotTheDefault()
    {
        LiveColumnSnapshot[] columns = [new LiveColumnSnapshot("a", "integer", true, null, false, null, null)];
        var plain = new LiveTableSnapshot("public", "t", columns) { AccessMethod = "heap", IsDefaultAccessMethod = true };
        var other = new LiveTableSnapshot("public", "u", columns) { AccessMethod = "heap2", IsDefaultAccessMethod = false };

        var snapshot = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [plain, other], [], []);
        StringAssert.Contains(snapshot, ")" + Environment.NewLine + "USING heap;");
        StringAssert.Contains(snapshot, ")" + Environment.NewLine + "USING heap2;");

        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [plain, other], [], [], [], [], [], [], [], [], []);
        Assert.IsFalse(files.Single(static file => file.RelativePath.EndsWith("/t.sql", StringComparison.Ordinal)).Content.Contains("USING", StringComparison.Ordinal));
        StringAssert.Contains(files.Single(static file => file.RelativePath.EndsWith("/u.sql", StringComparison.Ordinal)).Content, "USING heap2");
    }
}
