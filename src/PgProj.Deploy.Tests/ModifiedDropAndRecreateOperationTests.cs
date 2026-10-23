using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class ModifiedDropAndRecreateOperationTests
{
    [TestMethod]
    public void ModifiedIndexEmitsDropThenCreate()
    {
        var oldIndex = CreateIndex(isUnique: false);
        var newIndex = CreateIndex(isUnique: true);

        var batch = GenerateBatch(ChangeNodeKind.Index, oldIndex, newIndex);

        Assert.AreEqual(2, batch.Operations.Count);
        Assert.IsInstanceOfType<DropOperation>(batch.Operations[0]);
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[1]);
        StringAssert.Contains(batch.Operations[0].GenerateSql(), "DROP INDEX");
        StringAssert.Contains(batch.Operations[1].GenerateSql(), "CREATE UNIQUE INDEX");
    }

    [TestMethod]
    public void ModifiedConstraintEmitsDropThenCreate()
    {
        var oldConstraint = CreateUniqueConstraint(["email"]);
        var newConstraint = CreateUniqueConstraint(["email", "tenant_id"]);

        var batch = GenerateBatch(ChangeNodeKind.Constraint, oldConstraint, newConstraint);

        Assert.AreEqual(2, batch.Operations.Count);
        Assert.IsInstanceOfType<DropOperation>(batch.Operations[0]);
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[1]);
        StringAssert.Contains(batch.Operations[0].GenerateSql(), "DROP CONSTRAINT");
        StringAssert.Contains(batch.Operations[1].GenerateSql(), "ADD CONSTRAINT");
    }

    [TestMethod]
    public void ModifiedTypeEmitsDropThenCreate()
    {
        var oldType = CreateCompositeType("integer");
        var newType = CreateCompositeType("bigint");

        var batch = GenerateBatch(ChangeNodeKind.Type, oldType, newType);

        Assert.AreEqual(2, batch.Operations.Count);
        Assert.IsInstanceOfType<DropOperation>(batch.Operations[0]);
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[1]);
        StringAssert.Contains(batch.Operations[0].GenerateSql(), "DROP TYPE");
        StringAssert.Contains(batch.Operations[1].GenerateSql(), "bigint");
    }

    [TestMethod]
    public void ModifiedGrantEmitsRevokeThenGrant()
    {
        var oldGrant = CreateGrant(["SELECT"]);
        var newGrant = CreateGrant(["SELECT", "INSERT"]);

        var batch = GenerateBatch(ChangeNodeKind.Grant, oldGrant, newGrant);

        Assert.AreEqual(2, batch.Operations.Count);
        Assert.IsInstanceOfType<DropOperation>(batch.Operations[0]);
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[1]);
        StringAssert.Contains(batch.Operations[0].GenerateSql(), "REVOKE");
        StringAssert.Contains(batch.Operations[1].GenerateSql(), "GRANT SELECT, INSERT");
    }

    [TestMethod]
    public void ModifiedEnumTypeWithAppendedLabelsEmitsAlterTypeAddValue()
    {
        var oldType = CreateEnumType(["sad", "ok"]);
        var newType = CreateEnumType(["sad", "ok", "happy"]);

        var batch = GenerateBatch(ChangeNodeKind.Type, oldType, newType);

        Assert.AreEqual(1, batch.Operations.Count);
        Assert.IsInstanceOfType<AlterOperation>(batch.Operations[0]);
        Assert.AreEqual(
            "ALTER TYPE \"public\".\"mood\" ADD VALUE 'happy';",
            batch.Operations[0].GenerateSql());
    }

    [TestMethod]
    public void ModifiedEnumTypeWithRemovedLabelEmitsDropThenCreate()
    {
        var oldType = CreateEnumType(["sad", "ok", "happy"]);
        var newType = CreateEnumType(["sad", "happy"]);

        var batch = GenerateBatch(ChangeNodeKind.Type, oldType, newType);

        Assert.AreEqual(2, batch.Operations.Count);
        Assert.IsInstanceOfType<DropOperation>(batch.Operations[0]);
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[1]);
        StringAssert.Contains(batch.Operations[0].GenerateSql(), "DROP TYPE");
        StringAssert.Contains(batch.Operations[1].GenerateSql(), "CREATE TYPE");
    }

    [TestMethod]
    public void ModifiedEnumTypeWithReorderedLabelsEmitsDropThenCreate()
    {
        var oldType = CreateEnumType(["sad", "ok", "happy"]);
        var newType = CreateEnumType(["sad", "happy", "ok"]);

        var batch = GenerateBatch(ChangeNodeKind.Type, oldType, newType);

        Assert.AreEqual(2, batch.Operations.Count);
        Assert.IsInstanceOfType<DropOperation>(batch.Operations[0]);
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[1]);
    }

    [TestMethod]
    [DataRow(new[] { "sad", "happy" }, new[] { "sad", "ok", "happy" }, "ALTER TYPE \"public\".\"mood\" ADD VALUE 'ok' AFTER 'sad';")]
    [DataRow(new[] { "sad", "happy" }, new[] { "ok", "sad", "happy" }, "ALTER TYPE \"public\".\"mood\" ADD VALUE 'ok' BEFORE 'sad';")]
    [DataRow(new[] { "sad", "happy" }, new[] { "sad", "ok", "fine", "happy" }, "ALTER TYPE \"public\".\"mood\" ADD VALUE 'ok' AFTER 'sad';\nALTER TYPE \"public\".\"mood\" ADD VALUE 'fine' AFTER 'ok';")]
    [DataRow(new[] { "sad", "happy" }, new[] { "ok", "fine", "sad", "happy" }, "ALTER TYPE \"public\".\"mood\" ADD VALUE 'ok' BEFORE 'sad';\nALTER TYPE \"public\".\"mood\" ADD VALUE 'fine' AFTER 'ok';")]
    [DataRow(new[] { "sad", "happy" }, new[] { "sad", "ok", "happy", "glad" }, "ALTER TYPE \"public\".\"mood\" ADD VALUE 'ok' AFTER 'sad';\nALTER TYPE \"public\".\"mood\" ADD VALUE 'glad';")]
    [DataRow(new[] { "sad" }, new[] { "sad", "it's" }, "ALTER TYPE \"public\".\"mood\" ADD VALUE 'it''s';")]
    public void ModifiedEnumTypeWithInsertedLabelsEmitsPositionedAlterTypeAddValue(string[] oldLabels, string[] newLabels, string expected)
    {
        var batch = GenerateBatch(ChangeNodeKind.Type, CreateEnumType(oldLabels), CreateEnumType(newLabels));

        Assert.AreEqual(1, batch.Operations.Count);
        Assert.IsInstanceOfType<AlterOperation>(batch.Operations[0]);
        Assert.AreEqual(expected, batch.Operations[0].GenerateSql());
    }

    [TestMethod]
    [DataRow(new[] { "sad", "happy" }, new[] { "ok", "sad" })]
    [DataRow(new[] { "sad", "happy" }, new[] { "happy", "ok", "sad" })]
    [DataRow(new[] { "sad", "happy" }, new[] { "sad", "ok", "glad" })]
    public void AnInsertionTogetherWithARemovalOrReorderIsStillDropThenCreate(string[] oldLabels, string[] newLabels)
    {
        var batch = GenerateBatch(ChangeNodeKind.Type, CreateEnumType(oldLabels), CreateEnumType(newLabels));

        Assert.AreEqual(2, batch.Operations.Count);
        Assert.IsInstanceOfType<DropOperation>(batch.Operations[0]);
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[1]);
    }

    private static OperationBatch GenerateBatch(ChangeNodeKind nodeKind, ISemanticNode oldNode, ISemanticNode newNode)
    {
        var changes = new ChangeSet
        {
            Added = [],
            Removed = [],
            Modified =
            [
                new ModifiedChange
                {
                    NodeId = newNode.Key,
                    NodeKind = nodeKind,
                    OldNode = oldNode,
                    NewNode = newNode,
                    Details = [],
                },
            ],
        };

        return new ScriptGenerator().Generate(changes, new DependencyGraph());
    }

    private static IndexDefinition CreateIndex(bool isUnique)
        => new(
            "public.ix_orders_email",
            "ix_orders_email",
            "public",
            "orders",
            null,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [])
        {
            KeyColumns = ["email"],
            IsUnique = isUnique,
        };

    private static ConstraintDefinition CreateUniqueConstraint(IReadOnlyList<string> uniqueColumns)
        => new(
            "public.orders_email_key",
            "orders_email_key",
            "public",
            "orders",
            "UNIQUE",
            [],
            uniqueColumns,
            null,
            [],
            null,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            []);

    private static TypeDefinition CreateCompositeType(string idFieldType)
        => new(
            "public.money_amount",
            "money_amount",
            "public",
            "composite",
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [])
        {
            CompositeFields =
            [
                new CompositeTypeFieldDefinition("id", idFieldType, idFieldType),
            ],
        };

    private static TypeDefinition CreateEnumType(IReadOnlyList<string> labels)
        => new(
            "public.mood",
            "mood",
            "public",
            "user-defined",
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [])
        {
            EnumLabels = labels,
        };

    private static GrantDefinition CreateGrant(IReadOnlyList<string> privileges)
        => new(
            "grant:schema:public:app_user",
            "app_user",
            "public",
            "schema:public",
            "app_user",
            privileges,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            []);
}
