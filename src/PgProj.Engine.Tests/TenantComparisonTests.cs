using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-290b: a tenant's changes are kept to its schema (moved from <c>TenantDiffOutputTests</c> with <c>TenantComparison</c>, G-316).
/// </summary>
[TestClass]
public sealed class TenantComparisonTests
{
    [TestMethod]
    public void ATenantsChangesAreKeptToItsSchema()
    {
        var changes = new ChangeSet
        {
            Added = [Added(Table("tenant_acme", "orders")), Added(Table("platform", "currencies")), Added(new SchemaDefinition("tenant_acme", "tenant_acme"))],
            Removed = [Removed(Table("tenant_acme", "old")), Removed(Table("public", "other"))],
            Modified = [],
        };

        var kept = TenantComparison.KeepSchema(changes, "tenant_acme");

        CollectionAssert.AreEqual(new[] { "tenant_acme.orders", "tenant_acme" }, kept.Added.Select(static change => change.NodeId).ToArray());
        CollectionAssert.AreEqual(new[] { "tenant_acme.old" }, kept.Removed.Select(static change => change.NodeId).ToArray());
    }

    private static ISemanticNode Table(string schema, string name)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse($"CREATE TABLE {schema}.{name} (id int);")]).Tables.Values.Single();

    private static AddedChange Added(ISemanticNode node) => new() { NodeId = node.Key, NodeKind = ChangeRecord.MapKind(node), Node = node };

    private static RemovedChange Removed(ISemanticNode node) => new() { NodeId = node.Key, NodeKind = ChangeRecord.MapKind(node), Node = node };
}
