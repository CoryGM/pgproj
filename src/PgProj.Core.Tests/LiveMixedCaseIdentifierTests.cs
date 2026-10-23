using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-70: the live model is built by writing the catalog as SQL and parsing it, so what the loader writes for a name has to read back as
/// the same name. A name with a capital, a space, or a reserved word (<c>"order"</c>) must be quoted, and one that is plain must not
/// change. (The loader itself is exercised against a real server by <c>MixedCaseIdentifierEndToEndTests</c>.)
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveMixedCaseIdentifierTests
{
    [TestMethod]
    public void NamesThatNeedQuotesAreQuotedInTheSnapshotAndReadBackAsTheSameNames()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("MySchema"), new LiveSchemaSnapshot("public")],
            [
                new LiveTableSnapshot(
                    "MySchema",
                    "Parent",
                    [
                        new LiveColumnSnapshot("Id", "integer", false, null, false, null, null),
                        new LiveColumnSnapshot("order", "integer", true, null, false, null, null),
                        new LiveColumnSnapshot("Mixed Col", "text", true, "'x'::text", false, null, null),
                        new LiveColumnSnapshot("plain", "text", true, null, false, null, null),
                    ]),
            ],
            [new LiveConstraintSnapshot("MySchema", "Parent", "Parent_pkey", "PRIMARY KEY (\"Id\")")],
            [new LiveIndexSnapshot("MySchema", "Parent", "IxParent", "CREATE INDEX \"IxParent\" ON \"MySchema\".\"Parent\" USING btree (\"order\", lower(\"Mixed Col\"))")],
            [],
            [],
            [],
            []);

        StringAssert.Contains(snapshotSql, "\"MySchema\".\"Parent\"");
        StringAssert.Contains(snapshotSql, "\"order\" integer");
        StringAssert.Contains(snapshotSql, "\"Mixed Col\" text");
        StringAssert.Contains(snapshotSql, "    plain text", "A plain name stays bare, so text that was always right does not change.");

        var model = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(snapshotSql);

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error), string.Join("; ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var table = model.Tables["MySchema.Parent"];
        Assert.AreEqual("Parent", table.Name);
        Assert.AreEqual("MySchema", table.SchemaName);
        CollectionAssert.AreEquivalent(new[] { "Id", "order", "Mixed Col", "plain" }, table.Columns.Select(static column => column.Name).ToArray());
        Assert.AreEqual("IxParent", model.Indexes.Values.Single().Name);
        CollectionAssert.AreEqual(new[] { "Id" }, model.Constraints.Values.Single().PrimaryKeyColumns.ToArray());
    }

    [TestMethod]
    public void AGranteeThatNeedsQuotesIsQuoted()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")],
            [],
            [],
            [],
            [],
            [],
            [],
            [new LiveGrantSnapshot("public", "schema:public", "SomeRole", "GRANT USAGE ON SCHEMA public TO \"SomeRole\"")]);

        StringAssert.Contains(snapshotSql, "TO \"SomeRole\"");
    }
}
