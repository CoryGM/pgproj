using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-131: a cast is compared by its method, its function and the function's argument types (in comparison form, in order), and when it applies;
/// the same cast written another way is not a change, and a real change is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CastDiffTests
{
    private const string Setup = "CREATE TYPE public.mood AS ENUM ('a', 'b'); "
        + "CREATE FUNCTION public.t2m(text) RETURNS public.mood LANGUAGE sql AS $$ SELECT $1::public.mood $$; "
        + "CREATE FUNCTION public.t2m2(text) RETURNS public.mood LANGUAGE sql AS $$ SELECT $1::public.mood $$; ";

    private static string Cast(string clause) => Setup + "CREATE CAST (text AS public.mood) " + clause + ";";

    [TestMethod]
    public void ACastDiffedWithItselfIsEmpty()
    {
        var model = Build(Cast("WITH FUNCTION public.t2m(text) AS ASSIGNMENT"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    [DataRow("WITH FUNCTION public.t2m(text) AS ASSIGNMENT", "WITH FUNCTION t2m(pg_catalog.text) AS ASSIGNMENT")]
    [DataRow("WITH FUNCTION t2m(text)", "WITH FUNCTION public.t2m(text)")]
    public void TheSameCastWrittenTwoWaysReportsNothing(string first, string second)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Cast(first)), Build(Cast(second)))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Cast(second)), Build(Cast(first)))));
    }

    [TestMethod]
    [DataRow("WITH FUNCTION t2m(text)", "WITH FUNCTION t2m2(text)", nameof(CastDefinition.FunctionName))]
    [DataRow("WITH FUNCTION t2m(text)", "WITH FUNCTION t2m(text) AS IMPLICIT", nameof(CastDefinition.Context))]
    [DataRow("WITH FUNCTION t2m(text) AS ASSIGNMENT", "WITH FUNCTION t2m(text) AS IMPLICIT", nameof(CastDefinition.Context))]
    [DataRow("WITH FUNCTION t2m(text)", "WITH INOUT", nameof(CastDefinition.Method))]
    public void AChangeToTheFunctionTheContextOrTheMethodIsReported(string before, string after, string property)
    {
        var diff = new DiffEngine().Diff(Build(Cast(before)), Build(Cast(after)));

        var change = diff.Modified.Single(static c => c.NodeKind == ChangeNodeKind.Cast);
        Assert.IsTrue(change.Details.Any(detail => detail.PropertyName == property), string.Join(", ", change.Details.Select(static d => d.PropertyName)));
    }

    [TestMethod]
    public void AnAddedAndARemovedCastAreAddedAndRemoved()
    {
        var with = Build(Cast("WITH FUNCTION t2m(text)"));
        var without = Build(Setup);

        Assert.AreEqual(1, new DiffEngine().Diff(without, with).Added.Count(static c => c.NodeKind == ChangeNodeKind.Cast));
        Assert.AreEqual(1, new DiffEngine().Diff(with, without).Removed.Count(static c => c.NodeKind == ChangeNodeKind.Cast));
    }

    [TestMethod]
    public void TwoCastsBetweenDifferentTypesAreDifferentCasts()
    {
        var first = Build(Setup + "CREATE CAST (text AS public.mood) WITH FUNCTION t2m(text);");
        var second = Build(Setup + "CREATE CAST (varchar AS public.mood) WITH INOUT;");

        var diff = new DiffEngine().Diff(first, second);
        Assert.AreEqual(1, diff.Added.Count(static c => c.NodeKind == ChangeNodeKind.Cast));
        Assert.AreEqual(1, diff.Removed.Count(static c => c.NodeKind == ChangeNodeKind.Cast));
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
