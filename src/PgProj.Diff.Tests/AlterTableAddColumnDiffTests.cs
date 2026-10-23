using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-47: a column added by <c>ALTER TABLE ... ADD COLUMN</c> is an ordinary column of its table, so it must diff exactly as the same
/// column written inside <c>CREATE TABLE</c> does. Both directions matter (<c>copilot-diff.md</c>): the same schema written two ways
/// reports nothing, and a real difference is still reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AlterTableAddColumnDiffTests
{
    private const string Inline = "CREATE TABLE public.customers (id integer PRIMARY KEY, name text, phone varchar(20) NOT NULL DEFAULT 'none');";
    private const string Added = "CREATE TABLE public.customers (id integer PRIMARY KEY, name text); ALTER TABLE public.customers ADD COLUMN phone varchar(20) NOT NULL DEFAULT 'none';";

    [TestMethod]
    public void ASelfDiffIsEmpty()
    {
        var model = Build(Added);

        AssertNoChanges(new DiffEngine().Diff(model, Build(Added)));
    }

    [TestMethod]
    public void AnAddedColumnAndTheSameColumnInlineAreTheSameSchema()
    {
        AssertNoChanges(new DiffEngine().Diff(Build(Inline), Build(Added)));
        AssertNoChanges(new DiffEngine().Diff(Build(Added), Build(Inline)));
    }

    [TestMethod]
    public void CosmeticDifferencesInTheAlterReportNothing()
    {
        var reformatted = "create table public.customers (id integer primary key, name text);\nALTER   TABLE  public.customers\n  ADD  COLUMN  PHONE  VARCHAR(20)  NOT NULL  DEFAULT  'none' ;";

        AssertNoChanges(new DiffEngine().Diff(Build(Inline), Build(reformatted)));
    }

    [TestMethod]
    public void TheAlterAndTheTableMayBeInDifferentFiles()
    {
        AssertNoChanges(new DiffEngine().Diff(
            Build(Inline),
            Build("ALTER TABLE public.customers ADD COLUMN phone varchar(20) NOT NULL DEFAULT 'none';", "CREATE TABLE public.customers (id integer PRIMARY KEY, name text);")));
    }

    [TestMethod]
    public void AnAddedColumnTheLiveTableLacksIsReported()
    {
        var changes = new DiffEngine().Diff(Build("CREATE TABLE public.customers (id integer PRIMARY KEY, name text);"), Build(Added));

        Assert.HasCount(1, changes.Modified.Where(static change => change.NodeKind == ChangeNodeKind.Table).ToArray());
        Assert.IsTrue(changes.Added.Any(static change => change.NodeId == "public.customers.phone"), Describe(changes));
    }

    [TestMethod]
    public void AMeaningfulDifferenceInAnAddedColumnIsStillReported()
    {
        var changes = new DiffEngine().Diff(
            Build(Inline),
            Build("CREATE TABLE public.customers (id integer PRIMARY KEY, name text); ALTER TABLE public.customers ADD COLUMN phone varchar(40) NOT NULL DEFAULT 'none';"));

        var column = changes.Modified.Single(static change => change.NodeId == "public.customers.phone");
        Assert.IsTrue(column.Details.Any(static detail => detail.PropertyName.Contains("Type", StringComparison.Ordinal)), Describe(changes));
    }

    [TestMethod]
    public void AChangedDefaultOfAnAddedColumnIsStillReported()
    {
        var changes = new DiffEngine().Diff(
            Build(Inline),
            Build("CREATE TABLE public.customers (id integer PRIMARY KEY, name text); ALTER TABLE public.customers ADD COLUMN phone varchar(20) NOT NULL DEFAULT 'other';"));

        Assert.IsTrue(changes.Modified.Any(static change => change.NodeId == "public.customers.phone"), Describe(changes));
    }

    private static void AssertNoChanges(ChangeSet changes)
        => Assert.AreEqual(0, changes.Added.Count + changes.Removed.Count + changes.Modified.Count, Describe(changes));

    private static string Describe(ChangeSet changes)
        => "Added: " + string.Join(", ", changes.Added.Select(static change => change.NodeId))
            + " Removed: " + string.Join(", ", changes.Removed.Select(static change => change.NodeId))
            + " Modified: " + string.Join("; ", changes.Modified.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => detail.PropertyName + ": " + detail.OldValue + " -> " + detail.NewValue)) + "]"));

    private static DatabaseModel Build(params string[] files)
    {
        var parser = new PgQueryParser();
        return new SemanticPipeline().Execute(files.Select(parser.Parse).ToArray());
    }
}
