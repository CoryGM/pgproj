using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-134: a publication is compared by whether it is <c>FOR ALL TABLES</c>, its options in comparison form, its schemas, and its tables with their column lists and row filters;
/// the same publication written another way is not a change, and a real change is, with the detail that says what.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PublicationDiffTests
{
    private const string Tables = "CREATE TABLE public.t (id int PRIMARY KEY, n int); CREATE TABLE public.u (id int PRIMARY KEY); CREATE SCHEMA app; ";

    private static string Publication(string body) => Tables + "CREATE PUBLICATION p " + body + ";";

    [TestMethod]
    public void APublicationDiffedWithItselfIsEmpty()
    {
        var model = Build(Publication("FOR TABLE public.t (id, n) WHERE (n > 0), public.u WITH (publish = 'insert')"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    [DataRow("FOR TABLE public.t", "FOR TABLE t WITH (publish = 'insert, update, delete, truncate', publish_via_partition_root = false)")]
    [DataRow("FOR TABLE public.t (id, n) WHERE (n > 0)", "FOR TABLE public.t (n, id) WHERE ((n) > 0)")]
    [DataRow("FOR TABLE public.t WITH (publish = 'update, insert')", "FOR TABLE public.t WITH (publish = 'insert,update')")]
    public void TheSamePublicationWrittenTwoWaysReportsNothing(string first, string second)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Publication(first)), Build(Publication(second)))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Publication(second)), Build(Publication(first)))));
    }

    [TestMethod]
    [DataRow("FOR TABLE public.t", "FOR ALL TABLES", "IsAllTables")]
    [DataRow("FOR TABLE public.t", "FOR TABLE public.t WITH (publish = 'insert')", "Option:publish")]
    [DataRow("FOR TABLE public.t", "FOR TABLE public.t WITH (publish_via_partition_root = true)", "Option:publish_via_partition_root")]
    [DataRow("FOR TABLE public.t", "FOR TABLE public.t, public.u", "Tables")]
    [DataRow("FOR TABLE public.t", "FOR TABLE public.t, TABLES IN SCHEMA app", "Schemas")]
    [DataRow("FOR TABLE public.t", "FOR TABLE public.t (id)", "Table:public.t:Columns")]
    [DataRow("FOR TABLE public.t WHERE (id > 0)", "FOR TABLE public.t WHERE (id > 1)", "Table:public.t:Filter")]
    public void AChangeIsReportedWithWhatChanged(string before, string after, string property)
    {
        var diff = new DiffEngine().Diff(Build(Publication(before)), Build(Publication(after)));

        var modified = diff.Modified.Single(static change => change.NodeKind == ChangeNodeKind.Publication);
        Assert.IsTrue(modified.Details.Any(detail => detail.PropertyName == property), string.Join("; ", modified.Details.Select(static d => d.PropertyName)));
    }

    [TestMethod]
    public void AnAddedPublicationAndARemovedPublicationAreReported()
    {
        var with = Build(Publication("FOR TABLE public.t"));
        var without = Build(Tables);

        Assert.AreEqual(1, new DiffEngine().Diff(without, with).Added.Count(static change => change.NodeKind == ChangeNodeKind.Publication));
        Assert.AreEqual(1, new DiffEngine().Diff(with, without).Removed.Count(static change => change.NodeKind == ChangeNodeKind.Publication));
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
