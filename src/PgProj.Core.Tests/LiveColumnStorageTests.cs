namespace PgProj.Core.Tests;

/// <summary>G-222: a live column's storage is always in the database read and in a scaffold only when it is not its type's; its own compression is in both.</summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveColumnStorageTests
{
    [TestMethod]
    public void TheTypesStorageIsScaffoldedOnlyWhenItIsNot()
    {
        LiveColumnSnapshot[] columns =
        [
            new LiveColumnSnapshot("a", "text", true, null, false, null, null) { Storage = "main", Compression = "lz4" },
            new LiveColumnSnapshot("b", "text", true, null, false, null, null) { Storage = "extended", IsDefaultStorage = true },
        ];
        var table = new LiveTableSnapshot("public", "t", columns);

        var snapshot = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [table], [], []);
        StringAssert.Contains(snapshot, "a text STORAGE main COMPRESSION lz4");
        StringAssert.Contains(snapshot, "b text STORAGE extended");

        var file = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [table], [], [], [], [], [], [], [], [], []).Single(static file => file.RelativePath.EndsWith("/t.sql", StringComparison.Ordinal)).Content;
        // Since G-269 the scaffold writes the storage after the table, in the form every supported version accepts (STORAGE inside CREATE TABLE needs 16).
        StringAssert.Contains(file, "a text COMPRESSION lz4");
        StringAssert.Contains(file, "ALTER TABLE public.t ALTER COLUMN a SET STORAGE main;");
        Assert.IsFalse(file.Contains("STORAGE extended", StringComparison.Ordinal), file);
    }
}
