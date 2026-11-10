using PgProj.Diff;
using PgProj.Dependencies;
using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>Base types (G-267, the user's design of 2026-11-09): the shell before the support functions, and the functions a dropped type takes with it.</summary>
public sealed partial class ScriptGenerator
{
    /// <summary>
    /// The support functions of the base types this script drops, to remove them or to create them again: <c>DROP TYPE ... CASCADE</c> drops them (item 4), so no
    /// <c>DROP FUNCTION</c> of their own is written, which would fail, the type depending on them.
    /// </summary>
    private static HashSet<string> FindCascadedSupportFunctions(IEnumerable<RemovedChange> removedChanges, IEnumerable<ModifiedChange> modifiedChanges, DependencyGraph graph)
    {
        var removed = removedChanges.ToArray();
        var cascaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in removed.Select(static change => change.Node).OfType<TypeDefinition>().Where(static type => type.IsBaseType))
        {
            foreach (var function in removed.Where(change => change.Node is not null && IsOwnSupportFunction(change.Node, type)))
            {
                cascaded.Add(function.NodeId);
            }
        }

        foreach (var change in modifiedChanges.Where(static change => change.OldNode is TypeDefinition { IsBaseType: true } && IsDropAndRecreateObject(change)))
        {
            foreach (var node in graph.Nodes.Values.Where(node => IsOwnSupportFunction(node.SemanticNode, (TypeDefinition)change.OldNode)))
            {
                cascaded.Add(node.NodeId);
            }
        }

        return cascaded;
    }

    /// <summary>
    /// <c>CREATE TYPE name;</c> before the first support function of each base type the script creates (item 2): the input function returns the type and the output
    /// function takes it, so the shell stands in for it until they exist. A type created with no support function before it in the script needs none.
    /// </summary>
    private static List<SqlOperation> InsertBaseTypeShells(List<SqlOperation> operations)
    {
        var shells = new List<(int Index, BaseTypeShellOperation Shell)>();
        for (var typeIndex = 0; typeIndex < operations.Count; typeIndex++)
        {
            if (operations[typeIndex] is not CreateOperation { Node: TypeDefinition { IsBaseType: true } type } create)
            {
                continue;
            }

            var first = operations.FindIndex(0, typeIndex, operation => operation is CreateOperation { Node: FunctionDefinition function } && IsOwnSupportFunction(function, type));
            if (first >= 0)
            {
                shells.Add((first, new BaseTypeShellOperation { NodeId = create.NodeId, NodeKind = ChangeNodeKind.Type, SchemaName = type.SchemaName, TypeName = type.Name }));
            }
        }

        if (shells.Count == 0)
        {
            return operations;
        }

        var result = new List<SqlOperation>(operations);
        foreach (var (index, shell) in shells.OrderByDescending(static shell => shell.Index))
        {
            result.Insert(index, shell);
        }

        return result;
    }
}
