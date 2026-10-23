using PgProj.Diff;
using PgProj.Engine;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-290b: a tenant's changes are kept to its schema, shown in the template's stand-in schema, and tenants with the same changes are grouped; the totals add
/// up every tenant; <c>--tenants</c> takes identifiers exactly as written.
/// </summary>
[TestClass]
public sealed class TenantDiffOutputTests
{
    [TestMethod]
    [DataRow("tenant_acme.orders", "tenant.orders")]
    [DataRow("'tenant_acme.customers(id)'", "'tenant.customers(id)'")]
    [DataRow("\"tenant_acme\".\"Orders\"", "tenant.\"Orders\"")]
    [DataRow("tenant_acme", "tenant")]
    [DataRow("tenant_acme_two.orders", "tenant_acme_two.orders")]
    [DataRow("my_tenant_acme.orders", "my_tenant_acme.orders")]
    public void ATenantsSchemaIsShownAsTheStandIn(string text, string expected)
        => Assert.AreEqual(expected, TenantsComparison.StandInDisplay("tenant_acme", "tenant")(text));


    [TestMethod]
    public void TenantsWithTheSameChangesAreOneGroupAndTheTotalsCountEachTenant()
    {
        var result = Result(
            ("acme", true, [Table("tenant_acme", "audit")]),
            ("globex", true, [Table("tenant_globex", "audit")]),
            ("initech", true, [Table("tenant_initech", "audit"), Table("tenant_initech", "extra")]),
            ("umbrella", true, []),
            ("newco", false, [Table("tenant_newco", "orders"), Table("tenant_newco", "customers")]));

        var groups = result.Tenants!.Groups;
        using var writer = new StringWriter();
        TenantDiffOutput.WriteText(result, writer);
        var text = writer.ToString();

        Assert.AreEqual(2, groups.Count);
        CollectionAssert.AreEqual(new[] { "tenant_acme", "tenant_globex" }, groups[0].Members.Select(static member => member.Tenant.Schema).ToArray());
        StringAssert.Contains(text, "2 tenant(s): tenant_acme, tenant_globex" + Environment.NewLine + "Added (1):" + Environment.NewLine + "  + Table tenant.audit");
        StringAssert.Contains(text, "1 tenant(s), changed differently from the tenants above: tenant_initech");
        StringAssert.Contains(text, "1 new tenant(s), with no schema yet; publish creates each with the template's 2 object(s): tenant_newco");
        StringAssert.Contains(text, "1 tenant(s) up to date: tenant_umbrella");
        StringAssert.EndsWith(text, "Added: 6" + Environment.NewLine + "Removed: 0" + Environment.NewLine + "Modified: 0" + Environment.NewLine);
        Assert.AreEqual((6, 0, 0), result.Tenants.Totals);
    }

    [TestMethod]
    public void ALongListOfTenantsIsShortenedInText()
    {
        var tenants = Enumerable.Range(1, 12).Select(static index => ($"t{index:00}", true, (IReadOnlyList<ISemanticNode>)[])).ToArray();
        using var writer = new StringWriter();

        TenantDiffOutput.WriteText(Result(tenants), writer);

        StringAssert.Contains(writer.ToString(), "12 tenant(s) up to date: tenant_t01, tenant_t02, tenant_t03, tenant_t04, tenant_t05, tenant_t06, tenant_t07, tenant_t08, tenant_t09, tenant_t10, and 2 more (--format json lists them all)");
    }

    [TestMethod]
    public void TheFormerTenantWarningSaysTheSchemaIsLeftAndHowToDropIt()
    {
        StringAssert.Contains(TenantDiffOutput.DescribeFormerTenants(["tenant_old"], forPublish: false), "tenant_old is no longer a tenant");
        StringAssert.Contains(TenantDiffOutput.DescribeFormerTenants(["tenant_old"], forPublish: false), "drop it by other means (for example DROP SCHEMA tenant_old CASCADE)");
        StringAssert.Contains(TenantDiffOutput.DescribeFormerTenants(["tenant_a", "tenant_b"], forPublish: true), "tenant_a, tenant_b are no longer tenants");
        StringAssert.Contains(TenantDiffOutput.DescribeFormerTenants(["tenant_a", "tenant_b"], forPublish: true), "pgproj left these schemas");
    }

    [TestMethod]
    public void TenantIdentifiersAreSplitTrimmedAndKeptAsWritten()
        => CollectionAssert.AreEqual(new[] { "acme", "Globex", "initech" }, DiffCommand.SplitTenantIdentifiers(["acme, Globex", "initech,acme", " "]).ToArray());

    private static CompareResult Result(params (string Identifier, bool Exists, IReadOnlyList<ISemanticNode> Added)[] tenants)
    {
        var empty = new ChangeSet { Added = [], Removed = [], Modified = [] };
        var targets = tenants.Select(static tenant => new TenantTarget(tenant.Identifier, "tenant_" + tenant.Identifier, tenant.Exists)).ToArray();
        var comparison = new TenantsComparison(
            "tenant",
            new TenantList(targets, [], null),
            [.. tenants.Select((tenant, index) => new TenantDifference(targets[index], empty with { Added = [.. tenant.Added.Select(Added)] }))]);
        var (added, removed, modified) = comparison.Totals;
        return new CompareResult { Status = CompareStatus.Succeeded, Changes = empty, Tenants = comparison, AddedCount = added, RemovedCount = removed, ModifiedCount = modified };
    }

    private static ISemanticNode Table(string schema, string name)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse($"CREATE TABLE {schema}.{name} (id int);")]).Tables.Values.Single();

    private static AddedChange Added(ISemanticNode node) => new() { NodeId = node.Key, NodeKind = ChangeRecord.MapKind(node), Node = node };
}
