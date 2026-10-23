using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-182: the column list of <c>ON DELETE SET NULL (cols)</c> is compared. A foreign key that nulls only <c>b</c> and one that nulls every
/// referencing column are different, and the list written in another order is not.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ForeignKeySetColumnsDiffTests
{
    private const string Template = "CREATE TABLE public.par (a int, b int, PRIMARY KEY (a, b)); CREATE TABLE public.ch (a int, b int, CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES public.par (a, b) {0});";

    [TestMethod]
    public void AForeignKeyDiffedWithItselfIsEmpty()
    {
        var sql = string.Format(Template, "MATCH FULL ON DELETE SET NULL (b)");

        Assert.IsEmpty(Modified(Build(sql), Build(sql)));
    }

    [TestMethod]
    public void TheSameColumnsInAnotherOrderAreNotAChange()
    {
        Assert.IsEmpty(Modified(Build(string.Format(Template, "ON DELETE SET DEFAULT (a, b)")), Build(string.Format(Template, "ON DELETE SET DEFAULT (b, a)"))));
    }

    [TestMethod]
    [DataRow("ON DELETE SET NULL", "ON DELETE SET NULL (b)")]
    [DataRow("ON DELETE SET NULL (b)", "ON DELETE SET NULL")]
    [DataRow("ON DELETE SET NULL (a)", "ON DELETE SET NULL (b)")]
    public void ADifferentColumnListIsAChange(string before, string after)
    {
        var modified = Modified(Build(string.Format(Template, before)), Build(string.Format(Template, after)));

        var detail = modified.Single().Details.Single();
        Assert.AreEqual(nameof(ConstraintDefinition.ForeignKeyDeleteSetColumns), detail.PropertyName);
    }

    private static IReadOnlyList<ModifiedChange> Modified(DatabaseModel live, DatabaseModel project)
        => [.. new DiffEngine().Diff(live, project).Modified.Where(static change => change.NodeId.EndsWith(".fk", StringComparison.OrdinalIgnoreCase))];

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
