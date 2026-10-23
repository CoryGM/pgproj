using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-130: an aggregate is compared by each option in comparison form; an option that one side states and the other leaves at PostgreSQL's default is not a change, the same
/// aggregate written another way is not, and a real change is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AggregateDiffTests
{
    private const string Head = "CREATE FUNCTION public.step(s integer, x integer) RETURNS integer LANGUAGE sql AS $$ SELECT s $$; "
        + "CREATE FUNCTION public.step2(s integer, x integer) RETURNS integer LANGUAGE sql AS $$ SELECT s + x $$; ";

    private static string Aggregate(string options) => Head + "CREATE AGGREGATE public.a (integer) (" + options + ");";

    [TestMethod]
    public void AnAggregateDiffedWithItselfIsEmpty()
    {
        var model = Build(Aggregate("SFUNC = public.step, STYPE = integer, INITCOND = '0', PARALLEL = SAFE"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    [DataRow("SFUNC = public.step, STYPE = integer", "SFUNC = step, STYPE = pg_catalog.int4, PARALLEL = UNSAFE, FINALFUNC_MODIFY = READ_ONLY, SSPACE = 0")]
    [DataRow("SFUNC = public.step, STYPE = integer, FINALFUNC_EXTRA", "STYPE = integer, FINALFUNC_EXTRA, SFUNC = public.step")]
    public void TheSameAggregateWrittenTwoWaysReportsNothing(string first, string second)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Aggregate(first)), Build(Aggregate(second)))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Aggregate(second)), Build(Aggregate(first)))));
    }

    [TestMethod]
    [DataRow("SFUNC = public.step, STYPE = integer", "SFUNC = public.step2, STYPE = integer", "Option:sfunc")]
    [DataRow("SFUNC = public.step, STYPE = integer", "SFUNC = public.step, STYPE = bigint", "Option:stype")]
    [DataRow("SFUNC = public.step, STYPE = integer", "SFUNC = public.step, STYPE = integer, INITCOND = '1'", "Option:initcond")]
    [DataRow("SFUNC = public.step, STYPE = integer", "SFUNC = public.step, STYPE = integer, PARALLEL = SAFE", "Option:parallel")]
    [DataRow("SFUNC = public.step, STYPE = integer", "SFUNC = public.step, STYPE = integer, FINALFUNC = public.step2", "Option:finalfunc")]
    [DataRow("SFUNC = public.step, STYPE = integer", "SFUNC = public.step, STYPE = integer, FINALFUNC_EXTRA", "Option:finalfunc_extra")]
    [DataRow("SFUNC = public.step, STYPE = integer", "SFUNC = public.step, STYPE = integer, SORTOP = >", "Option:sortop")]
    public void AChangeToAnOptionIsReported(string before, string after, string option)
    {
        var diff = new DiffEngine().Diff(Build(Aggregate(before)), Build(Aggregate(after)));

        var modified = diff.Modified.Single(static change => change.NodeKind == ChangeNodeKind.Aggregate);
        Assert.IsTrue(modified.Details.Any(detail => detail.PropertyName == option), string.Join("; ", modified.Details.Select(static d => d.PropertyName)));
    }

    [TestMethod]
    public void AnAddedAggregateAndARemovedAggregateAreReported()
    {
        var with = Build(Aggregate("SFUNC = public.step, STYPE = integer"));
        var without = Build(Head);

        Assert.AreEqual(1, new DiffEngine().Diff(without, with).Added.Count(static change => change.NodeKind == ChangeNodeKind.Aggregate));
        Assert.AreEqual(1, new DiffEngine().Diff(with, without).Removed.Count(static change => change.NodeKind == ChangeNodeKind.Aggregate));
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
