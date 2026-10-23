using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-271: an index PostgreSQL marks <c>INVALID</c> (a failed <c>CREATE INDEX CONCURRENTLY</c>) is not finished, whatever its definition: it is a change
/// (<c>Valid: false -&gt; true</c>), so <c>publish</c> rebuilds it instead of taking it for done.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class InvalidIndexDiffTests
{
    private const string Sql = "CREATE TABLE public.t (a int); CREATE INDEX t_a ON public.t (a);";

    [TestMethod]
    public void AnInvalidLiveIndexIsAChange()
    {
        var live = Build(Sql);
        live.Indexes = live.Indexes.ToDictionary(static pair => pair.Key, static pair => pair.Value with { IsValid = false }, SqlIdentifiers.NameComparer);

        var diff = new DiffEngine().Diff(live, Build(Sql));

        var detail = diff.Modified.Where(static change => change.NewNode is IndexDefinition).SelectMany(static change => change.Details).Single(static detail => detail.PropertyName == nameof(IndexDefinition.IsValid));
        Assert.AreEqual("False -> True", $"{detail.OldValue} -> {detail.NewValue}");
    }

    [TestMethod]
    public void AValidIndexIsNoChange()
    {
        Assert.AreEqual(0, new DiffEngine().Diff(Build(Sql), Build(Sql)).Modified.Count);
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
