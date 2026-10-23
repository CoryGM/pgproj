using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-121 part 2: a constraint that changes between enforced and <c>NOT ENFORCED</c> is a modified constraint (reported as
/// <c>IsEnforced</c>), and the same constraint written two ways is not.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ConstraintEnforcementDiffTests
{
    private const string Check = "CREATE TABLE public.t (a integer, CONSTRAINT c CHECK (a > 0){0});";
    private const string ForeignKey = "CREATE TABLE public.p (id integer PRIMARY KEY); CREATE TABLE public.t (a integer, CONSTRAINT c FOREIGN KEY (a) REFERENCES public.p (id){0});";

    [TestMethod]
    [DataRow(Check)]
    [DataRow(ForeignKey)]
    public void ANotEnforcedConstraintDiffedWithItselfIsEmpty(string template)
    {
        var model = Build(string.Format(template, " NOT ENFORCED"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(string.Format(template, " NOT ENFORCED")), Build(string.Format(template, " NOT ENFORCED")))));
    }

    [TestMethod]
    [DataRow(Check, "", " ENFORCED")]
    [DataRow(ForeignKey, "", " ENFORCED")]
    [DataRow(Check, " NOT VALID", " NOT VALID")]
    public void TheSameEnforcedConstraintWrittenTwoWaysReportsNothing(string template, string first, string second)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(string.Format(template, first)), Build(string.Format(template, second)))));
    }

    [TestMethod]
    [DataRow(Check, "", " NOT ENFORCED")]
    [DataRow(Check, " NOT ENFORCED", "")]
    [DataRow(ForeignKey, "", " NOT ENFORCED")]
    [DataRow(ForeignKey, " NOT ENFORCED", " ENFORCED")]
    public void AChangeBetweenEnforcedAndNotEnforcedIsReported(string template, string before, string after)
    {
        var diff = new DiffEngine().Diff(Build(string.Format(template, before)), Build(string.Format(template, after)));

        var change = diff.Modified.Single(static candidate => candidate.NodeId.EndsWith(".c", StringComparison.OrdinalIgnoreCase));
        var detail = change.Details.Single();
        Assert.AreEqual(nameof(ConstraintDefinition.IsEnforced), detail.PropertyName);
        Assert.AreEqual((!before.Contains("NOT ENFORCED", StringComparison.Ordinal)).ToString(), detail.OldValue?.ToString());
        Assert.AreEqual((!after.Contains("NOT ENFORCED", StringComparison.Ordinal)).ToString(), detail.NewValue?.ToString());
    }

    [TestMethod]
    public void ANotEnforcedConstraintAddedOrRemovedIsAnAddedOrRemovedConstraint()
    {
        var withOut = Build("CREATE TABLE public.t (a integer);");
        var with = Build(string.Format(Check, " NOT ENFORCED"));

        Assert.IsTrue(new DiffEngine().Diff(withOut, with).Added.Any(static change => change.NodeId.EndsWith(".c", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(new DiffEngine().Diff(with, withOut).Removed.Any(static change => change.NodeId.EndsWith(".c", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
