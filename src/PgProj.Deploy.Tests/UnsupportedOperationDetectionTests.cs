using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// Regression coverage for G-28: <see cref="OperationBatch.GetUnsupportedOperations"/> must
/// identify every operation whose generated SQL is only a TODO/unsupported placeholder, so
/// callers (e.g. <c>publish --execute</c>) can fail closed instead of silently skipping the
/// affected schema change.
/// </summary>
[TestClass]
public sealed class UnsupportedOperationDetectionTests
{
    [TestMethod]
    public void BatchWithOnlyRealSqlHasNoUnsupportedOperations()
    {
        var table = CreateTable("public.orders", "orders", "public");
        var batch = new OperationBatch
        {
            Operations =
            [
                new CreateOperation { NodeId = table.Key, NodeKind = ChangeNodeKind.Table, Node = table },
            ],
        };

        Assert.IsEmpty(batch.GetUnsupportedOperations());
    }

    [TestMethod]
    public void BatchWithTodoOnlyEnumCreateIsReportedAsUnsupported()
    {
        var enumType = CreateType("public.mood", "mood", "public");
        var batch = new OperationBatch
        {
            Operations =
            [
                new CreateOperation { NodeId = enumType.Key, NodeKind = ChangeNodeKind.Type, Node = enumType },
            ],
        };

        var unsupported = batch.GetUnsupportedOperations();

        Assert.HasCount(1, unsupported);
        Assert.AreEqual(enumType.Key, unsupported[0].NodeId);
    }

    [TestMethod]
    public void BatchWithMixOfRealAndTodoOperationsOnlyReportsTheTodoOne()
    {
        var table = CreateTable("public.orders", "orders", "public");
        var enumType = CreateType("public.mood", "mood", "public");
        var batch = new OperationBatch
        {
            Operations =
            [
                new CreateOperation { NodeId = table.Key, NodeKind = ChangeNodeKind.Table, Node = table },
                new CreateOperation { NodeId = enumType.Key, NodeKind = ChangeNodeKind.Type, Node = enumType },
            ],
        };

        var unsupported = batch.GetUnsupportedOperations();

        Assert.HasCount(1, unsupported);
        Assert.AreEqual(enumType.Key, unsupported[0].NodeId);
    }

    private static TableDefinition CreateTable(string key, string name, string? schemaName)
        => new(key, name, schemaName, [], [], [], [], [], [], [], [], [], []);

    private static TypeDefinition CreateType(string key, string name, string? schemaName)
        => new(key, name, schemaName, "user-defined", [], [], [], [], [], [], [], []);
}
