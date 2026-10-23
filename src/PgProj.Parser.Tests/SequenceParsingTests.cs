namespace PgProj.Parser.Tests;

[TestClass]
public sealed class SequenceParsingTests
{
    [TestMethod]
    public void CreateSequenceProducesSequenceNodeWithOptions()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse(
            "CREATE SEQUENCE public.order_id_seq INCREMENT BY 2 START WITH 10 MINVALUE 1 MAXVALUE 1000 CACHE 5 CYCLE OWNED BY public.orders.id;");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var sequence = tree.Sequences.Single();
        Assert.AreEqual("order_id_seq", sequence.Name);
        Assert.AreEqual("public", sequence.SchemaName);
        Assert.IsNotNull(sequence.Options);
        Assert.AreEqual("2", sequence.Options!["increment"]);
        Assert.AreEqual("10", sequence.Options["start"]);
        Assert.AreEqual("1", sequence.Options["minvalue"]);
        Assert.AreEqual("1000", sequence.Options["maxvalue"]);
        Assert.AreEqual("5", sequence.Options["cache"]);
        Assert.IsTrue(bool.Parse(sequence.Options["cycle"]));
        Assert.AreEqual("public.orders.id", sequence.Options["owned_by"]);
    }

    [TestMethod]
    public void CreateSequenceWithoutOptionsProducesSequenceNodeWithNullOptions()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE SEQUENCE public.simple_seq;");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var sequence = tree.Sequences.Single();
        Assert.AreEqual("simple_seq", sequence.Name);
        Assert.AreEqual("public", sequence.SchemaName);
        Assert.IsNull(sequence.Options);
    }

    /// <summary>pg_query leaves a zero out of its JSON; MINVALUE 0 used to be read as the text "{}" (deployed as invalid SQL).</summary>
    [TestMethod]
    [DataRow("MINVALUE 0", "minvalue", "0")]
    [DataRow("START WITH 0 MINVALUE 0", "start", "0")]
    [DataRow("MAXVALUE 0 INCREMENT BY -1", "maxvalue", "0")]
    [DataRow("MINVALUE -5", "minvalue", "-5")]
    [DataRow("MINVALUE 7", "minvalue", "7")]
    [DataRow("CACHE 0", "cache", "0")]
    public void ZeroAndNegativeOptionValuesAreReadAsNumbers(string options, string option, string expected)
    {
        var sequence = new PgQueryParser().Parse($"CREATE SEQUENCE public.s {options};").Sequences.Single();

        Assert.AreEqual(expected, sequence.Options![option]);
        Assert.IsFalse(sequence.Options.Values.Any(static value => value.Contains('{', StringComparison.Ordinal)), string.Join(", ", sequence.Options.Select(static pair => $"{pair.Key}={pair.Value}")));
    }
}
