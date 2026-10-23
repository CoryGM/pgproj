using PgProj.Semantics;

namespace PgProj.Diff.Tests;

[TestClass]
public sealed class SequenceDiffTests
{
    [TestMethod]
    public void AddedSequenceIsReportedAsAdded()
    {
        var oldModel = CreateModel();
        var newModel = CreateModel(CreateSequence("public.order_id_seq", "order_id_seq", "public"));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.AddedSequences);
        Assert.AreEqual("public.order_id_seq", result.AddedSequences[0].Key);
    }

    [TestMethod]
    public void RemovedSequenceIsReportedAsRemoved()
    {
        var oldModel = CreateModel(CreateSequence("public.order_id_seq", "order_id_seq", "public"));
        var newModel = CreateModel();

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.RemovedSequences);
        Assert.AreEqual("public.order_id_seq", result.RemovedSequences[0].Key);
    }

    [TestMethod]
    public void SequenceOptionChangesProduceModificationDetails()
    {
        var oldModel = CreateModel(CreateSequence(
            "public.order_id_seq",
            "order_id_seq",
            "public",
            incrementBy: "1",
            cacheSize: "1",
            cycle: false));

        var newModel = CreateModel(CreateSequence(
            "public.order_id_seq",
            "order_id_seq",
            "public",
            incrementBy: "2",
            cacheSize: "10",
            cycle: true));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedSequences);
        var change = result.ModifiedSequences[0];
        Assert.AreEqual("public.order_id_seq", change.NodeId);
        Assert.IsTrue(change.Details.Any(detail =>
            detail.PropertyName == nameof(SequenceDefinition.IncrementBy)
            && detail.OldValue == "1"
            && detail.NewValue == "2"));
        Assert.IsTrue(change.Details.Any(detail =>
            detail.PropertyName == nameof(SequenceDefinition.CacheSize)
            && detail.OldValue == "1"
            && detail.NewValue == "10"));
        Assert.IsTrue(change.Details.Any(detail => detail.PropertyName == nameof(SequenceDefinition.Cycle)));
    }

    [TestMethod]
    public void UnchangedSequenceProducesNoModification()
    {
        var oldModel = CreateModel(CreateSequence("public.order_id_seq", "order_id_seq", "public", incrementBy: "1"));
        var newModel = CreateModel(CreateSequence("public.order_id_seq", "order_id_seq", "public", incrementBy: "1"));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(0, result.ModifiedSequences);
    }

    private static DatabaseModel CreateModel(params SequenceDefinition[] sequences)
        => DatabaseModel.Empty with
        {
            Sequences = sequences.ToDictionary(static sequence => sequence.Key, StringComparer.OrdinalIgnoreCase),
        };

    private static SequenceDefinition CreateSequence(
        string key,
        string name,
        string? schemaName,
        string? incrementBy = null,
        string? cacheSize = null,
        bool? cycle = null)
        => new(
            key,
            name,
            schemaName,
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
            CacheSize = cacheSize,
            Cycle = cycle,
        };
}
