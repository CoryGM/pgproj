namespace PgProj.Engine.Tests;

/// <summary>
/// G-285: <c>diff</c> names a changed property as a user knows it (moved from <c>OutputRoughEdgesTests</c> with <c>DiffPropertyLabels</c>, G-316).
/// </summary>
[TestClass]
public sealed class DiffPropertyLabelsTests
{
    [TestMethod]
    [DataRow("DefaultExpressionNode", "Default")]
    [DataRow("SelectExpressionNode", "Query")]
    [DataRow("ModifiedColumns", "Columns changed")]
    [DataRow("OwnerRole", "Owner")]
    [DataRow("Signature.Result", "Result type")]
    [DataRow("Comment", "Comment")]
    [DataRow("ForeignKeyDeleteAction", "On delete")]
    [DataRow("IsUnlogged", "Unlogged")]
    [DataRow("IsRowSecurityEnabled", "Row security enabled")]
    [DataRow("WithoutOverlaps", "Without overlaps")]
    [DataRow("Option:fillfactor", "Option fillfactor")]
    [DataRow("Mapping:asciiword", "Mapping asciiword")]
    [DataRow("Table:public.t:Columns", "Table public.t columns")]
    public void APropertyIsNamedAsAUserKnowsIt(string propertyName, string label)
    {
        Assert.AreEqual(label, DiffPropertyLabels.For(propertyName));
    }
}
