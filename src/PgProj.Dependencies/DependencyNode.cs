using PgProj.Semantics;

namespace PgProj.Dependencies;

public enum DependencyKind
{
    Unknown,
    Table,
    View,
    Function,
    Type,
    Index,
    Constraint,
}

public sealed record class DependencyNode
{
    public required string NodeId { get; init; }

    public required DependencyKind Kind { get; init; }

    public required ISemanticNode SemanticNode { get; init; }

    public required IReadOnlyList<string> ReferencedNodeIds { get; init; }

    public static DependencyNode FromSemanticNode(ISemanticNode semanticNode, IReadOnlyList<string> referencedNodeIds)
    {
        ArgumentNullException.ThrowIfNull(semanticNode);
        ArgumentNullException.ThrowIfNull(referencedNodeIds);

        return new DependencyNode
        {
            NodeId = semanticNode.Key,
            Kind = semanticNode switch
            {
                TableDefinition => DependencyKind.Table,
                ViewDefinition => DependencyKind.View,
                FunctionDefinition => DependencyKind.Function,
                TypeDefinition => DependencyKind.Type,
                IndexDefinition => DependencyKind.Index,
                ConstraintDefinition => DependencyKind.Constraint,
                _ => DependencyKind.Unknown,
            },
            SemanticNode = semanticNode,
            ReferencedNodeIds = referencedNodeIds,
        };
    }
}
