using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-76: row-level security policies diff as their own object kind, scoped to their table like a trigger (G-36) or
/// constraint (G-38); a table's own <c>ENABLE</c>/<c>FORCE ROW LEVEL SECURITY</c> flags diff as ordinary table properties.
/// Phantom-diff regressions per CLAUDE.md's Definition of Done: self-diff is empty, cosmetic-only differences (role case/order,
/// re-parenthesized expressions) report nothing, and a meaningful change is still reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PolicyDiffTests
{
    private const string Table = "CREATE TABLE public.items (id integer, owner_id integer, status text);\n";

    [TestMethod]
    public void SelfDiffOfAPolicyIsEmpty()
    {
        var model = BuildModel(Table + "CREATE POLICY p1 ON public.items AS RESTRICTIVE FOR SELECT TO app_user "
            + "USING (owner_id = 1) WITH CHECK (status = 'active');");

        AssertNoPolicyChanges(new DiffEngine().Execute(model, model));
    }

    [TestMethod]
    public void CosmeticOnlyDifferencesReportNothing()
    {
        var oldModel = BuildModel(Table + "CREATE POLICY p ON public.items TO app_user, other_role USING (owner_id=1 AND status='active');");
        var newModel = BuildModel(Table + "CREATE POLICY p ON public.items TO Other_Role, App_User USING (((owner_id = 1) AND (status = 'active')));");

        AssertNoPolicyChanges(new DiffEngine().Execute(oldModel, newModel));
    }

    [TestMethod]
    public void AMeaningfulUsingExpressionChangeIsReported()
    {
        var oldModel = BuildModel(Table + "CREATE POLICY p ON public.items USING (owner_id = 1);");
        var newModel = BuildModel(Table + "CREATE POLICY p ON public.items USING (owner_id = 2);");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.IsEmpty(result.AddedPolicies);
        Assert.IsEmpty(result.RemovedPolicies);
        Assert.HasCount(1, result.ModifiedPolicies);
        Assert.AreEqual("public.items.p", result.ModifiedPolicies[0].NodeId);
    }

    [TestMethod]
    public void ARoleAddedIsReported()
    {
        var oldModel = BuildModel(Table + "CREATE POLICY p ON public.items TO app_user USING (true);");
        var newModel = BuildModel(Table + "CREATE POLICY p ON public.items TO app_user, other_role USING (true);");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedPolicies);
    }

    [TestMethod]
    public void ACommandChangeIsReported()
    {
        var oldModel = BuildModel(Table + "CREATE POLICY p ON public.items FOR SELECT USING (true);");
        var newModel = BuildModel(Table + "CREATE POLICY p ON public.items FOR ALL USING (true);");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedPolicies);
    }

    [TestMethod]
    public void APolicyAddedOrRemovedIsReportedNotHidden()
    {
        var withPolicy = BuildModel(Table + "CREATE POLICY p ON public.items USING (true);");
        var withoutPolicy = BuildModel(Table);

        var added = new DiffEngine().Execute(withoutPolicy, withPolicy);
        Assert.HasCount(1, added.AddedPolicies);
        Assert.IsEmpty(added.RemovedPolicies);

        var removed = new DiffEngine().Execute(withPolicy, withoutPolicy);
        Assert.HasCount(1, removed.RemovedPolicies);
        Assert.IsEmpty(removed.AddedPolicies);
    }

    [TestMethod]
    public void TheSamePolicyNameOnTwoDifferentTablesDiffsIndependently()
    {
        const string twoTables = """
            create table public.a (id integer);
            create table public.b (id integer);
            create policy p on public.a using (true);
            create policy p on public.b using (true);
            """;
        var oldModel = BuildModel(twoTables);
        var newModel = BuildModel("""
            create table public.a (id integer);
            create table public.b (id integer);
            create policy p on public.a using (true);
            create policy p on public.b using (false);
            """);

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedPolicies);
        Assert.AreEqual("public.b.p", result.ModifiedPolicies[0].NodeId);
    }

    // --- A table's own row-security flags (ENABLE/FORCE) diff as ordinary table properties, not as a policy. ---

    [TestMethod]
    public void SelfDiffOfARowSecurityEnabledTableIsEmpty()
    {
        var model = BuildModel(Table + "ALTER TABLE public.items ENABLE ROW LEVEL SECURITY;");

        var result = new DiffEngine().Execute(model, model);

        Assert.IsEmpty(result.ModifiedTables);
    }

    [TestMethod]
    public void EnablingRowSecurityIsReportedAsATableModification()
    {
        var oldModel = BuildModel(Table);
        var newModel = BuildModel(Table + "ALTER TABLE public.items ENABLE ROW LEVEL SECURITY;");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedTables);
        Assert.IsTrue(result.ModifiedTables[0].Details.Any(static detail => detail.PropertyName == nameof(TableDefinition.IsRowSecurityEnabled)));
    }

    [TestMethod]
    public void ForcingRowSecurityIsReportedAsATableModification()
    {
        var oldModel = BuildModel(Table + "ALTER TABLE public.items ENABLE ROW LEVEL SECURITY;");
        var newModel = BuildModel(Table + "ALTER TABLE public.items ENABLE ROW LEVEL SECURITY; ALTER TABLE public.items FORCE ROW LEVEL SECURITY;");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedTables);
        Assert.IsTrue(result.ModifiedTables[0].Details.Any(static detail => detail.PropertyName == nameof(TableDefinition.IsRowSecurityForced)));
    }

    private static void AssertNoPolicyChanges(DiffResult result)
    {
        Assert.IsEmpty(result.AddedPolicies);
        Assert.IsEmpty(result.RemovedPolicies);
        Assert.IsEmpty(result.ModifiedPolicies);
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
