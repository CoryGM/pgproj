using PgProj.Parser;

namespace PgProj.Semantics;

public sealed record ProcedureDefinition(
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

    public string Kind => "Procedure";

    /// <summary>
    /// True when another procedure of this name has an <code>OUT</code> parameter (G-232), so a statement that names this one must spell out every parameter's mode:
    /// <c>ptest10(integer, integer, integer)</c> matches both <c>ptest10(IN a, IN b, IN c)</c> and <c>ptest10(OUT a, IN b, IN c)</c>.
    /// </summary>
    public bool UsesFullIdentity { get; init; }

    /// <summary>The argument list a statement that names this procedure (<c>DROP</c>, <c>COMMENT</c>, <c>OWNER</c>, <c>GRANT</c>) writes between the parentheses.</summary>
    public string ArgumentIdentitySql
        => Signature is null ? string.Empty : UsesFullIdentity ? Signature.RenderFullIdentitySql() : Signature.RenderArgumentTypeSignatureSql();

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The keys of the project views (and materialized views) this one's query or body reads, so it is created after them (G-90, G-91).</summary>
    public IReadOnlyList<string> ReferencedViews { get; init; } = [];

    public ExpressionNode? BodyExpressionNode { get; init; }

    public IReadOnlyList<string> NormalizedExpressions { get; init; } = [];

    /// <summary>
    /// The procedure body exactly as the user wrote it. Deploy-only state, excluded from diffing.
    /// </summary>
    public string? RawBody { get; init; }

    public string? BodyDelimiter { get; init; }

    /// <summary>True for a SQL-standard body (<c>BEGIN ATOMIC</c> or <c>RETURN</c>, G-95), deployed without <c>AS</c> or quoting.</summary>
    public bool IsSqlStandardBody { get; init; }

    /// <summary>
    /// A SQL-standard body as PostgreSQL itself renders it, or null when not available (G-95); see
    /// <see cref="FunctionDefinition.ServerRenderedBody"/>.
    /// </summary>
    public string? ServerRenderedBody { get; init; }

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
