using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>G-150: statistics are compared by table, kinds, column set, and expressions; the same statistics written another way are no change.</summary>
[TestClass]
[DoNotParallelize]
public sealed class StatisticsDiffTests
{
    private const string Table = "CREATE SCHEMA app; CREATE TABLE app.t (a int, b int, c text); CREATE TABLE app.u (a int, b int); ";

    private static string Statistics(string body) => Table + "CREATE STATISTICS app.s " + body + ";";

    [TestMethod]
    public void StatisticsDiffedWithThemselvesAreEmpty()
    {
        var model = Build(Statistics("(mcv) ON (lower(c)), a FROM app.t"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    [DataRow("ON a, b FROM app.t", "(ndistinct, dependencies, mcv) ON b, a FROM app.t")]
    [DataRow("ON (lower(c)), a FROM app.t", "ON (LOWER((c))), a FROM app.t")]
    public void TheSameStatisticsWrittenTwoWaysReportNothing(string first, string second)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Statistics(first)), Build(Statistics(second)))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Statistics(second)), Build(Statistics(first)))));
    }

    [TestMethod]
    [DataRow("ON a, b FROM app.t", "(mcv) ON a, b FROM app.t", "Kinds")]
    [DataRow("ON a, b FROM app.t", "ON a, c FROM app.t", "Columns")]
    [DataRow("ON a, b FROM app.t", "ON a, b FROM app.u", "TableName")]
    [DataRow("ON (lower(c)), a FROM app.t", "ON (upper(c)), a FROM app.t", "Expressions")]
    public void ARealChangeIsReportedWithTheProperty(string before, string after, string property)
    {
        var changes = new DiffEngine().Diff(Build(Statistics(before)), Build(Statistics(after)));

        var modified = changes.Modified.Single(static change => change.NodeKind == ChangeNodeKind.Statistics);
        Assert.IsTrue(modified.Details.Any(detail => detail.PropertyName == property), string.Join(", ", modified.Details.Select(static d => d.PropertyName)));
    }

    [TestMethod]
    public void AnAddedAndARemovedStatisticsObjectAreReported()
    {
        var with = Build(Statistics("ON a, b FROM app.t"));
        var without = Build(Table);

        Assert.AreEqual(1, new DiffEngine().Diff(without, with).Added.Count(static change => change.NodeKind == ChangeNodeKind.Statistics));
        Assert.AreEqual(1, new DiffEngine().Diff(with, without).Removed.Count(static change => change.NodeKind == ChangeNodeKind.Statistics));
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
