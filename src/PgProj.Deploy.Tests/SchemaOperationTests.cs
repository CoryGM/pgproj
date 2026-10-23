using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class SchemaOperationTests
{
    [TestMethod]
    public void GenerateEmitsCreateSchemaForNonPublicSchemaOfAddedTable()
    {
        var table = CreateTable("sales", "orders");

        var changes = new ChangeSet
        {
            Added = [new AddedChange { NodeId = table.Key, NodeKind = ChangeNodeKind.Table, Node = table }],
            Removed = [],
            Modified = [],
        };

        var batch = new ScriptGenerator().Generate(changes, new DependencyGraph());

        Assert.AreEqual(2, batch.Operations.Count);
        Assert.IsInstanceOfType<SchemaCreateOperation>(batch.Operations[0]);
        StringAssert.Contains(batch.Operations[0].GenerateSql(), "CREATE SCHEMA IF NOT EXISTS \"sales\";");
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[1]);
    }

    [TestMethod]
    public void GenerateDoesNotEmitCreateSchemaForPublicSchema()
    {
        var table = CreateTable("public", "orders");

        var changes = new ChangeSet
        {
            Added = [new AddedChange { NodeId = table.Key, NodeKind = ChangeNodeKind.Table, Node = table }],
            Removed = [],
            Modified = [],
        };

        var batch = new ScriptGenerator().Generate(changes, new DependencyGraph());

        Assert.AreEqual(1, batch.Operations.Count);
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[0]);
    }

    [TestMethod]
    public void GenerateEmitsDistinctSortedCreateSchemaStatementsForMultipleAddedObjects()
    {
        var ordersTable = CreateTable("sales", "orders");
        var customersTable = CreateTable("sales", "customers");
        var eventsTable = CreateTable("analytics", "events");

        var changes = new ChangeSet
        {
            Added =
            [
                new AddedChange { NodeId = ordersTable.Key, NodeKind = ChangeNodeKind.Table, Node = ordersTable },
                new AddedChange { NodeId = customersTable.Key, NodeKind = ChangeNodeKind.Table, Node = customersTable },
                new AddedChange { NodeId = eventsTable.Key, NodeKind = ChangeNodeKind.Table, Node = eventsTable },
            ],
            Removed = [],
            Modified = [],
        };

        var batch = new ScriptGenerator().Generate(changes, new DependencyGraph());

        var schemaOperations = batch.Operations.OfType<SchemaCreateOperation>().ToArray();
        Assert.AreEqual(2, schemaOperations.Length);
        Assert.AreEqual("analytics", schemaOperations[0].SchemaName);
        Assert.AreEqual("sales", schemaOperations[1].SchemaName);
    }

    private static TableDefinition CreateTable(string schemaName, string tableName)
        => new(
            $"{schemaName}.{tableName}",
            tableName,
            schemaName,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            []);
}
