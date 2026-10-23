namespace PgProj.Parser.Tests;

/// <summary>
/// G-206: a name typed outside SQL (<c>pgproj.json</c>, a command-line option, <c>@renamed-from</c>) is read as PostgreSQL and <c>pg_dump -n</c> read a
/// name: folded to lower case unless it is double-quoted. Names in the model are then compared exactly (<see cref="SqlIdentifiers.NameComparer"/>).
/// </summary>
[TestClass]
public sealed class NameFoldingTests
{
    [TestMethod]
    [DataRow("App", "app")]
    [DataRow("  APP ", "app")]
    [DataRow("\"App\"", "App")]
    [DataRow("\"My \"\"S\"\"\"", "My \"S\"")]
    [DataRow("app", "app")]
    public void ANameIsFoldedUnlessQuoted(string typed, string name)
    {
        Assert.AreEqual(name, SqlIdentifiers.FoldName(typed));
    }

    [TestMethod]
    public void NamesAreComparedExactly()
    {
        Assert.IsFalse(SqlIdentifiers.NameComparer.Equals("T", "t"));
        Assert.IsTrue(SqlIdentifiers.NameComparer.Equals("t", "t"));
    }
}
