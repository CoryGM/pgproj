using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-37: the live loader must read triggers (which PostgreSQL reports via
/// <c>pg_get_triggerdef</c>) and produce the same model identity and function reference the
/// project-side parser produces, independent of the connection's <c>search_path</c>.
/// </summary>
[TestClass]
public sealed class TriggerLiveLoaderTests
{
    [TestMethod]
    public void UnqualifiedFunctionInTriggerDefinitionIsRewrittenToSchemaQualifiedName()
    {
        var trigger = new LiveTriggerSnapshot(
            "public",
            "orders",
            "audit_trg",
            "CREATE TRIGGER audit_trg AFTER INSERT ON public.orders FOR EACH ROW EXECUTE FUNCTION audit()",
            "public",
            "audit");

        var sql = LiveDatabaseModelLoader.BuildCreateTriggerStatement(trigger);

        Assert.AreEqual(
            "CREATE TRIGGER audit_trg AFTER INSERT ON public.orders FOR EACH ROW EXECUTE FUNCTION public.audit();",
            sql);
    }

    [TestMethod]
    public void AlreadyQualifiedFunctionIsNormalizedToTheSameForm()
    {
        var trigger = new LiveTriggerSnapshot(
            "accounts",
            "account",
            "create_audit_trg",
            "CREATE TRIGGER create_audit_trg AFTER INSERT ON accounts.account FOR EACH ROW EXECUTE FUNCTION audit.generic_create_audit()",
            "audit",
            "generic_create_audit");

        var sql = LiveDatabaseModelLoader.BuildCreateTriggerStatement(trigger);

        StringAssert.EndsWith(sql, "EXECUTE FUNCTION audit.generic_create_audit();");
    }

    [TestMethod]
    public void QuotedFunctionAndArgumentsArePreserved()
    {
        var trigger = new LiveTriggerSnapshot(
            "public",
            "orders",
            "audit_trg",
            "CREATE TRIGGER audit_trg BEFORE UPDATE ON public.orders FOR EACH ROW EXECUTE FUNCTION \"Audit Fn\"('a', 'b')",
            "Odd Schema",
            "Audit Fn");

        var sql = LiveDatabaseModelLoader.BuildCreateTriggerStatement(trigger);

        StringAssert.EndsWith(sql, "EXECUTE FUNCTION \"Odd Schema\".\"Audit Fn\"('a', 'b');");
    }

    [TestMethod]
    public void WhenClauseIsPreserved()
    {
        var trigger = new LiveTriggerSnapshot(
            "public",
            "orders",
            "audit_trg",
            "CREATE TRIGGER audit_trg AFTER UPDATE ON public.orders FOR EACH ROW WHEN (old.id IS DISTINCT FROM new.id) EXECUTE FUNCTION audit()",
            "public",
            "audit");

        var sql = LiveDatabaseModelLoader.BuildCreateTriggerStatement(trigger);

        StringAssert.Contains(sql, "WHEN (old.id IS DISTINCT FROM new.id)");
        StringAssert.EndsWith(sql, "EXECUTE FUNCTION public.audit();");
    }

    [TestMethod]
    public void SnapshotWithTriggersProducesTableScopedTriggerModel()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")],
            [
                new LiveTableSnapshot("public", "orders", [new LiveColumnSnapshot("id", "bigint", false, null, false, null, null)]),
                new LiveTableSnapshot("public", "customers", [new LiveColumnSnapshot("id", "bigint", false, null, false, null, null)]),
            ],
            [],
            [],
            functions: [new LiveFunctionSnapshot("public", "audit", "CREATE OR REPLACE FUNCTION public.audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$")],
            triggers:
            [
                new LiveTriggerSnapshot("public", "orders", "audit_trg", "CREATE TRIGGER audit_trg AFTER INSERT ON public.orders FOR EACH ROW EXECUTE FUNCTION audit()", "public", "audit"),
                new LiveTriggerSnapshot("public", "customers", "audit_trg", "CREATE TRIGGER audit_trg AFTER INSERT OR UPDATE ON public.customers FOR EACH ROW EXECUTE FUNCTION audit()", "public", "audit"),
            ]);

        var model = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(snapshotSql);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.HasCount(2, model.Triggers);

        var orders = model.Triggers["public.orders.audit_trg"];
        Assert.AreEqual("public.audit", orders.FunctionName);
        Assert.AreEqual("after", orders.Timing);
        Assert.IsTrue(orders.IsRowLevel);
        CollectionAssert.AreEqual(new[] { "INSERT" }, orders.Events.ToArray());

        var customers = model.Triggers["public.customers.audit_trg"];
        CollectionAssert.AreEqual(new[] { "INSERT", "UPDATE" }, customers.Events.ToArray());
    }

    [TestMethod]
    public void SnapshotWithoutTriggersIsUnchanged()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [], [], []);

        Assert.DoesNotContain("TRIGGER", snapshotSql, StringComparison.OrdinalIgnoreCase);
    }
}
