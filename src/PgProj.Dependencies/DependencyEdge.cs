namespace PgProj.Dependencies;

public sealed record class DependencyEdge
{
    public required string FromNodeId { get; init; }

    public required string ToNodeId { get; init; }
}
