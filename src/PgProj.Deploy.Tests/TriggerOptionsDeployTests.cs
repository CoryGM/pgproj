using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-40: a change to a trigger's <c>WHEN</c> condition, <c>UPDATE OF</c> columns, arguments, or constraint-trigger options is now a
/// modification, so it is redeployed (the trigger is dropped and the statement the author wrote is replayed). Before, the diff saw
/// nothing and <c>publish</c> emitted no statement at all.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TriggerOptionsDeployTests
{
    private const string Prefix = """
        CREATE TABLE public.items (a integer, b integer);
        CREATE FUNCTION public.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        """;

    [TestMethod]
    public void AChangedWhenConditionIsDroppedAndTheNewStatementReplayed()
    {
        var statements = Statements(
            "CREATE TRIGGER trg AFTER UPDATE ON public.items FOR EACH ROW WHEN (new.a > 0) EXECUTE FUNCTION public.f();",
            "CREATE TRIGGER trg AFTER UPDATE ON public.items FOR EACH ROW WHEN (new.a > 5) EXECUTE FUNCTION public.f();");

        Assert.HasCount(2, statements, Dump(statements));
        Assert.AreEqual("DROP TRIGGER \"trg\" ON \"public\".\"items\";", statements[0]);
        StringAssert.Contains(statements[1], "WHEN (new.a > 5)");
    }

    [TestMethod]
    public void ANewUpdateOfListIsDeployed()
    {
        var statements = Statements(
            "CREATE TRIGGER trg AFTER UPDATE ON public.items FOR EACH ROW EXECUTE FUNCTION public.f();",
            "CREATE TRIGGER trg AFTER UPDATE OF a ON public.items FOR EACH ROW EXECUTE FUNCTION public.f();");

        Assert.HasCount(2, statements, Dump(statements));
        StringAssert.Contains(statements[1], "UPDATE OF a ON");
    }

    [TestMethod]
    public void AChangedArgumentIsDeployed()
    {
        var statements = Statements(
            "CREATE TRIGGER trg AFTER UPDATE ON public.items FOR EACH ROW EXECUTE FUNCTION public.f('x');",
            "CREATE TRIGGER trg AFTER UPDATE ON public.items FOR EACH ROW EXECUTE FUNCTION public.f('y');");

        Assert.HasCount(2, statements, Dump(statements));
        StringAssert.Contains(statements[1], "public.f('y')");
    }

    [TestMethod]
    public void ADeferrableConstraintTriggerIsDeployedWithItsOptions()
    {
        var statements = Statements(
            "CREATE CONSTRAINT TRIGGER trg AFTER UPDATE ON public.items DEFERRABLE INITIALLY IMMEDIATE FOR EACH ROW EXECUTE FUNCTION public.f();",
            "CREATE CONSTRAINT TRIGGER trg AFTER UPDATE ON public.items DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.f();");

        Assert.HasCount(2, statements, Dump(statements));
        StringAssert.Contains(statements[1], "INITIALLY DEFERRED");
    }

    [TestMethod]
    public void TheSameConditionAsPostgreSqlPrintsItDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            "CREATE TRIGGER trg AFTER UPDATE ON public.items FOR EACH ROW WHEN (((new.a > 0) AND (new.b < 10))) EXECUTE FUNCTION public.f();",
            "CREATE TRIGGER trg AFTER UPDATE ON public.items FOR EACH ROW WHEN (new.a>0 AND new.b<10) EXECUTE FUNCTION public.f();"));
    }

    private static string Dump(IEnumerable<string> statements) => string.Join(Environment.NewLine, statements);

    private static List<string> Statements(string oldTrigger, string newTrigger)
    {
        var oldModel = Build(oldTrigger);
        var newModel = Build(newTrigger);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToList();
    }

    private static DatabaseModel Build(string trigger)
    {
        var parser = new PgQueryParser();
        return new SemanticPipeline().Execute([parser.Parse(Prefix), parser.Parse(trigger)]);
    }
}
