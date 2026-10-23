using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

[TestClass]
public sealed class SequenceLiveLoaderTests
{
    [TestMethod]
    public void BuildModelFromSnapshotSql_MapsSequencesWithOptionsAndOwnedBy()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [
                new LiveSchemaSnapshot("public")
            ],
            [
                new LiveTableSnapshot(
                    "public",
                    "orders",
                    [
                        new LiveColumnSnapshot("id", "integer", false, null, false, null, null)
                    ])
            ],
            [],
            [],
            sequences:
            [
                new LiveSequenceSnapshot(
                    "public",
                    "order_id_seq",
                    "bigint",
                    "10",
                    "1",
                    "1000",
                    "2",
                    true,
                    "5",
                    "public",
                    "orders",
                    "id")
            ]);

        var model = CreateLoader().BuildModelFromSnapshotSql(snapshotSql);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsTrue(model.Sequences.ContainsKey("public.order_id_seq"));
        var sequence = model.Sequences["public.order_id_seq"];
        Assert.AreEqual("pg_catalog.int8", sequence.DataType);
        Assert.AreEqual("2", sequence.IncrementBy);
        Assert.AreEqual("1", sequence.MinValue);
        Assert.AreEqual("1000", sequence.MaxValue);
        Assert.AreEqual("10", sequence.StartValue);
        Assert.AreEqual("5", sequence.CacheSize);
        Assert.AreEqual(true, sequence.Cycle);
        Assert.AreEqual("public.orders", sequence.OwnedByTable);
        Assert.AreEqual("id", sequence.OwnedByColumn);
    }

    [TestMethod]
    public void GenerateSnapshotSql_EmitsSequencesBeforeTables()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [
                new LiveSchemaSnapshot("public")
            ],
            [
                new LiveTableSnapshot(
                    "public",
                    "orders",
                    [
                        new LiveColumnSnapshot("id", "integer", false, null, false, null, null)
                    ])
            ],
            [],
            [],
            sequences:
            [
                new LiveSequenceSnapshot("public", "order_id_seq", null, "1", "1", "9223372036854775807", "1", false, "1", null, null, null)
            ]);

        var createSequenceIndex = snapshotSql.IndexOf("CREATE SEQUENCE public.order_id_seq", StringComparison.Ordinal);
        var createTableIndex = snapshotSql.IndexOf("CREATE TABLE public.orders", StringComparison.Ordinal);

        Assert.IsTrue(createSequenceIndex >= 0);
        Assert.IsTrue(createTableIndex > createSequenceIndex);
    }

    private static LiveDatabaseModelLoader CreateLoader()
        => new(new PgQueryParser(), new SemanticBuilder());
}
