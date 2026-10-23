using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-121 part 2: a generated column that changes between <c>STORED</c> and <c>VIRTUAL</c> (PostgreSQL 18) is a modified column, reported as
/// <c>IsVirtual</c>; the same column written two ways (<c>VIRTUAL</c>, or neither keyword) is not.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class VirtualGeneratedColumnDiffTests
{
    private const string Table = "CREATE TABLE public.t (a integer, b integer GENERATED ALWAYS AS ({0}){1});";

    [TestMethod]
    [DataRow(" VIRTUAL")]
    [DataRow(" STORED")]
    [DataRow("")]
    public void AGeneratedColumnDiffedWithItselfIsEmpty(string kind)
    {
        var model = Build(string.Format(Table, "a * 2", kind));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(string.Format(Table, "a * 2", kind)), Build(string.Format(Table, "a * 2", kind)))));
    }

    [TestMethod]
    public void VirtualAndNeitherKeywordAreTheSameColumn()
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(string.Format(Table, "a * 2", " VIRTUAL")), Build(string.Format(Table, "a * 2", string.Empty)))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(string.Format(Table, "a * 2", string.Empty)), Build(string.Format(Table, "a * 2", " VIRTUAL")))));
    }

    [TestMethod]
    public void TheSameVirtualExpressionWrittenTwoWaysReportsNothing()
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(string.Format(Table, "(a * 2)", " VIRTUAL")), Build(string.Format(Table, "a   *   2", " VIRTUAL")))));
    }

    [TestMethod]
    [DataRow(" STORED", " VIRTUAL")]
    [DataRow(" VIRTUAL", " STORED")]
    [DataRow(" STORED", "")]
    public void AChangeBetweenStoredAndVirtualIsReportedAsIsVirtual(string before, string after)
    {
        var diff = new DiffEngine().Diff(Build(string.Format(Table, "a * 2", before)), Build(string.Format(Table, "a * 2", after)));

        var detail = diff.Modified.Single(static change => change.NodeId.EndsWith(".b", StringComparison.OrdinalIgnoreCase)).Details.Single();
        Assert.AreEqual(nameof(ColumnDefinition.IsVirtual), detail.PropertyName);
        Assert.AreEqual((before != " STORED").ToString(), detail.OldValue?.ToString());
        Assert.AreEqual((after != " STORED").ToString(), detail.NewValue?.ToString());
    }

    [TestMethod]
    public void AChangedVirtualExpressionIsReportedAsTheExpression()
    {
        var diff = new DiffEngine().Diff(Build(string.Format(Table, "a * 2", " VIRTUAL")), Build(string.Format(Table, "a * 3", " VIRTUAL")));

        var detail = diff.Modified.Single(static change => change.NodeId.EndsWith(".b", StringComparison.OrdinalIgnoreCase)).Details.Single();
        Assert.AreEqual(nameof(ColumnDefinition.GeneratedExpression), detail.PropertyName);
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
