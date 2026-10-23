using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-40: the diff compared a trigger's table, function, timing, events, and row/statement level only, so a change to its <c>WHEN</c>
/// condition, <c>UPDATE OF</c> columns, function arguments, constraint-trigger options, or transition tables was never detected and
/// <c>publish</c> reported success while leaving the trigger stale. Both directions are tested (<c>copilot-diff.md</c>): each change is
/// reported, and the same trigger written differently (or as PostgreSQL prints it) is not.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TriggerDeepComparisonDiffTests
{
    private const string Prefix = """
        CREATE TABLE public.items (a integer, b integer, n text, v varchar(20));
        CREATE FUNCTION public.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE FUNCTION public.g() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        """;

    private const string Row = "CREATE TRIGGER trg AFTER UPDATE ON public.items FOR EACH ROW EXECUTE FUNCTION public.f();";

    [TestMethod]
    public void ASelfDiffIsEmpty()
    {
        const string everything = "CREATE CONSTRAINT TRIGGER trg AFTER INSERT OR UPDATE OF b, a ON public.items FROM public.items DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (new.a > 0) EXECUTE FUNCTION public.f('x', 'y');";

        AssertNoChanges(Diff(everything, everything));
    }

    [TestMethod]
    [DataRow("WHEN (new.a > 0)", "WHEN (new.a > 1)")]
    [DataRow("WHEN (new.a > 0)", "WHEN (new.a >= 0)")]
    [DataRow("WHEN (new.a > 0)", "WHEN (old.a > 0)")]
    [DataRow("WHEN (new.a > 0 AND new.b > 0)", "WHEN (new.a > 0 OR new.b > 0)")]
    [DataRow("WHEN (old.a IS DISTINCT FROM new.a)", "WHEN (old.a IS NOT DISTINCT FROM new.a)")]
    [DataRow("WHEN (new.n = 'x')", "WHEN (new.n = 'X')")]
    [DataRow("WHEN (new.n LIKE 'a%')", "WHEN (new.n NOT LIKE 'a%')")]
    [DataRow("WHEN (new.a IN (1, 2))", "WHEN (new.a IN (1, 3))")]
    [DataRow("WHEN (new.a BETWEEN 1 AND 5)", "WHEN (new.a BETWEEN 1 AND 6)")]
    [DataRow("WHEN ((new.a > 0 AND new.b > 0) OR new.a < 9)", "WHEN (new.a > 0 AND (new.b > 0 OR new.a < 9))")]
    public void AChangedWhenConditionIsReported(string before, string after)
    {
        var changes = Diff(Row.Replace("FOR EACH ROW", $"FOR EACH ROW {before}", StringComparison.Ordinal), Row.Replace("FOR EACH ROW", $"FOR EACH ROW {after}", StringComparison.Ordinal));

        var detail = changes.Modified.Single().Details.Single();
        Assert.AreEqual("WhenCondition", detail.PropertyName);
    }

    [TestMethod]
    public void AddingAndRemovingAWhenConditionAreReported()
    {
        var withWhen = Row.Replace("FOR EACH ROW", "FOR EACH ROW WHEN (new.a > 0)", StringComparison.Ordinal);

        Assert.AreEqual("WhenCondition", Diff(Row, withWhen).Modified.Single().Details.Single().PropertyName);
        Assert.AreEqual("WhenCondition", Diff(withWhen, Row).Modified.Single().Details.Single().PropertyName);
    }

    [TestMethod]
    [DataRow("WHEN (new.a > 0)", "WHEN (new.a>0)")]
    [DataRow("WHEN (new.a > 0)", "when ( NEW.A   >   0 )")]
    [DataRow("WHEN (new.a > 0)", "WHEN ((new.a > 0))")]
    [DataRow("WHEN (new.a > 0 AND new.b < 10)", "WHEN (((new.a > 0) AND (new.b < 10)))")]
    [DataRow("WHEN (new.a != 0)", "WHEN ((new.a <> 0))")]
    [DataRow("WHEN (new.n LIKE 'a%')", "WHEN ((new.n ~~ 'a%'::text))")]
    [DataRow("WHEN (new.a BETWEEN 1 AND 5)", "WHEN (((new.a >= 1) AND (new.a <= 5)))")]
    [DataRow("WHEN (new.a IN (1, 2, 3))", "WHEN ((new.a = ANY (ARRAY[1, 2, 3])))")]
    [DataRow("WHEN (new.a NOT IN (1, 2))", "WHEN ((new.a <> ALL (ARRAY[1, 2])))")]
    [DataRow("WHEN (lower(new.n) = 'x')", "WHEN ((lower(new.n) = 'x'::text))")]
    [DataRow("WHEN (pg_catalog.lower(new.n) = 'x')", "WHEN ((lower(new.n) = 'x'::text))")]
    [DataRow("WHEN (new.v = 'x')", "WHEN (((new.v)::text = 'x'::text))")]
    [DataRow("WHEN (new.a > 0 AND new.b > 0 AND new.a < 9)", "WHEN (((new.a > 0) AND (new.b > 0) AND (new.a < 9)))")]
    [DataRow("WHEN (COALESCE(new.a, 0) > 0)", "WHEN ((COALESCE(new.a, 0) > 0))")]
    [DataRow("WHEN (old.* IS DISTINCT FROM new.*)", "WHEN ((old.* IS DISTINCT FROM new.*))")]
    public void TheSameWhenConditionWrittenDifferentlyOrAsPostgreSqlPrintsItReportsNothing(string written, string rendered)
    {
        AssertNoChanges(Diff(
            Row.Replace("FOR EACH ROW", $"FOR EACH ROW {rendered}", StringComparison.Ordinal),
            Row.Replace("FOR EACH ROW", $"FOR EACH ROW {written}", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AddingChangingAndRemovingUpdateOfColumnsAreReported()
    {
        var updateOfA = Row.Replace("UPDATE", "UPDATE OF a", StringComparison.Ordinal);
        var updateOfAb = Row.Replace("UPDATE", "UPDATE OF a, b", StringComparison.Ordinal);

        Assert.AreEqual("UpdateColumns", Diff(Row, updateOfA).Modified.Single().Details.Single().PropertyName);
        Assert.AreEqual("UpdateColumns", Diff(updateOfA, updateOfAb).Modified.Single().Details.Single().PropertyName);
        Assert.AreEqual("UpdateColumns", Diff(updateOfAb, Row).Modified.Single().Details.Single().PropertyName);
        Assert.AreEqual("UpdateColumns", Diff(updateOfA, Row.Replace("UPDATE", "UPDATE OF b", StringComparison.Ordinal)).Modified.Single().Details.Single().PropertyName);
    }

    [TestMethod]
    public void TheOrderOfUpdateOfColumnsDoesNotMatter()
    {
        AssertNoChanges(Diff(
            Row.Replace("UPDATE", "UPDATE OF b, a", StringComparison.Ordinal),
            Row.Replace("UPDATE", "UPDATE OF a, b", StringComparison.Ordinal)));
        AssertNoChanges(Diff(
            Row.Replace("UPDATE", "UPDATE OF a, b", StringComparison.Ordinal),
            Row.Replace("UPDATE", "UPDATE OF A, B", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ChangedFunctionArgumentsAreReportedAndTheirOrderMatters()
    {
        var one = Row.Replace("f()", "f('x', 'y')", StringComparison.Ordinal);

        Assert.AreEqual("FunctionArguments", Diff(Row, one).Modified.Single().Details.Single().PropertyName);
        Assert.AreEqual("FunctionArguments", Diff(one, Row).Modified.Single().Details.Single().PropertyName);
        Assert.AreEqual("FunctionArguments", Diff(one, Row.Replace("f()", "f('y', 'x')", StringComparison.Ordinal)).Modified.Single().Details.Single().PropertyName);
        Assert.AreEqual("FunctionArguments", Diff(one, Row.Replace("f()", "f('x', 'z')", StringComparison.Ordinal)).Modified.Single().Details.Single().PropertyName);
        Assert.AreEqual("FunctionArguments", Diff(one, Row.Replace("f()", "f('X', 'y')", StringComparison.Ordinal)).Modified.Single().Details.Single().PropertyName, "Arguments are literals, so case matters.");
    }

    [TestMethod]
    public void ANumericArgumentIsTheSameAsItsQuotedForm()
    {
        // PostgreSQL stores every trigger argument as a string, and prints f('2') for f(2).
        AssertNoChanges(Diff(Row.Replace("f()", "f(2)", StringComparison.Ordinal), Row.Replace("f()", "f('2')", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ConstraintTriggerOptionsAreReported()
    {
        const string constraint = "CREATE CONSTRAINT TRIGGER trg AFTER UPDATE ON public.items DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f();";
        var immediate = constraint.Replace("INITIALLY DEFERRED", "INITIALLY IMMEDIATE", StringComparison.Ordinal);
        var notDeferrable = constraint.Replace("DEFERRABLE INITIALLY DEFERRED ", string.Empty, StringComparison.Ordinal);

        Assert.IsTrue(Diff(Row, constraint).Modified.Single().Details.Any(static detail => detail.PropertyName == "IsConstraintTrigger"));
        Assert.AreEqual("IsInitiallyDeferred", Diff(constraint, immediate).Modified.Single().Details.Single().PropertyName);
        Assert.IsTrue(Diff(constraint, notDeferrable).Modified.Single().Details.Any(static detail => detail.PropertyName == "IsDeferrable"));
        Assert.AreEqual("ConstraintTableName", Diff(notDeferrable, notDeferrable.Replace("ON public.items ", "ON public.items FROM public.items ", StringComparison.Ordinal)).Modified.Single().Details.Single().PropertyName);
    }

    [TestMethod]
    public void ANonDeferrableConstraintTriggerIsTheSameWhetherOrNotItSaysSo()
    {
        AssertNoChanges(Diff(
            "CREATE CONSTRAINT TRIGGER trg AFTER UPDATE ON public.items FOR EACH ROW EXECUTE FUNCTION public.f();",
            "CREATE CONSTRAINT TRIGGER trg AFTER UPDATE ON public.items NOT DEFERRABLE INITIALLY IMMEDIATE FOR EACH ROW EXECUTE FUNCTION public.f();"));
    }

    [TestMethod]
    public void TransitionTablesAreReported()
    {
        const string statement = "CREATE TRIGGER trg AFTER UPDATE ON public.items REFERENCING OLD TABLE AS ot NEW TABLE AS nt FOR EACH STATEMENT EXECUTE FUNCTION public.f();";
        var withoutNew = statement.Replace(" NEW TABLE AS nt", string.Empty, StringComparison.Ordinal);
        var renamed = statement.Replace("AS nt", "AS other", StringComparison.Ordinal);
        var none = statement.Replace("REFERENCING OLD TABLE AS ot NEW TABLE AS nt ", string.Empty, StringComparison.Ordinal);

        Assert.AreEqual("NewTransitionTable", Diff(statement, withoutNew).Modified.Single().Details.Single().PropertyName);
        Assert.AreEqual("NewTransitionTable", Diff(statement, renamed).Modified.Single().Details.Single().PropertyName);
        Assert.AreEqual(2, Diff(statement, none).Modified.Single().Details.Count(static detail => detail.PropertyName is "OldTransitionTable" or "NewTransitionTable"));
    }

    [TestMethod]
    public void TheSameTriggerWithTheClausesInTheirUsualFormReportsNothing()
    {
        AssertNoChanges(Diff(
            "CREATE TRIGGER trg AFTER INSERT OR UPDATE OF a, b ON public.items FOR EACH ROW WHEN (new.a > 0) EXECUTE FUNCTION public.f('x');",
            "create trigger trg after insert or update of B, A on public.items for each row when ((new.a > 0)) execute procedure public.f('x')"));
    }

    [TestMethod]
    public void TheExistingComparisonsStillWork()
    {
        Assert.AreEqual("FunctionName", Diff(Row, Row.Replace("f()", "g()", StringComparison.Ordinal)).Modified.Single().Details.Single().PropertyName);
        Assert.AreEqual("IsRowLevel", Diff(Row, Row.Replace("FOR EACH ROW", "FOR EACH STATEMENT", StringComparison.Ordinal)).Modified.Single().Details.Single().PropertyName);
        Assert.AreEqual("Timing", Diff(Row, Row.Replace("AFTER", "BEFORE", StringComparison.Ordinal)).Modified.Single().Details.Single().PropertyName);
    }

    private static ChangeSet Diff(string oldTrigger, string newTrigger)
        => new DiffEngine().Diff(Build(oldTrigger), Build(newTrigger));

    private static void AssertNoChanges(ChangeSet changes)
        => Assert.AreEqual(0, changes.Added.Count + changes.Removed.Count + changes.Modified.Count, Describe(changes));

    private static string Describe(ChangeSet changes)
        => "Added: " + string.Join(", ", changes.Added.Select(static change => change.NodeId))
            + " Removed: " + string.Join(", ", changes.Removed.Select(static change => change.NodeId))
            + " Modified: " + string.Join("; ", changes.Modified.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => detail.PropertyName + ": " + detail.OldValue + " -> " + detail.NewValue)) + "]"));

    private static DatabaseModel Build(string trigger)
    {
        var parser = new PgQueryParser();
        return new SemanticPipeline().Execute([parser.Parse(Prefix), parser.Parse(trigger)]);
    }
}
