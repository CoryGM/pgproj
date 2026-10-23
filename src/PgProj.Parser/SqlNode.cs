namespace PgProj.Parser;

public abstract record SqlNode(string Kind, string? Name, string? SchemaName)
{
    /// <summary>
    /// True when <see cref="SchemaName"/> was not written but given by <see cref="DefaultSchema"/> (G-93). A declaration\'s own name keeps it;
    /// a reference to an existing object (an index\'s or trigger\'s table, a comment\'s or grant\'s target) is found through the project\'s
    /// search path instead, as PostgreSQL finds it.
    /// </summary>
    public bool IsSchemaDefaulted { get; init; }

    private readonly PlaceSlot<SourceRange> _range = PlaceSlot<SourceRange>.Empty;

    /// <summary>
    /// Where the node was written in its file (G-309e): its statement without the <c>;</c>, or for a column, a constraint, or an index element its own
    /// text; null for a node no statement produced. Never part of the node's equality.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SourceRange? Range
    {
        get => _range.Value;
        init => _range = new PlaceSlot<SourceRange>(value);
    }
}
