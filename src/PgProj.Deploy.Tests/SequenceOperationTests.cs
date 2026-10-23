using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class SequenceOperationTests
{
    [TestMethod]
    public void CreateOperationGeneratesCreateSequenceStatement()
    {
        var sequence = CreateSequence(
            incrementBy: "2",
            minValue: "1",
            maxValue: "1000",
            startValue: "10",
            cacheSize: "5",
            cycle: true);

        var operation = new CreateOperation
        {
            NodeId = sequence.Key,
            NodeKind = ChangeNodeKind.Sequence,
            Node = sequence,
        };

        var sql = operation.GenerateSql();

        Assert.AreEqual(
            "CREATE SEQUENCE \"public\".\"order_id_seq\" INCREMENT BY 2 MINVALUE 1 MAXVALUE 1000 START WITH 10 CACHE 5 CYCLE;",
            sql);
    }

    [TestMethod]
    public void CreateOperationGeneratesOwnedByClauseInline()
    {
        var sequence = CreateSequence() with
        {
            OwnedByTable = "public.orders",
            OwnedByColumn = "id",
        };

        var operation = new CreateOperation
        {
            NodeId = sequence.Key,
            NodeKind = ChangeNodeKind.Sequence,
            Node = sequence,
        };

        var sql = operation.GenerateSql();

        Assert.AreEqual(
            "CREATE SEQUENCE \"public\".\"order_id_seq\" OWNED BY public.orders.\"id\";",
            sql);
    }

    [TestMethod]
    public void DropOperationGeneratesDropSequenceStatement()
    {
        var sequence = CreateSequence();

        var operation = new DropOperation
        {
            NodeId = sequence.Key,
            NodeKind = ChangeNodeKind.Sequence,
            Node = sequence,
        };

        Assert.AreEqual("DROP SEQUENCE \"public\".\"order_id_seq\";", operation.GenerateSql());
    }

    private static SequenceDefinition CreateSequence(
        string? incrementBy = null,
        string? minValue = null,
        string? maxValue = null,
        string? startValue = null,
        string? cacheSize = null,
        bool? cycle = null)
        => new(
            "public.order_id_seq",
            "order_id_seq",
            "public",
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [])
        {
            IncrementBy = incrementBy,
            MinValue = minValue,
            MaxValue = maxValue,
            StartValue = startValue,
            CacheSize = cacheSize,
            Cycle = cycle,
        };
}
