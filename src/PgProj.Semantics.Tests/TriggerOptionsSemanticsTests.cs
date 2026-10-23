using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-40: the options of a trigger that the diff ignored (<c>WHEN</c>, <c>UPDATE OF</c>, arguments, constraint-trigger options, and
/// transition tables) are modeled and canonicalized as <c>copilot-semantic.md</c> requires: a set is sorted, a positional list is not,
/// identifiers are case-canonicalized, and the expression is kept as written beside its comparison form.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TriggerOptionsSemanticsTests
{
    private const string Prefix = """
        CREATE TABLE public.items (a integer, b integer, n text, v varchar(20), "Mixed" integer);
        CREATE FUNCTION public.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        """;

    [TestMethod]
    public void UpdateOfColumnsAreASortedSetWhateverOrderTheyWereWritten()
    {
        var one = Trigger("CREATE TRIGGER t AFTER UPDATE OF b, a ON public.items FOR EACH ROW EXECUTE FUNCTION public.f();");
        var other = Trigger("CREATE TRIGGER t AFTER UPDATE OF a, B ON public.items FOR EACH ROW EXECUTE FUNCTION public.f();");

        CollectionAssert.AreEqual(new[] { "a", "b" }, one.UpdateColumns.ToArray());
        CollectionAssert.AreEqual(one.UpdateColumns.ToArray(), other.UpdateColumns.ToArray());
    }

    [TestMethod]
    public void FunctionArgumentsKeepTheirOrder()
    {
        var trigger = Trigger("CREATE TRIGGER t AFTER UPDATE ON public.items FOR EACH ROW EXECUTE FUNCTION public.f('z', 'a', 'M');");

        CollectionAssert.AreEqual(new[] { "z", "a", "M" }, trigger.FunctionArguments.ToArray());
    }

    [TestMethod]
    public void ConstraintTriggerOptionsAndTransitionTablesAreModeled()
    {
        var constraint = Trigger("CREATE CONSTRAINT TRIGGER t AFTER UPDATE ON public.items FROM public.items DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f();");
        var transition = Trigger("CREATE TRIGGER t AFTER UPDATE ON public.items REFERENCING OLD TABLE AS Old_Rows NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION public.f();");

        Assert.IsTrue(constraint.IsConstraintTrigger);
        Assert.IsTrue(constraint.IsDeferrable);
        Assert.IsTrue(constraint.IsInitiallyDeferred);
        Assert.AreEqual("public.items", constraint.ConstraintTableName);
        Assert.AreEqual("old_rows", transition.OldTransitionTable);
        Assert.AreEqual("new_rows", transition.NewTransitionTable);
    }

    [TestMethod]
    public void TheWhenConditionIsKeptAsWrittenBesideItsComparisonForm()
    {
        var trigger = Trigger("CREATE TRIGGER t AFTER UPDATE ON public.items FOR EACH ROW WHEN ( NEW.a  >  0 ) EXECUTE FUNCTION public.f();");

        Assert.AreEqual("NEW.a  >  0", trigger.RawWhenCondition);
        Assert.IsFalse(string.IsNullOrWhiteSpace(trigger.NormalizedWhenCondition));
        Assert.AreNotEqual(trigger.RawWhenCondition, trigger.NormalizedWhenCondition);
    }

    [TestMethod]
    public void ATriggerWithoutAWhenConditionHasNeither()
    {
        var trigger = Trigger("CREATE TRIGGER t AFTER UPDATE ON public.items FOR EACH ROW EXECUTE FUNCTION public.f();");

        Assert.IsNull(trigger.RawWhenCondition);
        Assert.IsNull(trigger.NormalizedWhenCondition);
    }

    [TestMethod]
    public void TheComparisonFormIgnoresWhitespaceCaseAndParentheses()
    {
        var written = Trigger("CREATE TRIGGER t AFTER UPDATE ON public.items FOR EACH ROW WHEN (new.a>0 AND new.b<10) EXECUTE FUNCTION public.f();");
        var rendered = Trigger("CREATE TRIGGER t AFTER UPDATE ON public.items FOR EACH ROW WHEN (((new.a > 0) AND (new.b < 10))) EXECUTE FUNCTION public.f();");

        Assert.AreEqual(written.NormalizedWhenCondition, rendered.NormalizedWhenCondition);
    }

    [TestMethod]
    public void ACastToTextIsFoldedOnlyForAVarcharColumnOfTheTriggersOwnTable()
    {
        var varchar = Trigger("CREATE TRIGGER t AFTER UPDATE ON public.items FOR EACH ROW WHEN (new.v = 'x') EXECUTE FUNCTION public.f();");
        var varcharRendered = Trigger("CREATE TRIGGER t AFTER UPDATE ON public.items FOR EACH ROW WHEN (((new.v)::text = 'x'::text)) EXECUTE FUNCTION public.f();");
        var text = Trigger("CREATE TRIGGER t AFTER UPDATE ON public.items FOR EACH ROW WHEN (new.n = 'x') EXECUTE FUNCTION public.f();");
        var textCast = Trigger("CREATE TRIGGER t AFTER UPDATE ON public.items FOR EACH ROW WHEN (new.n::text = 'x') EXECUTE FUNCTION public.f();");

        Assert.AreEqual(varchar.NormalizedWhenCondition, varcharRendered.NormalizedWhenCondition, "PostgreSQL adds ::text to a varchar column itself.");
        Assert.AreNotEqual(text.NormalizedWhenCondition, textCast.NormalizedWhenCondition, "A cast the author wrote on a non-varchar column is kept.");
    }

    [TestMethod]
    public void TheTableMayBeInAnotherFileAndTheSchemaMayBeImplicit()
    {
        var parser = new PgQueryParser();
        var model = new SemanticPipeline().Execute(
        [
            parser.Parse("CREATE TRIGGER t AFTER UPDATE ON items FOR EACH ROW WHEN ((new.v)::text = 'x'::text) EXECUTE FUNCTION public.f();"),
            parser.Parse("CREATE TABLE public.items (v varchar(5));"),
            parser.Parse("CREATE FUNCTION public.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;"),
        ]);
        var plain = new SemanticPipeline().Execute(
        [
            parser.Parse("CREATE TABLE public.items (v varchar(5));"),
            parser.Parse("CREATE FUNCTION public.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;"),
            parser.Parse("CREATE TRIGGER t AFTER UPDATE ON public.items FOR EACH ROW WHEN (new.v = 'x') EXECUTE FUNCTION public.f();"),
        ]);

        Assert.AreEqual(plain.Triggers.Values.Single().NormalizedWhenCondition, model.Triggers.Values.Single().NormalizedWhenCondition);
    }

    [TestMethod]
    public void TheStatementDeployedIsStillTheOneTheAuthorWrote()
    {
        const string sql = "CREATE TRIGGER t AFTER UPDATE OF b, a ON public.items FOR EACH ROW WHEN (new.a>0) EXECUTE FUNCTION public.f('x');";

        Assert.AreEqual(sql.TrimEnd(';'), Trigger(sql).RawStatement);
    }

    private static TriggerDefinition Trigger(string sql)
    {
        var parser = new PgQueryParser();
        var model = new SemanticPipeline().Execute([parser.Parse(Prefix), parser.Parse(sql)]);
        return model.Triggers.Values.Single();
    }
}
