using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class SequenceAlterOperationTests
{
    [TestMethod]
    public void AlterOperationEmitsIncrementByForChangedIncrement()
    {
        var oldSequence = CreateSequence(incrementBy: "1");
        var newSequence = CreateSequence(incrementBy: "5");

        var sql = BuildAlterSql(oldSequence, newSequence, [
            Detail(nameof(SequenceDefinition.IncrementBy), "1", "5"),
        ]);

        Assert.AreEqual("ALTER SEQUENCE \"public\".\"order_id_seq\" INCREMENT BY 5;", sql);
    }

    [TestMethod]
    public void AlterOperationEmitsMultipleChangedOptionsInOneStatement()
    {
        var oldSequence = CreateSequence(minValue: "1", maxValue: "100", cycle: false);
        var newSequence = CreateSequence(minValue: "1", maxValue: "1000", cycle: true);

        var sql = BuildAlterSql(oldSequence, newSequence, [
            Detail(nameof(SequenceDefinition.MaxValue), "100", "1000"),
            Detail(nameof(SequenceDefinition.Cycle), "False", "True"),
        ]);

        Assert.AreEqual("ALTER SEQUENCE \"public\".\"order_id_seq\" MAXVALUE 1000 CYCLE;", sql);
    }

    [TestMethod]
    public void AlterOperationEmitsNoMinValueWhenMinValueCleared()
    {
        var oldSequence = CreateSequence(minValue: "5");
        var newSequence = CreateSequence(minValue: null);

        var sql = BuildAlterSql(oldSequence, newSequence, [
            Detail(nameof(SequenceDefinition.MinValue), "5", null),
        ]);

        Assert.AreEqual("ALTER SEQUENCE \"public\".\"order_id_seq\" NO MINVALUE;", sql);
    }

    [TestMethod]
    public void AlterOperationEmitsStartWithForChangedStartValue()
    {
        var oldSequence = CreateSequence(startValue: "1");
        var newSequence = CreateSequence(startValue: "100");

        var sql = BuildAlterSql(oldSequence, newSequence, [
            Detail(nameof(SequenceDefinition.StartValue), "1", "100"),
        ]);

        // G-220: the start is recorded and the counter left alone; RESTART moved it and never converged.
        Assert.AreEqual("ALTER SEQUENCE \"public\".\"order_id_seq\" START WITH 100;", sql);
    }

    [TestMethod]
    public void AlterOperationEmitsOwnedByNoneWhenOwnershipRemoved()
    {
        var oldSequence = CreateSequence() with { OwnedByTable = "public.orders", OwnedByColumn = "id" };
        var newSequence = CreateSequence();

        var sql = BuildAlterSql(oldSequence, newSequence, [
            Detail(nameof(SequenceDefinition.OwnedByTable), "public.orders", null),
            Detail(nameof(SequenceDefinition.OwnedByColumn), "id", null),
        ]);

        Assert.AreEqual("ALTER SEQUENCE \"public\".\"order_id_seq\" OWNED BY NONE;", sql);
    }

    [TestMethod]
    public void AlterOperationReportsNoChangesWhenDetailsAreEmpty()
    {
        var sequence = CreateSequence();

        var sql = BuildAlterSql(sequence, sequence, []);

        StringAssert.StartsWith(sql, "-- No changes detected");
    }

    private static string BuildAlterSql(SequenceDefinition oldSequence, SequenceDefinition newSequence, IReadOnlyList<ModificationDetail> details)
        => new AlterOperation
        {
            NodeId = newSequence.Key,
            NodeKind = ChangeNodeKind.Sequence,
            Change = new ModifiedChange
            {
                NodeId = newSequence.Key,
                NodeKind = ChangeNodeKind.Sequence,
                OldNode = oldSequence,
                NewNode = newSequence,
                Details = details,
            },
        }.GenerateSql();

    private static ModificationDetail Detail(string propertyName, string? oldValue, string? newValue)
        => new()
        {
            PropertyName = propertyName,
            OldValue = oldValue,
            NewValue = newValue,
        };

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
