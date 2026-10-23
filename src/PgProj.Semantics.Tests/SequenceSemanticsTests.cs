using PgProj.Parser;

namespace PgProj.Semantics.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SequenceSemanticsTests
{
    [TestMethod]
    public void CreateSequenceIsCapturedStructurally()
    {
        const string sql =
            "create sequence public.order_id_seq increment by 2 start with 10 minvalue 1 maxvalue 1000 cache 5 cycle owned by public.orders.id;";

        var model = BuildModel(sql);

        Assert.IsTrue(model.Sequences.ContainsKey("public.order_id_seq"));
        var sequence = model.Sequences["public.order_id_seq"];
        Assert.AreEqual("order_id_seq", sequence.Name);
        Assert.AreEqual("public", sequence.SchemaName);
        Assert.AreEqual("2", sequence.IncrementBy);
        Assert.AreEqual("10", sequence.StartValue);
        Assert.AreEqual("1", sequence.MinValue);
        Assert.AreEqual("1000", sequence.MaxValue);
        Assert.AreEqual("5", sequence.CacheSize);
        Assert.AreEqual(true, sequence.Cycle);
        Assert.AreEqual("public.orders", sequence.OwnedByTable);
        Assert.AreEqual("id", sequence.OwnedByColumn);
        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void CreateSequenceWithoutOptionsLeavesFieldsNull()
    {
        const string sql = "create sequence public.simple_seq;";

        var model = BuildModel(sql);

        Assert.IsTrue(model.Sequences.ContainsKey("public.simple_seq"));
        var sequence = model.Sequences["public.simple_seq"];
        Assert.IsNull(sequence.IncrementBy);
        Assert.IsNull(sequence.StartValue);
        Assert.IsNull(sequence.MinValue);
        Assert.IsNull(sequence.MaxValue);
        Assert.IsNull(sequence.CacheSize);
        Assert.IsNull(sequence.Cycle);
        Assert.IsNull(sequence.OwnedByTable);
        Assert.IsNull(sequence.OwnedByColumn);
    }

    [TestMethod]
    public void DuplicateSequenceDeclarationProducesDiagnostic()
    {
        const string sql = "create sequence public.dup_seq; create sequence public.dup_seq;";

        var model = BuildModel(sql);

        Assert.IsTrue(model.HasErrors);
        Assert.IsTrue(model.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("Duplicate Sequence definition", StringComparison.OrdinalIgnoreCase)));
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
