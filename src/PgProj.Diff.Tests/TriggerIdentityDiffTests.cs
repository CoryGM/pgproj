using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// Regression coverage for G-36: triggers are identified by schema, table, and name, so
/// same-named triggers on different tables diff independently and without phantom changes.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TriggerIdentityDiffTests
{
    private const string Prelude = """
        create table public.orders (id integer);
        create table public.customers (id integer);
        create function public.audit() returns trigger language plpgsql as $$begin return new; end;$$;
        """;

    private const string OrdersTrigger = "create trigger audit_trg after insert on public.orders for each row execute function public.audit();";

    private const string CustomersTrigger = "create trigger audit_trg after insert on public.customers for each row execute function public.audit();";

    [TestMethod]
    public void SelfDiffOfSameNamedTriggersIsEmpty()
    {
        var model = BuildModel(Prelude + OrdersTrigger + CustomersTrigger);

        var result = new DiffEngine().Execute(model, model);

        AssertNoTriggerChanges(result);
    }

    [TestMethod]
    public void CosmeticDifferencesAndDeclarationOrderProduceNoTriggerChanges()
    {
        var oldModel = BuildModel(Prelude + OrdersTrigger + CustomersTrigger);
        var newModel = BuildModel(Prelude
            + "CREATE   TRIGGER   Audit_Trg   AFTER INSERT ON Public.Customers FOR EACH ROW EXECUTE FUNCTION public.audit();\n"
            + "CREATE TRIGGER AUDIT_TRG AFTER INSERT ON PUBLIC.ORDERS FOR EACH ROW EXECUTE FUNCTION public.audit();");

        var result = new DiffEngine().Execute(oldModel, newModel);

        AssertNoTriggerChanges(result);
    }

    [TestMethod]
    public void ChangingOneOfTwoSameNamedTriggersReportsOnlyThatTrigger()
    {
        var oldModel = BuildModel(Prelude + OrdersTrigger + CustomersTrigger);
        var newModel = BuildModel(Prelude + OrdersTrigger
            + "create trigger audit_trg before insert on public.customers for each row execute function public.audit();");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.IsEmpty(result.AddedTriggers);
        Assert.IsEmpty(result.RemovedTriggers);
        Assert.HasCount(1, result.ModifiedTriggers);
        Assert.AreEqual("public.customers.audit_trg", result.ModifiedTriggers[0].NodeId);
    }

    [TestMethod]
    public void DroppingOneOfTwoSameNamedTriggersReportsOnlyThatTrigger()
    {
        var oldModel = BuildModel(Prelude + OrdersTrigger + CustomersTrigger);
        var newModel = BuildModel(Prelude + OrdersTrigger);

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.IsEmpty(result.AddedTriggers);
        Assert.IsEmpty(result.ModifiedTriggers);
        Assert.HasCount(1, result.RemovedTriggers);
        Assert.AreEqual("public.customers.audit_trg", result.RemovedTriggers[0].Key);
    }

    private static void AssertNoTriggerChanges(DiffResult result)
    {
        Assert.IsEmpty(result.AddedTriggers);
        Assert.IsEmpty(result.RemovedTriggers);
        Assert.IsEmpty(result.ModifiedTriggers);
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
