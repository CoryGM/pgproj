using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-136: a live typed table (<c>pg_class.reloftype</c> set) is written to the snapshot and to <c>init</c>'s scaffold as <c>CREATE TABLE ... OF type</c>, listing only the columns it
/// gives a default or NOT NULL, and read back with its type and the columns of that type, so it compares with a project's own. Before, it was left out (G-118).
/// </summary>
[TestClass]
public sealed class LiveTypedTableTests
{
    private static readonly LiveTypeSnapshot Type = new("public", "r", "composite", "CREATE TYPE public.r AS (k integer, v text);");

    private static LiveTableSnapshot Typed() => new(
        "public",
        "t",
        [
            new LiveColumnSnapshot("k", "integer", false, null, false, null, null),
            new LiveColumnSnapshot("v", "text", false, "'x'::text", false, null, null),
            new LiveColumnSnapshot("w", "text", true, null, false, null, null),
        ],
        OfTypeSchemaName: "public",
        OfTypeName: "r");

    [TestMethod]
    public void ATypedTableIsWrittenOfItsTypeListingOnlyWhatItGivesItsColumns()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [Typed()], [], [], types: [Type]);

        StringAssert.Contains(sql, "CREATE TABLE public.t OF public.r (");
        StringAssert.Contains(sql, "k WITH OPTIONS NOT NULL");
        StringAssert.Contains(sql, "v WITH OPTIONS DEFAULT 'x'::text NOT NULL");
        Assert.IsFalse(sql.Contains("w WITH OPTIONS", StringComparison.Ordinal), "A column that is its field as it is is not listed.");
    }

    [TestMethod]
    public void ATypedTableWithNothingToListIsWrittenWithoutParentheses()
    {
        var table = Typed() with { Columns = [new LiveColumnSnapshot("k", "integer", true, null, false, null, null), new LiveColumnSnapshot("v", "text", true, null, false, null, null)] };

        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [table], [], [], types: [Type]);

        StringAssert.Contains(sql, "CREATE TABLE public.t OF public.r;");
    }

    [TestMethod]
    public void ATypedTableIsReadBackWithItsTypeAndTheColumnsOfThatType()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [Typed() with { Columns = Typed().Columns.Take(2).ToArray() }], [], [], types: [Type]);

        var model = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);

        var table = model.Tables["public.t"];
        Assert.AreEqual("public.r", table.OfTypeKey);
        CollectionAssert.AreEqual(new[] { "k", "v" }, table.Columns.Select(static column => column.Name).ToArray());
        Assert.AreEqual("'x'::text", table.Columns[1].RawDefaultExpression);
        Assert.IsTrue(model.Types["public.r"].HasTypedTables);
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheScaffoldWritesATypedTableOfItsTypeInItsTableFile()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [Typed()], [], [], [], [], [Type], [], [], [], []);

        var file = files.Single(static f => f.RelativePath == "Schema/public/Table/t.sql");
        StringAssert.Contains(file.Content, "CREATE TABLE public.t OF public.r");
        Assert.IsEmpty(new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).FindUnparseableFiles([file]));
    }
}
