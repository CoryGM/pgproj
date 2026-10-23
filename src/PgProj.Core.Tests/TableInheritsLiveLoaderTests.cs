namespace PgProj.Core.Tests;

/// <summary>
/// G-194: the live loader scripts a child of <c>INHERITS</c> the way a project writes one, its own columns (<c>attislocal</c>) and its parents in order, so the
/// model it builds has the inherited columns from the parents, as a project does, and <c>init</c> scaffolds the same text.
/// </summary>
[TestClass]
public sealed class TableInheritsLiveLoaderTests
{
    [TestMethod]
    public void AChildIsScriptedWithItsOwnColumnsAndItsParents()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")],
            [
                new LiveTableSnapshot("public", "p", [new LiveColumnSnapshot("id", "integer", false, null, false, null, null)]),
                new LiveTableSnapshot(
                    "public",
                    "c",
                    [
                        new LiveColumnSnapshot("id", "integer", false, null, false, null, null, IsLocal: false),
                        new LiveColumnSnapshot("extra", "integer", true, null, false, null, null),
                    ],
                    InheritsFrom: ["public.p", "s.\"Other\""]),
            ],
            [],
            []);

        StringAssert.Contains(sql, $"CREATE TABLE public.c ({Environment.NewLine}    extra integer{Environment.NewLine}){Environment.NewLine}INHERITS (public.p, s.\"Other\");");
    }
}
