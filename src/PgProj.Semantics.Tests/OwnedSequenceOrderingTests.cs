using PgProj.Parser;

namespace PgProj.Semantics.Tests;

[TestClass]
[DoNotParallelize]
public sealed class OwnedSequenceOrderingTests
{
    [TestMethod]
    public void SemanticGraphPlacesSequenceBeforeOwningTable()
    {
        const string sql =
            """
            CREATE SEQUENCE public.orders_id_seq OWNED BY public.orders.id;
            CREATE TABLE public.orders (id integer);
            """;

        var model = BuildModel(sql);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));

        var order = model.SemanticGraph.TopologicalOrder;
        var sequenceIndex = order.IndexOf("public.orders_id_seq");
        var tableIndex = order.IndexOf("public.orders");

        Assert.IsTrue(sequenceIndex >= 0, "Sequence must appear in topological order.");
        Assert.IsTrue(tableIndex >= 0, "Table must appear in topological order.");
        Assert.IsTrue(sequenceIndex < tableIndex,
            $"Sequence (index {sequenceIndex}) must be ordered before table (index {tableIndex}).");
    }

    [TestMethod]
    public void SemanticGraphIsUnaffectedForSequenceWithoutOwnedBy()
    {
        const string sql =
            """
            CREATE SEQUENCE public.standalone_seq;
            CREATE TABLE public.products (id integer);
            """;

        var model = BuildModel(sql);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));

        var order = model.SemanticGraph.TopologicalOrder;
        Assert.IsTrue(order.Contains("public.standalone_seq"), "Standalone sequence must be in topological order.");
        Assert.IsTrue(order.Contains("public.products"), "Table must be in topological order.");
    }

    [TestMethod]
    public void MultipleOwnedSequencesAllPrecedeOwningTable()
    {
        const string sql =
            """
            CREATE SEQUENCE public.invoices_id_seq OWNED BY public.invoices.id;
            CREATE SEQUENCE public.invoices_number_seq OWNED BY public.invoices.number;
            CREATE TABLE public.invoices (id integer, number integer);
            """;

        var model = BuildModel(sql);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));

        var order = model.SemanticGraph.TopologicalOrder;
        var tableIndex = order.IndexOf("public.invoices");
        var idSeqIndex = order.IndexOf("public.invoices_id_seq");
        var numberSeqIndex = order.IndexOf("public.invoices_number_seq");

        Assert.IsTrue(idSeqIndex >= 0 && idSeqIndex < tableIndex,
            $"invoices_id_seq (index {idSeqIndex}) must precede invoices (index {tableIndex}).");
        Assert.IsTrue(numberSeqIndex >= 0 && numberSeqIndex < tableIndex,
            $"invoices_number_seq (index {numberSeqIndex}) must precede invoices (index {tableIndex}).");
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
