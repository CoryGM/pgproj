using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// Regression coverage for G-36: PostgreSQL scopes trigger names to their table, so the same
/// trigger name on different tables in one schema must produce distinct model identities.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TriggerIdentitySemanticsTests
{
    private const string Prelude = """
        create table public.orders (id integer);
        create table public.customers (id integer);
        create function public.audit() returns trigger language plpgsql as $$begin return new; end;$$;
        """;

    [TestMethod]
    public void SameTriggerNameOnDifferentTablesGetsDistinctKeys()
    {
        var model = BuildModel(Prelude + """
            create trigger audit_trg after insert on public.orders for each row execute function public.audit();
            create trigger audit_trg after insert on public.customers for each row execute function public.audit();
            """);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(2, model.Triggers.Count);
        Assert.IsTrue(model.Triggers.ContainsKey("public.orders.audit_trg"));
        Assert.IsTrue(model.Triggers.ContainsKey("public.customers.audit_trg"));
    }

    [TestMethod]
    public void SameTriggerNameTwiceOnSameTableIsStillADuplicate()
    {
        var model = BuildModel(Prelude + """
            create trigger audit_trg after insert on public.orders for each row execute function public.audit();
            create trigger audit_trg after update on public.orders for each row execute function public.audit();
            """);

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("Duplicate Trigger definition", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void TriggerKeyIsCaseCanonicalized()
    {
        var model = BuildModel(Prelude + "create trigger Audit_Trg after insert on Public.Orders for each row execute function public.audit();");

        Assert.IsTrue(model.Triggers.ContainsKey("public.orders.audit_trg"));
    }

    [TestMethod]
    public void TriggerKeyDegradesToSchemaQualifiedNameWithoutTable()
    {
        Assert.AreEqual("public.audit_trg", DatabaseModel.CreateTriggerKey("public", null, "audit_trg"));
        Assert.AreEqual("audit_trg", DatabaseModel.CreateTriggerKey(null, null, "audit_trg"));
        Assert.AreEqual("public.orders.audit_trg", DatabaseModel.CreateTriggerKey("public", "orders", "audit_trg"));
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
