using PgProj.Parser;

namespace PgProj.Semantics;

public sealed record SequenceDefinition(
    string Key,
    string Name,
    string? SchemaName,
    IReadOnlyList<string> ReferencedTables,
    IReadOnlyList<string> ReferencedColumns,
    IReadOnlyList<string> ReferencedTypes,
    IReadOnlyList<string> ReferencedFunctions,
    IReadOnlyList<TableDefinition> ResolvedTables,
    IReadOnlyList<ColumnDefinition> ResolvedColumns,
    IReadOnlyList<TypeDefinition> ResolvedTypes,
    IReadOnlyList<FunctionDefinition> ResolvedFunctions) : ISemanticNode
{
    public string Kind => "Sequence";

    public SequenceNode? DeclarationNode { get; init; }

    public string? DataType { get; init; }

    public string? IncrementBy { get; init; }

    public string? MinValue { get; init; }

    public string? MaxValue { get; init; }

    public string? StartValue { get; init; }

    public string? CacheSize { get; init; }

    public bool? Cycle { get; init; }

    /// <summary>True for an unlogged sequence (<c>CREATE UNLOGGED SEQUENCE</c>, or a serial's or identity's on an unlogged table); compared, and changed in place with <c>SET LOGGED/UNLOGGED</c> (G-224).</summary>
    public bool IsUnlogged { get; init; }

    public string? OwnedByTable { get; init; }

    /// <summary>
    /// The table <see cref="OwnedByTable"/> names, schema-qualified, when it was written without a schema (<c>OWNED BY instance_map.instance_id</c>): the table the search path finds, which is how
    /// PostgreSQL resolves it and how a live database reads it back (G-283). Null when <see cref="OwnedByTable"/> already names its schema. It is what the diff compares; <see cref="OwnedByTable"/>
    /// stays as written, which is what is deployed.
    /// </summary>
    public string? ResolvedOwnedByTable { get; init; }

    public string? OwnedByColumn { get; init; }

    public string? Comment { get; init; }

    /// <summary>
    /// True when this sequence backs a <c>GENERATED ... AS IDENTITY</c> column. PostgreSQL creates
    /// and drops identity sequences automatically as part of the owning column's DDL, so deploy
    /// generation must not emit an explicit CREATE/ALTER/DROP SEQUENCE for it (unlike SERIAL, which
    /// needs an explicit sequence created before the table it defaults from). This is still tracked
    /// as a normal <see cref="SequenceDefinition"/> so it participates in diffing/reload identically
    /// to the live catalog's owned sequence.
    /// </summary>
    public bool IsIdentityOwned { get; init; }

    /// <summary>
    /// For an identity's sequence, the options its column states (<see cref="IncrementBy"/>, <see cref="StartValue"/>, ...): only these are compared, and
    /// PostgreSQL's defaults stand for the rest (G-214). Null for any other sequence, whose options are all compared.
    /// </summary>
    public IReadOnlySet<string>? IdentityStatedOptions { get; init; }

    /// <summary>
    /// The options this sequence has once PostgreSQL's defaults are applied (a plain <c>CREATE SEQUENCE</c> is a <c>bigint</c>
    /// sequence from 1 to 9223372036854775807, and so on). Used to decide whether two sequences differ; the properties above
    /// keep what was written, which is what gets deployed (G-57).
    /// </summary>
    public SequenceOptions GetEffectiveOptions() => SequenceOptions.Resolve(this);

    public bool IsRecovered { get; set; }

    public bool HasErrors { get; set; }

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
