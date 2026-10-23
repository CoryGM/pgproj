using PgProj.Parser;

namespace PgProj.Semantics;

public sealed record FunctionDefinition(
    string Key,
    string Name,
    string? SchemaName,
    string? Language,
    IReadOnlyList<string> ReferencedTables,
    IReadOnlyList<string> ReferencedColumns,
    IReadOnlyList<string> ReferencedTypes,
    IReadOnlyList<string> ReferencedFunctions,
    IReadOnlyList<TableDefinition> ResolvedTables,
    IReadOnlyList<ColumnDefinition> ResolvedColumns,
    IReadOnlyList<TypeDefinition> ResolvedTypes,
    IReadOnlyList<FunctionDefinition> ResolvedFunctions) : ISemanticNode
{
    /// <summary>The keys of the project operators (G-129) this object expressions use (G-138): it is created after them and dropped before them.</summary>
    public IReadOnlyList<string> ReferencedOperators { get; init; } = [];

    public string Kind => "Function";

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The keys of the project views (and materialized views) this one's query or body reads, so it is created after them (G-90, G-91).</summary>
    public IReadOnlyList<string> ReferencedViews { get; init; } = [];

    public ExpressionNode? BodyExpressionNode { get; init; }

    public IReadOnlyList<string> NormalizedExpressions { get; init; } = [];

    /// <summary>
    /// The routine body exactly as the user wrote it. This is deploy-only state and is
    /// deliberately excluded from canonicalization and diffing so formatting never
    /// produces a phantom change.
    /// </summary>
    public string? RawBody { get; init; }

    /// <summary>
    /// The original quoting delimiter that wrapped the body, such as <c>$$</c> or <c>$fn$</c>.
    /// </summary>
    public string? BodyDelimiter { get; init; }

    /// <summary>True for a SQL-standard body (<c>BEGIN ATOMIC</c> or <c>RETURN</c>, G-95), deployed without <c>AS</c> or quoting.</summary>
    public bool IsSqlStandardBody { get; init; }

    /// <summary>
    /// A SQL-standard body as PostgreSQL itself renders it (<c>pg_get_functiondef</c>), or null when not available (G-95). A live
    /// function carries its own rendering; a project function carries the rendering of its body created as a temporary function on
    /// the target server during <c>diff</c>/<c>publish</c>. When both sides have one, the diff compares these instead of the parsed
    /// body, since PostgreSQL stores such a body parsed (qualifying names, adding casts and aliases), not as written.
    /// </summary>
    public string? ServerRenderedBody { get; init; }

    /// <summary>
    /// Signature metadata used to regenerate the CREATE header around the verbatim body.
    /// </summary>
    public RoutineSignature? Signature { get; init; }

    public string? Comment { get; init; }

    /// <summary>What a PL/pgSQL body names (G-303b); null for any other language or a body that could not be read. See <see cref="PlpgsqlBodyReferences"/>.</summary>
    public PlpgsqlBodyReferences? PlpgsqlReferences { get; init; }

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
