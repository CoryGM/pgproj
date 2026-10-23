namespace PgProj.Parser;

public sealed record SequenceNode(
    string? Name,
    string? SchemaName,
    IReadOnlyDictionary<string, string>? Options) : SqlNode("Sequence", Name, SchemaName)
{
    /// <summary>True for <c>CREATE UNLOGGED SEQUENCE</c> (G-224).</summary>
    public bool IsUnlogged { get; init; }
}
