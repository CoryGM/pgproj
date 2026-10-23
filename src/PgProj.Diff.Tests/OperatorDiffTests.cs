using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-129: an operator is compared by its function, commutator, negator, estimators, and flags; the same operator written another way (an unqualified
/// name, a spelled-out schema) is not a change, and a real change is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OperatorDiffTests
{
    private const string Setup = "CREATE TYPE public.mood AS ENUM ('a', 'b'); "
        + "CREATE FUNCTION public.eq1(public.mood, public.mood) RETURNS boolean LANGUAGE sql AS $$ SELECT true $$; "
        + "CREATE FUNCTION public.eq2(public.mood, public.mood) RETURNS boolean LANGUAGE sql AS $$ SELECT false $$; ";

    private static string Operator(string options) => Setup + "CREATE OPERATOR public.=== (LEFTARG = public.mood, RIGHTARG = public.mood, " + options + ");";

    [TestMethod]
    public void AnOperatorDiffedWithItselfIsEmpty()
    {
        var model = Build(Operator("FUNCTION = public.eq1, RESTRICT = eqsel, JOIN = eqjoinsel, HASHES"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    [DataRow("FUNCTION = public.eq1, RESTRICT = eqsel", "FUNCTION = eq1, RESTRICT = pg_catalog.eqsel")]
    [DataRow("PROCEDURE = public.eq1", "FUNCTION = public.eq1")]
    [DataRow("FUNCTION = public.eq1, COMMUTATOR = ===", "FUNCTION = public.eq1, COMMUTATOR = OPERATOR(public.===)")]
    public void TheSameOperatorWrittenTwoWaysReportsNothing(string first, string second)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Operator(first)), Build(Operator(second)))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Operator(second)), Build(Operator(first)))));
    }

    [TestMethod]
    [DataRow("FUNCTION = public.eq1", "FUNCTION = public.eq2", nameof(OperatorDefinition.FunctionName))]
    [DataRow("FUNCTION = public.eq1", "FUNCTION = public.eq1, RESTRICT = eqsel", nameof(OperatorDefinition.RestrictFunction))]
    [DataRow("FUNCTION = public.eq1, JOIN = eqjoinsel", "FUNCTION = public.eq1, JOIN = neqjoinsel", nameof(OperatorDefinition.JoinFunction))]
    [DataRow("FUNCTION = public.eq1", "FUNCTION = public.eq1, HASHES", nameof(OperatorDefinition.IsHashes))]
    [DataRow("FUNCTION = public.eq1", "FUNCTION = public.eq1, MERGES", nameof(OperatorDefinition.IsMerges))]
    [DataRow("FUNCTION = public.eq1", "FUNCTION = public.eq1, COMMUTATOR = ===", nameof(OperatorDefinition.Commutator))]
    [DataRow("FUNCTION = public.eq1", "FUNCTION = public.eq1, NEGATOR = <>", nameof(OperatorDefinition.Negator))]
    public void AChangeToAnOptionIsReported(string before, string after, string property)
    {
        var diff = new DiffEngine().Diff(Build(Operator(before)), Build(Operator(after)));

        var modified = diff.Modified.Single(static change => change.NodeKind == ChangeNodeKind.Operator);
        Assert.IsTrue(modified.Details.Any(detail => detail.PropertyName == property), string.Join("; ", modified.Details.Select(static d => d.PropertyName)));
    }

    [TestMethod]
    public void AnAddedOperatorAndARemovedOperatorAreReported()
    {
        var withOperator = Build(Operator("FUNCTION = public.eq1"));
        var without = Build(Setup);

        Assert.AreEqual(1, new DiffEngine().Diff(without, withOperator).Added.Count(static change => change.NodeKind == ChangeNodeKind.Operator));
        Assert.AreEqual(1, new DiffEngine().Diff(withOperator, without).Removed.Count(static change => change.NodeKind == ChangeNodeKind.Operator));
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
