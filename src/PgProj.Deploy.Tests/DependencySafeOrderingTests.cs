using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// Regression tests for G-27: removed objects and coordinated drop/recreate operations must be
/// ordered dependency-safely instead of falling back to the unordered pass or dropping a
/// dependency before its dependent.
/// </summary>
[TestClass]
public sealed class DependencySafeOrderingTests
{
    [TestMethod]
    public void RemovedDependentIsDroppedBeforeRemovedDependency()
    {
        // "calc" (function) depends on "money_amount" (type). Both are removed, so the
        // function's DROP must be emitted before the type's DROP.
        var type = CreateType("public.money_amount", "money_amount");
        var function = CreateFunction("public.calc", "calc", referencedTypes: [type.Key]);

        var graph = new DependencyGraph();
        graph.AddNode(DependencyNode.FromSemanticNode(type, []));
        graph.AddNode(DependencyNode.FromSemanticNode(function, [type.Key]));
        graph.AddEdge(function.Key, type.Key);

        var changes = new ChangeSet
        {
            Added = [],
            Removed =
            [
                new RemovedChange { NodeId = type.Key, NodeKind = ChangeNodeKind.Type, Node = type },
                new RemovedChange { NodeId = function.Key, NodeKind = ChangeNodeKind.Function, Node = function },
            ],
            Modified = [],
        };

        var batch = new ScriptGenerator().Generate(changes, graph);

        var dropIndex = batch.Operations
            .Select((operation, index) => (operation, index))
            .Where(entry => entry.operation is DropOperation)
            .ToDictionary(entry => entry.operation.NodeId, entry => entry.index, StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual(2, dropIndex.Count);
        Assert.IsTrue(dropIndex[function.Key] < dropIndex[type.Key],
            "The dependent function must be dropped before the type it depends on.");
    }

    [TestMethod]
    public void CoordinatedDropAndRecreateDropsDependentsBeforeDependenciesAndCreatesInReverse()
    {
        // "calc" (function) depends on "money_amount" (type). Both are modified via
        // drop-and-recreate. All drops must happen before all creates, and within each phase
        // the function must be handled before the type is dropped, and the type must be
        // recreated before the function is recreated.
        var oldType = CreateType("public.money_amount", "money_amount");
        var newType = CreateType("public.money_amount", "money_amount");
        var oldFunction = CreateFunction("public.calc", "calc", referencedTypes: [oldType.Key]);
        var newFunction = CreateFunction("public.calc", "calc", referencedTypes: [newType.Key]);

        var graph = new DependencyGraph();
        graph.AddNode(DependencyNode.FromSemanticNode(newType, []));
        graph.AddNode(DependencyNode.FromSemanticNode(newFunction, [newType.Key]));
        graph.AddEdge(newFunction.Key, newType.Key);

        var changes = new ChangeSet
        {
            Added = [],
            Removed = [],
            Modified =
            [
                new ModifiedChange
                {
                    NodeId = newType.Key,
                    NodeKind = ChangeNodeKind.Type,
                    OldNode = oldType,
                    NewNode = newType,
                    Details = [],
                },
                new ModifiedChange
                {
                    NodeId = newFunction.Key,
                    NodeKind = ChangeNodeKind.Function,
                    OldNode = oldFunction,
                    NewNode = newFunction,
                    Details = [],
                },
            ],
        };

        var batch = new ScriptGenerator().Generate(changes, graph);

        Assert.AreEqual(4, batch.Operations.Count);

        var lastDropIndex = batch.Operations
            .Select((operation, index) => (operation, index))
            .Where(entry => entry.operation is DropOperation)
            .Max(entry => entry.index);
        var firstCreateIndex = batch.Operations
            .Select((operation, index) => (operation, index))
            .Where(entry => entry.operation is CreateOperation)
            .Min(entry => entry.index);

        Assert.IsTrue(lastDropIndex < firstCreateIndex, "All drops must occur before any creates.");

        var indexByNodeIdAndType = batch.Operations
            .Select((operation, index) => (operation, index))
            .ToLookup(entry => (entry.operation.NodeId, entry.operation.GetType()));

        var functionDropIndex = indexByNodeIdAndType[(newFunction.Key, typeof(DropOperation))].Single().index;
        var typeDropIndex = indexByNodeIdAndType[(newType.Key, typeof(DropOperation))].Single().index;
        var typeCreateIndex = indexByNodeIdAndType[(newType.Key, typeof(CreateOperation))].Single().index;
        var functionCreateIndex = indexByNodeIdAndType[(newFunction.Key, typeof(CreateOperation))].Single().index;

        Assert.IsTrue(functionDropIndex < typeDropIndex,
            "The dependent function must be dropped before the type it depends on is dropped.");
        Assert.IsTrue(typeCreateIndex < functionCreateIndex,
            "The type must be recreated before the dependent function is recreated.");
    }

    private static TypeDefinition CreateType(string key, string name)
        => new(
            key,
            name,
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
                new CompositeTypeFieldDefinition("id", "integer", "integer"),
            ],
        };

    private static FunctionDefinition CreateFunction(string key, string name, IReadOnlyList<string> referencedTypes)
        => new(
            key,
            name,
            "public",
            "plpgsql",
            [],
            [],
            referencedTypes,
            [],
            [],
            [],
            [],
            [])
        {
            RawBody = "BEGIN RETURN NULL; END;",
            BodyDelimiter = "$$",
            Signature = new RoutineSignature
            {
                Parameters = [],
                ReturnType = "money_amount",
                Language = "plpgsql",
            },
        };
}
