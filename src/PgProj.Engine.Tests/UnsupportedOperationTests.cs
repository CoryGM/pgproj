using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// Regression coverage for G-28: <see cref="ScriptComposition.GetUnsupportedOperations"/> must surface
/// TODO/unsupported-placeholder operations so <c>publish --execute</c> can fail closed instead of
/// silently running a script that skips the affected schema changes.
/// Moved from the CLI's <c>UnsupportedOperationCliTests</c> with the forward they called (G-320).
/// </summary>
[TestClass]
public sealed class UnsupportedOperationTests
{
    [TestMethod]
    public void GetUnsupportedOperationsReturnsEmptyForFullySupportedBatch()
    {
        var table = new TableDefinition("public.orders", "orders", "public", [], [], [], [], [], [], [], [], [], []);
        var batch = new OperationBatch
        {
            Operations =
            [
                new CreateOperation { NodeId = table.Key, NodeKind = ChangeNodeKind.Table, Node = table },
            ],
        };

        Assert.IsEmpty(ScriptComposition.GetUnsupportedOperations(batch));
    }

    [TestMethod]
    public void GetUnsupportedOperationsReturnsTodoOnlyOperations()
    {
        var enumType = new TypeDefinition("public.mood", "mood", "public", "user-defined", [], [], [], [], [], [], [], []);
        var batch = new OperationBatch
        {
            Operations =
            [
                new CreateOperation { NodeId = enumType.Key, NodeKind = ChangeNodeKind.Type, Node = enumType },
            ],
        };

        var unsupported = ScriptComposition.GetUnsupportedOperations(batch);

        Assert.HasCount(1, unsupported);
        Assert.AreEqual(enumType.Key, unsupported[0].NodeId);
    }
}
