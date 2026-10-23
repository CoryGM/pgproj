using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-40: the trigger comparison ignored <c>WHEN</c>, <c>UPDATE OF</c>, function arguments, constraint-trigger options, and transition
/// tables, so changing them was never detected. The parser now captures each of them, on the project side and (because the live
/// snapshot is parsed the same way) on the live side.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TriggerOptionsParsingTests
{
    [TestMethod]
    public void UpdateOfColumnsAreCapturedInTheOrderWritten()
    {
        var trigger = Parse("CREATE TRIGGER t AFTER INSERT OR UPDATE OF b, a ON public.x FOR EACH ROW EXECUTE FUNCTION public.f();");

        CollectionAssert.AreEqual(new[] { "b", "a" }, trigger.UpdateColumns.ToArray());
    }

    [TestMethod]
    public void AnUpdateTriggerWithoutAColumnListHasNone()
    {
        Assert.IsEmpty(Parse("CREATE TRIGGER t AFTER UPDATE ON public.x FOR EACH ROW EXECUTE FUNCTION public.f();").UpdateColumns.ToArray());
    }

    [TestMethod]
    public void FunctionArgumentsAreCapturedInOrderAsStrings()
    {
        var trigger = Parse("CREATE TRIGGER t AFTER UPDATE ON public.x FOR EACH ROW EXECUTE FUNCTION public.f('x', 2, 'it''s');");

        CollectionAssert.AreEqual(new[] { "x", "2", "it's" }, trigger.FunctionArguments.ToArray());
    }

    [TestMethod]
    public void ATriggerWithoutArgumentsHasNone()
    {
        Assert.IsEmpty(Parse("CREATE TRIGGER t AFTER UPDATE ON public.x FOR EACH ROW EXECUTE FUNCTION public.f();").FunctionArguments.ToArray());
    }

    [TestMethod]
    public void ConstraintTriggerOptionsAreCaptured()
    {
        var trigger = Parse("CREATE CONSTRAINT TRIGGER t AFTER UPDATE ON public.x FROM public.y DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f();");

        Assert.IsTrue(trigger.IsConstraintTrigger);
        Assert.IsTrue(trigger.IsDeferrable);
        Assert.IsTrue(trigger.IsInitiallyDeferred);
        Assert.AreEqual("public.y", trigger.ConstraintTableName);
    }

    [TestMethod]
    public void ADeferrableConstraintTriggerThatIsInitiallyImmediateIsNotInitiallyDeferred()
    {
        var trigger = Parse("CREATE CONSTRAINT TRIGGER t AFTER UPDATE ON public.x DEFERRABLE INITIALLY IMMEDIATE FOR EACH ROW EXECUTE FUNCTION public.f();");

        Assert.IsTrue(trigger.IsDeferrable);
        Assert.IsFalse(trigger.IsInitiallyDeferred);
        Assert.IsNull(trigger.ConstraintTableName);
    }

    [TestMethod]
    public void AnOrdinaryTriggerIsNotAConstraintTrigger()
    {
        var trigger = Parse("CREATE TRIGGER t AFTER UPDATE ON public.x FOR EACH ROW EXECUTE FUNCTION public.f();");

        Assert.IsFalse(trigger.IsConstraintTrigger);
        Assert.IsFalse(trigger.IsDeferrable);
        Assert.IsFalse(trigger.IsInitiallyDeferred);
    }

    [TestMethod]
    public void TransitionTablesAreCaptured()
    {
        var both = Parse("CREATE TRIGGER t AFTER UPDATE ON public.x REFERENCING OLD TABLE AS ot NEW TABLE AS nt FOR EACH STATEMENT EXECUTE FUNCTION public.f();");
        var onlyNew = Parse("CREATE TRIGGER t AFTER INSERT ON public.x REFERENCING NEW TABLE AS nt FOR EACH STATEMENT EXECUTE FUNCTION public.f();");
        var onlyOld = Parse("CREATE TRIGGER t AFTER DELETE ON public.x REFERENCING OLD TABLE AS ot FOR EACH STATEMENT EXECUTE FUNCTION public.f();");

        Assert.AreEqual("ot", both.OldTransitionTable);
        Assert.AreEqual("nt", both.NewTransitionTable);
        Assert.IsNull(onlyNew.OldTransitionTable);
        Assert.AreEqual("nt", onlyNew.NewTransitionTable);
        Assert.AreEqual("ot", onlyOld.OldTransitionTable);
        Assert.IsNull(onlyOld.NewTransitionTable);
    }

    [TestMethod]
    public void AWhenConditionIsCapturedAsWrittenAndAsATree()
    {
        var trigger = Parse("CREATE TRIGGER t AFTER UPDATE ON public.x FOR EACH ROW WHEN (old.a IS DISTINCT FROM new.a) EXECUTE FUNCTION public.f();");

        Assert.AreEqual("old.a IS DISTINCT FROM new.a", trigger.RawWhenCondition);
        StringAssert.Contains(trigger.WhenConditionAst, "AEXPR_DISTINCT");
    }

    [TestMethod]
    public void ATriggerWithoutAWhenConditionHasNone()
    {
        var trigger = Parse("CREATE TRIGGER t AFTER UPDATE ON public.x FOR EACH ROW EXECUTE FUNCTION public.f();");

        Assert.IsNull(trigger.RawWhenCondition);
        Assert.IsNull(trigger.WhenConditionAst);
    }

    [TestMethod]
    [DataRow("WHEN (new.a > 0 AND (new.b > 0 OR new.b IS NULL))", "new.a > 0 AND (new.b > 0 OR new.b IS NULL)")]
    [DataRow("WHEN ((new.a > 0))", "(new.a > 0)")]
    [DataRow("when (new.n = 'when (x)')", "new.n = 'when (x)'")]
    [DataRow("WHEN (lower(new.n) = 'a)b')", "lower(new.n) = 'a)b'")]
    [DataRow("WHEN (new.\"When\" > 0)", "new.\"When\" > 0")]
    [DataRow("WHEN (new.a > 0 /* a) comment */)", "new.a > 0 /* a) comment */")]
    public void TheWhenTextIsSlicedBetweenTheClausesParentheses(string clause, string expected)
    {
        var trigger = Parse($"CREATE TRIGGER t AFTER UPDATE ON public.x FOR EACH ROW {clause} EXECUTE FUNCTION public.f();");

        Assert.AreEqual(expected, trigger.RawWhenCondition);
    }

    [TestMethod]
    public void AFunctionArgumentContainingWhenIsNotAWhenCondition()
    {
        var trigger = Parse("CREATE TRIGGER t AFTER UPDATE ON public.x FOR EACH ROW EXECUTE FUNCTION public.f('when (a)');");

        Assert.IsNull(trigger.RawWhenCondition);
    }

    [TestMethod]
    public void TheFullStatementIsStillPreservedForDeployment()
    {
        const string sql = "CREATE TRIGGER t AFTER UPDATE OF a ON public.x FOR EACH ROW WHEN (new.a > 0) EXECUTE FUNCTION public.f('x');";

        Assert.AreEqual(sql.TrimEnd(';'), Parse(sql).RawStatement);
    }

    private static TriggerNode Parse(string sql) => new PgQueryParser().Parse(sql).Nodes.OfType<TriggerNode>().Single();
}
