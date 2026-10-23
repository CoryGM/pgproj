using PgProj.Parser;

namespace PgProj.Semantics;

public sealed record IndexKeyElementDefinition(
    string? Name,
    string? RawExpression,
    string? CollationName,
    string? OperatorClassName,
    IReadOnlyList<string> OperatorClassOptions,
    bool IsDescending,
    bool? NullsFirst)
{
    /// <summary>The comparison form of a key expression, built from its parsed tree (G-73). Null for a column or when there is no tree.</summary>
    public string? NormalizedExpression { get; init; }

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

public sealed record IndexDefinition(
    string Key,
    string Name,
    string? SchemaName,
    string? TableName,
    string? AccessMethod,
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

    public string Kind => "Index";

    public IndexNode? DeclarationNode { get; init; }

    public ExpressionNode? PredicateExpressionNode { get; init; }

    public IReadOnlyList<string> NormalizedExpressions { get; init; } = [];

    /// <summary>The comparison form of the WHERE predicate, built from its parsed tree (G-73). Null when there is no tree.</summary>
    public string? NormalizedPredicateExpression { get; init; }

    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    public bool IsUnique { get; init; }

    /// <summary>True for <c>NULLS NOT DISTINCT</c> on a unique index (G-262).</summary>
    public bool NullsNotDistinct { get; init; }

    /// <summary>True when the project states <c>ALTER TABLE t CLUSTER ON</c> this one (G-256).</summary>
    public bool IsClustered { get; init; }

    /// <summary>True when the project states <c>ALTER TABLE t REPLICA IDENTITY USING INDEX</c> this one (G-255).</summary>
    public bool IsReplicaIdentity { get; init; }

    public string? Comment { get; init; }

    public IReadOnlyList<IndexKeyElementDefinition> KeyElements { get; init; } = [];

    public IReadOnlyList<string> IncludeColumns { get; init; } = [];

    /// <summary>True when the project wrote the index without a name; it is deployed so, and PostgreSQL gives it <see cref="ISemanticNode.Name"/> (G-225).</summary>
    public bool IsNameGenerated { get; init; }

    public string? RawPredicateExpression { get; init; }

    /// <summary>
    /// The index's <c>WITH (...)</c> storage parameters, each as written, which is what is deployed (G-195). They are an unordered list compared by
    /// <see cref="TableStorageOptionNode.UnquotedComparisonText"/>; a change is applied in place with <c>ALTER INDEX ... SET/RESET</c>.
    /// </summary>
    public IReadOnlyList<TableStorageOptionNode> StorageOptions { get; init; } = [];

    /// <summary>
    /// The key of the table a <c>LIKE</c> clause copied this index from (G-221); null for one the project declares itself. The table's <c>CREATE TABLE</c>
    /// makes it, so it is not created on its own with the table; once the table exists it is changed like any other. Not compared.
    /// </summary>
    public string? CopiedByLike { get; init; }

    /// <summary>
    /// The <c>WHERE</c> predicate as PostgreSQL itself renders it (<c>pg_get_expr(indpred, indrelid)</c>), or null when not
    /// available (G-100). A live index carries its own rendering; a project index carries the rendering of its predicate on a
    /// temporary index over a copy of the live table, made during <c>diff</c>/<c>publish</c>. PostgreSQL types a literal by the
    /// column it is compared with (<c>status &lt;&gt; 'x'</c> on an enum is <c>status &lt;&gt; 'x'::s.st</c>, a <c>timestamptz</c>
    /// literal gains its offset), which the parsed comparison cannot know.
    /// </summary>
    public string? ServerRenderedPredicate { get; init; }

    /// <summary>
    /// False for a live index PostgreSQL marks <c>INVALID</c> (<c>pg_index.indisvalid</c>), left by a failed <c>CREATE INDEX CONCURRENTLY</c> (G-271): it is
    /// not used by queries and is rebuilt. Always true for a project index.
    /// </summary>
    public bool IsValid { get; init; } = true;

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
