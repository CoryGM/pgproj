namespace PgProj.Parser.Tests;

/// <summary>G-182: the column list of <c>ON DELETE SET NULL (cols)</c> and <c>SET DEFAULT (cols)</c> (PostgreSQL 15) is read.</summary>
[TestClass]
public sealed class ForeignKeySetColumnsParsingTests
{
    [TestMethod]
    [DataRow("ON DELETE SET NULL (b)", "n", "b")]
    [DataRow("ON DELETE SET DEFAULT (a, b)", "d", "a,b")]
    [DataRow("MATCH FULL ON DELETE SET NULL (b) ON UPDATE CASCADE", "n", "b")]
    [DataRow("ON DELETE SET NULL", "n", "")]
    [DataRow("ON DELETE CASCADE", "c", "")]
    public void TheSetColumnsAreRead(string clauses, string expectedAction, string expectedColumns)
    {
        var tree = new PgQueryParser().Parse($"CREATE TABLE public.ch (id int PRIMARY KEY, a int, b int, CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES public.par (a, b) {clauses});");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var foreignKey = tree.Nodes.OfType<ConstraintNode>().Single(static node => node.Name == "fk");
        Assert.AreEqual(expectedAction, foreignKey.ForeignKeyDeleteAction);
        Assert.AreEqual(expectedColumns, string.Join(',', foreignKey.ForeignKeyDeleteSetColumns));
    }
}
