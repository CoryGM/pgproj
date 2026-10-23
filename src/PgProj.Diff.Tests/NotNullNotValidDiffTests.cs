using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-121 part 2d: a NOT NULL that changes between valid and <c>NOT VALID</c> (PostgreSQL 18) is a modified column, reported as
/// <c>NotNullIsNotValid</c>; the constraint's name is compared only when the project states one (G-235; an unnamed one is named by the server).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NotNullNotValidDiffTests
{
    private const string Table = "CREATE TABLE public.t (a integer, b integer);";

    private static string NotValid(string name = "nn") => Table + $" ALTER TABLE public.t ADD CONSTRAINT {name} NOT NULL a NOT VALID;";

    private const string Valid = "CREATE TABLE public.t (a integer NOT NULL, b integer);";

    [TestMethod]
    public void ANotValidNotNullDiffedWithItselfIsEmpty()
    {
        var model = Build(NotValid());

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(NotValid()), Build(NotValid()))));
    }

    [TestMethod]
    public void TheNameOfANotValidNotNullIsNotComparedWhenTheProjectStatesNone()
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(NotValid()), Build(Table + " ALTER TABLE public.t ADD NOT NULL a NOT VALID;"))));
    }

    [TestMethod]
    public void AnotherNameTheProjectStatesIsAChange()
    {
        // G-235: a name the project states is compared against the database's (before, a NOT VALID NOT NULL's name was never compared).
        var diff = new DiffEngine().Diff(Build(NotValid("live_name")), Build(NotValid("project_name")));

        var detail = diff.Modified.Single(static change => change.NodeId.EndsWith(".a", StringComparison.OrdinalIgnoreCase)).Details.Single();
        Assert.AreEqual(nameof(ColumnDefinition.NotNullName), detail.PropertyName);
    }

    [TestMethod]
    public void AnOrdinaryNotNullNamedOrNotIsTheSameColumn()
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Valid), Build("CREATE TABLE public.t (a integer CONSTRAINT nn NOT NULL, b integer);"))));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AChangeBetweenValidAndNotValidIsReported(bool toNotValid)
    {
        var diff = toNotValid
            ? new DiffEngine().Diff(Build(Valid), Build(NotValid()))
            : new DiffEngine().Diff(Build(NotValid()), Build(Valid));

        var detail = diff.Modified.Single(static change => change.NodeId.EndsWith(".a", StringComparison.OrdinalIgnoreCase)).Details.Single();
        Assert.AreEqual(nameof(ColumnDefinition.NotNullIsNotValid), detail.PropertyName);
        Assert.AreEqual((!toNotValid).ToString(), detail.OldValue?.ToString());
        Assert.AreEqual(toNotValid.ToString(), detail.NewValue?.ToString());
    }

    [TestMethod]
    public void ANullableColumnBecomingNotValidNotNullIsReportedAsNullabilityAndNotValid()
    {
        var diff = new DiffEngine().Diff(Build(Table), Build(NotValid()));

        var names = diff.Modified.Single(static change => change.NodeId.EndsWith(".a", StringComparison.OrdinalIgnoreCase)).Details.Select(static d => d.PropertyName).Order().ToArray();
        CollectionAssert.AreEqual(new[] { nameof(ColumnDefinition.IsNullable), nameof(ColumnDefinition.NotNullIsNotValid) }, names);
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
