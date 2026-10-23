using PgProj.Parser;

namespace PgProj.Semantics.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SerialColumnSemanticsTests
{
    [TestMethod]
    public void SerialColumnIsNormalizedAndSynthesizesOwnedSequence()
    {
        var model = new SemanticPipeline().Execute(
            [new PgQueryParser().Parse("CREATE TABLE public.orders (id SERIAL PRIMARY KEY, name text);")]);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));

        var table = model.Tables["public.orders"];
        var column = table.Columns.Single(c => c.Name == "id");

        Assert.IsTrue(column.IsSerial);
        Assert.AreEqual("integer", column.RawType);
        Assert.AreEqual("nextval('public.orders_id_seq'::regclass)", column.RawDefaultExpression);

        Assert.IsTrue(model.Sequences.ContainsKey("public.orders_id_seq"), "Implicit sequence should be synthesized.");
        var sequence = model.Sequences["public.orders_id_seq"];
        Assert.AreEqual("public.orders", sequence.OwnedByTable);
        Assert.AreEqual("id", sequence.OwnedByColumn);
    }

    [TestMethod]
    public void SerialColumnSequenceIsOrderedBeforeOwningTable()
    {
        var model = new SemanticPipeline().Execute(
            [new PgQueryParser().Parse("CREATE TABLE public.orders (id SERIAL PRIMARY KEY);")]);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));

        var order = model.SemanticGraph.TopologicalOrder;
        var sequenceIndex = order.IndexOf("public.orders_id_seq");
        var tableIndex = order.IndexOf("public.orders");

        Assert.IsTrue(sequenceIndex >= 0, "Synthesized sequence must appear in topological order.");
        Assert.IsTrue(tableIndex >= 0, "Table must appear in topological order.");
        Assert.IsTrue(sequenceIndex < tableIndex,
            $"Sequence (index {sequenceIndex}) must be ordered before table (index {tableIndex}).");
    }

    [TestMethod]
    public void NonSerialColumnDoesNotSynthesizeSequence()
    {
        var model = new SemanticPipeline().Execute(
            [new PgQueryParser().Parse("CREATE TABLE public.products (id integer);")]);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));
        Assert.IsFalse(model.Sequences.ContainsKey("public.products_id_seq"));
    }
}
