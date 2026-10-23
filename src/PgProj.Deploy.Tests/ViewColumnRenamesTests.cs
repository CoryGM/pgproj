using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-276: when a view's output columns are its live ones renamed (a table column it selects was renamed), <c>publish</c> renames them with
/// <c>ALTER VIEW ... RENAME COLUMN</c> before <c>CREATE OR REPLACE VIEW</c>, which cannot rename one (<c>42P16</c>). Anything else stays a G-104 rebuild.
/// </summary>
[TestClass]
public sealed class ViewColumnRenamesTests
{
    [TestMethod]
    public void ARenamedColumnIsRenamed()
        => CollectionAssert.AreEqual(
            new[] { ("email", "email_address") },
            ViewColumnRenames.Plan(["id integer", "email text"], ["id integer", "email_address text"])!.ToArray());

    [TestMethod]
    public void RenamesAndColumnsAddedAfterThemArePlannedTogether()
        => CollectionAssert.AreEqual(
            new[] { ("a", "x"), ("b", "y") },
            ViewColumnRenames.Plan(["a integer", "b text"], ["x integer", "y text", "c date"])!.ToArray());

    [TestMethod]
    public void NoNameChangeIsAnEmptyPlan()
        => Assert.IsEmpty(ViewColumnRenames.Plan(["a integer"], ["a integer", "b text"])!);

    [TestMethod]
    public void QuotedNamesAreKeptAsQuoteIdentWritesThem()
        => CollectionAssert.AreEqual(
            new[] { ("\"E Mail\"", "\"Email \"\"Address\"\"\"") },
            ViewColumnRenames.Plan(["\"E Mail\" character varying(50)"], ["\"Email \"\"Address\"\"\" character varying(50)"])!.ToArray());

    [TestMethod]
    [DataRow(new[] { "a integer", "b text" }, new[] { "a integer" }, DisplayName = "a column dropped")]
    [DataRow(new[] { "a integer", "b text" }, new[] { "b text", "a integer" }, DisplayName = "columns moved")]
    [DataRow(new[] { "a integer" }, new[] { "x bigint" }, DisplayName = "renamed and retyped")]
    [DataRow(new[] { "a integer", "b integer" }, new[] { "b integer", "a integer" }, DisplayName = "two columns trade names")]
    [DataRow(new[] { "a integer", "b integer" }, new[] { "b integer", "c integer" }, DisplayName = "a new name another column has")]
    [DataRow(new string[0], new[] { "a integer" }, DisplayName = "the live columns are unknown")]
    [DataRow(new[] { "a integer" }, new string[0], DisplayName = "the project's columns are unknown")]
    public void AnyOtherChangeHasNoPlan(string[] oldColumns, string[] newColumns)
        => Assert.IsNull(ViewColumnRenames.Plan(oldColumns, newColumns));
}
