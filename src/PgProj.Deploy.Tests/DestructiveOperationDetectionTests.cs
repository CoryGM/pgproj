using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// Regression coverage for G-35: <see cref="OperationBatch.GetDestructiveOperations"/> must identify every
/// operation that can discard data that existed before it ran, so callers (e.g. <c>publish --execute</c>) can
/// fail closed unless the caller opts in, and must not flag an operation that is only rebuilt from source
/// (an index, a constraint, a plain view, a type, a function/procedure/trigger) with nothing lost.
/// </summary>
[TestClass]
public sealed class DestructiveOperationDetectionTests
{
    [TestMethod]
    public void DroppedTableIsDestructive()
    {
        var table = CreateTable("public.orders", "orders", "public", []);
        var batch = new OperationBatch
        {
            Operations = [new DropOperation { NodeId = table.Key, NodeKind = ChangeNodeKind.Table, Node = table }],
        };

        var destructive = batch.GetDestructiveOperations();

        Assert.HasCount(1, destructive);
        Assert.AreEqual(table.Key, destructive[0].Operation.NodeId);
        Assert.Contains("table dropped", destructive[0].Reasons);
    }

    [TestMethod]
    public void DroppedMaterializedViewIsDestructive()
    {
        var view = CreateView("public.totals", "totals", "public", isMaterialized: true);
        var batch = new OperationBatch
        {
            Operations = [new DropOperation { NodeId = view.Key, NodeKind = ChangeNodeKind.View, Node = view }],
        };

        var destructive = batch.GetDestructiveOperations();

        Assert.HasCount(1, destructive);
        Assert.AreEqual(view.Key, destructive[0].Operation.NodeId);
    }

    [TestMethod]
    public void DroppedPlainViewIsNotDestructive()
    {
        var view = CreateView("public.totals", "totals", "public", isMaterialized: false);
        var batch = new OperationBatch
        {
            Operations = [new DropOperation { NodeId = view.Key, NodeKind = ChangeNodeKind.View, Node = view }],
        };

        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void DroppedExtensionIsDestructive()
    {
        var extension = CreateType("public.citext", "citext", "public", "extension");
        var batch = new OperationBatch
        {
            Operations = [new DropOperation { NodeId = extension.Key, NodeKind = ChangeNodeKind.Type, Node = extension }],
        };

        var destructive = batch.GetDestructiveOperations();

        Assert.HasCount(1, destructive);
        Assert.AreEqual(extension.Key, destructive[0].Operation.NodeId);
    }

    [TestMethod]
    public void DroppedNonExtensionTypeIsNotDestructive()
    {
        var enumType = CreateType("public.mood", "mood", "public", "user-defined");
        var batch = new OperationBatch
        {
            Operations = [new DropOperation { NodeId = enumType.Key, NodeKind = ChangeNodeKind.Type, Node = enumType }],
        };

        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void DroppedIndexAndConstraintAreNotDestructive()
    {
        var index = new IndexDefinition("public.orders_name_idx", "orders_name_idx", "public", "orders", "btree", [], [], [], [], [], [], [], []);
        var constraint = new ConstraintDefinition("public.orders_pkey", "orders_pkey", "public", "orders", "PRIMARY KEY", [], [], null, [], null, [], [], [], [], [], [], [], []);
        var batch = new OperationBatch
        {
            Operations =
            [
                new DropOperation { NodeId = index.Key, NodeKind = ChangeNodeKind.Index, Node = index },
                new DropOperation { NodeId = constraint.Key, NodeKind = ChangeNodeKind.Constraint, Node = constraint },
            ],
        };

        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void RevokedGrantWithNoReplacementIsDestructive()
    {
        var grant = CreateTableGrant("grant:table:public.orders:app_readonly:SELECT", "public", "orders", "app_readonly", ["SELECT"]);
        var batch = new OperationBatch
        {
            Operations = [new DropOperation { NodeId = grant.Key, NodeKind = ChangeNodeKind.Grant, Node = grant }],
        };

        var destructive = batch.GetDestructiveOperations();

        Assert.HasCount(1, destructive);
        Assert.AreEqual(grant.Key, destructive[0].Operation.NodeId);
        StringAssert.Contains(destructive[0].Reasons[0], "grant revoked");
        StringAssert.Contains(destructive[0].Reasons[0], "REVOKE SELECT ON TABLE \"public\".\"orders\" FROM \"app_readonly\"");
    }

    [TestMethod]
    public void RevokedRoleMembershipWithNoReplacementIsDestructive()
    {
        var membership = CreateRoleMembershipGrant("grant:role:admin:app_user", "admin", "app_user");
        var batch = new OperationBatch
        {
            Operations = [new DropOperation { NodeId = membership.Key, NodeKind = ChangeNodeKind.Grant, Node = membership }],
        };

        var destructive = batch.GetDestructiveOperations();

        Assert.HasCount(1, destructive);
        StringAssert.Contains(destructive[0].Reasons[0], "role membership revoked");
        StringAssert.Contains(destructive[0].Reasons[0], "REVOKE \"admin\" FROM \"app_user\"");
    }

    /// <summary>
    /// A modified grant is redeployed as REVOKE-then-GRANT for the same <see cref="GrantDefinition.Key"/>
    /// (<see cref="ScriptGenerator"/>'s <c>AddModifiedGrantOperations</c>): the access is narrowed or widened, not
    /// lost, so this must not be flagged even though a bare <see cref="DropOperation"/> for the same grant kind is
    /// destructive on its own (G-80).
    /// </summary>
    [TestMethod]
    public void ModifiedGrantRevokeThenGrantIsNotDestructive()
    {
        var oldGrant = CreateTableGrant("grant:table:public.orders:app_readonly", "public", "orders", "app_readonly", ["SELECT", "INSERT"]);
        var newGrant = CreateTableGrant("grant:table:public.orders:app_readonly", "public", "orders", "app_readonly", ["SELECT"]);
        var batch = new OperationBatch
        {
            Operations =
            [
                new DropOperation { NodeId = oldGrant.Key, NodeKind = ChangeNodeKind.Grant, Node = oldGrant },
                new CreateOperation { NodeId = newGrant.Key, NodeKind = ChangeNodeKind.Grant, Node = newGrant },
            ],
        };

        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void TableAlterWithRemovedColumnIsDestructive()
    {
        var oldTable = CreateTable("public.orders", "orders", "public",
        [
            CreateColumn("id", "orders", "integer"),
            CreateColumn("legacy", "orders", "text"),
        ]);
        var newTable = CreateTable("public.orders", "orders", "public", [CreateColumn("id", "orders", "integer")]);

        var destructive = ClassifyAlter(oldTable, newTable);

        Assert.HasCount(1, destructive);
        Assert.Contains("column \"legacy\" removed", destructive[0].Reasons);
    }

    [TestMethod]
    public void TableAlterWithChangedColumnTypeIsDestructive()
    {
        var oldTable = CreateTable("public.orders", "orders", "public", [CreateColumn("quantity", "orders", "text")]);
        var newTable = CreateTable("public.orders", "orders", "public", [CreateColumn("quantity", "orders", "integer")]);

        var destructive = ClassifyAlter(oldTable, newTable);

        Assert.HasCount(1, destructive);
        StringAssert.Contains(destructive[0].Reasons[0], "type changed");
    }

    [TestMethod]
    public void TableAlterWithRenamedColumnIsNotDestructive()
    {
        var oldTable = CreateTable("public.orders", "orders", "public", [CreateColumn("legacy_name", "orders", "text")]);
        var newTable = CreateTable("public.orders", "orders", "public",
            [CreateColumn("display_name", "orders", "text", renamedFrom: "legacy_name")]);

        Assert.IsEmpty(ClassifyAlter(oldTable, newTable));
    }

    [TestMethod]
    public void TableAlterWithAddedColumnIsNotDestructive()
    {
        var oldTable = CreateTable("public.orders", "orders", "public", [CreateColumn("id", "orders", "integer")]);
        var newTable = CreateTable("public.orders", "orders", "public",
        [
            CreateColumn("id", "orders", "integer"),
            CreateColumn("name", "orders", "text"),
        ]);

        Assert.IsEmpty(ClassifyAlter(oldTable, newTable));
    }

    [TestMethod]
    public void TableAlterWithNullabilityAndDefaultChangesOnlyIsNotDestructive()
    {
        var oldTable = CreateTable("public.orders", "orders", "public",
            [CreateColumn("status", "orders", "text", isNullable: true, rawDefaultExpression: null)]);
        var newTable = CreateTable("public.orders", "orders", "public",
            [CreateColumn("status", "orders", "text", isNullable: false, rawDefaultExpression: "'pending'")]);

        Assert.IsEmpty(ClassifyAlter(oldTable, newTable));
    }

    [TestMethod]
    public void SelfDiffTableAlterIsNotDestructive()
    {
        var table = CreateTable("public.orders", "orders", "public",
        [
            CreateColumn("id", "orders", "integer"),
            CreateColumn("name", "orders", "text"),
        ]);

        Assert.IsEmpty(ClassifyAlter(table, table));
    }

    private static IReadOnlyList<DestructiveOperation> ClassifyAlter(TableDefinition oldTable, TableDefinition newTable)
    {
        var batch = new OperationBatch
        {
            Operations =
            [
                new AlterOperation
                {
                    NodeId = newTable.Key,
                    NodeKind = ChangeNodeKind.Table,
                    Change = new ModifiedChange
                    {
                        NodeId = newTable.Key,
                        NodeKind = ChangeNodeKind.Table,
                        OldNode = oldTable,
                        NewNode = newTable,
                        Details = [],
                    },
                },
            ],
        };

        return batch.GetDestructiveOperations();
    }

    private static TableDefinition CreateTable(string key, string name, string? schemaName, IReadOnlyList<ColumnDefinition> columns)
        => new(key, name, schemaName, columns, [], [], [], [], [], [], [], [], []);

    private static ColumnDefinition CreateColumn(
        string name,
        string tableName,
        string dataType,
        bool? isNullable = null,
        string? rawDefaultExpression = null,
        string? renamedFrom = null)
        => new(
            $"public.{tableName}.{name}",
            name,
            "public",
            tableName,
            dataType,
            dataType,
            isNullable,
            rawDefaultExpression,
            false,
            null,
            null,
            null,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            null,
            false,
            renamedFrom);

    private static ViewDefinition CreateView(string key, string name, string? schemaName, bool isMaterialized)
        => new(key, name, schemaName, [], [], [], [], [], [], [], []) { IsMaterialized = isMaterialized };

    private static TypeDefinition CreateType(string key, string name, string? schemaName, string typeKind)
        => new(key, name, schemaName, typeKind, [], [], [], [], [], [], [], []);

    private static GrantDefinition CreateTableGrant(string key, string schemaName, string tableName, string grantee, IReadOnlyList<string> privileges)
        => new(key, tableName, schemaName, $"table:{tableName}", grantee, privileges, [], [], [], [], [], [], [], [])
        {
            ObjectName = tableName,
        };

    private static GrantDefinition CreateRoleMembershipGrant(string key, string role, string grantee)
        => new(key, role, null, $"role:{role}", grantee, ["MEMBER"], [], [], [], [], [], [], [], []);
}
