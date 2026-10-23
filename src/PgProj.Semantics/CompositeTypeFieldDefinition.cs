namespace PgProj.Semantics;

public sealed record CompositeTypeFieldDefinition(
    string Name,
    string? DataType,
    string? RawType)
{
    /// <summary>
    /// The field's type in the canonical form a column's type is compared in
    /// (<see cref="ColumnTypeNormalizer.BuildComparableTypeSignature"/>), built from pg_query's parse of the field (G-99), so
    /// <c>varchar(10)</c> and PostgreSQL's <c>character varying(10)</c> are the same type. Null when the parse was not available;
    /// <see cref="DataType"/> is compared then. <see cref="RawType"/> stays what is deployed.
    /// </summary>
    public string? ComparableType { get; init; }

    /// <summary>
    /// The field's <c>COLLATE</c> as written (<c>C</c>, <c>app.ci</c>), or null when it states none (G-192). Deployed after the type and compared as
    /// <see cref="CollationNames"/> compares a column's collation.
    /// </summary>
    public string? CollationName { get; init; }

    private readonly PgProj.Parser.PlaceSlot<SemanticLocation> _location = PgProj.Parser.PlaceSlot<SemanticLocation>.Empty;

    /// <summary>
    /// Where the definition was written: its file and range (G-309e), for the diagnostics about it; null for one read from a database. Never part of the
    /// definition's equality, never compared, and not in <c>--dump-model</c>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SemanticLocation? Location
    {
        get => _location.Value;
        init => _location = new PgProj.Parser.PlaceSlot<SemanticLocation>(value);
    }
}
