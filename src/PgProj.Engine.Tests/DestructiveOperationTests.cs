using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// Regression coverage for G-35: <see cref="ScriptComposition.GetDestructiveOperations"/> must surface operations that
/// can discard data so <c>publish --execute</c> can fail closed unless <c>--allow-destructive</c> is passed. See
/// <c>PgProj.Integration.Tests.DestructiveOperationEndToEndTests</c> for the real CLI/--allow-destructive behavior.
/// Moved from the CLI's <c>DestructiveOperationCliTests</c> with the forward they called (G-320).
/// </summary>
[TestClass]
public sealed class DestructiveOperationTests
{
    [TestMethod]
    public void GetDestructiveOperationsReturnsEmptyForNonDestructiveBatch()
    {
        var table = new TableDefinition("public.orders", "orders", "public", [], [], [], [], [], [], [], [], [], []);
        var batch = new OperationBatch
        {
            Operations =
            [
                new CreateOperation { NodeId = table.Key, NodeKind = ChangeNodeKind.Table, Node = table },
            ],
        };

        Assert.IsEmpty(ScriptComposition.GetDestructiveOperations(batch));
    }

    [TestMethod]
    public void GetDestructiveOperationsReturnsDroppedTables()
    {
        var table = new TableDefinition("public.orders", "orders", "public", [], [], [], [], [], [], [], [], [], []);
        var batch = new OperationBatch
        {
            Operations =
            [
                new DropOperation { NodeId = table.Key, NodeKind = ChangeNodeKind.Table, Node = table },
            ],
        };

        var destructive = ScriptComposition.GetDestructiveOperations(batch);

        Assert.HasCount(1, destructive);
        Assert.AreEqual(table.Key, destructive[0].Operation.NodeId);
    }
}
