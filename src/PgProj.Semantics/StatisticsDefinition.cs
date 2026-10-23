using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>One column or expression of a <see cref="StatisticsDefinition"/> (G-150).</summary>
public sealed record StatisticsElementDefinition(string? Name)
{
    public string? RawExpression { get; init; }

    public string? ExpressionAst { get; init; }

    /// <summary>The expression in comparison form (<see cref="SemanticDiffNormalizationPass"/>): PostgreSQL prints it its own way; null for a column.</summary>
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

/// <summary>
/// A <c>CREATE STATISTICS</c> (G-150): extended statistics on the columns or expressions of one table. Its identity is its schema and name; the table is a dependency, so
/// it is created after the table and dropped before it. It has no <c>ALTER</c> for its columns or kinds, so a change is a drop and a create, which loses nothing: the
/// statistics are collected again by the next <c>ANALYZE</c>.
/// </summary>
public sealed record StatisticsDefinition(string Key, string Name) : ISemanticNode
{
    /// <summary>The kinds PostgreSQL makes when none is written.</summary>
    public static readonly IReadOnlyList<string> AllKinds = ["dependencies", "mcv", "ndistinct"];

    public string Kind => "Statistics";

    public string? SchemaName { get; init; }

    /// <summary>The table the statistics are about, by key.</summary>
    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    public string? TableSchemaName { get; init; }

    public string? TableName { get; init; }

    /// <summary>
    /// The key of the table a <c>LIKE</c> clause copied this statistics object from (G-221); null for one the project declares itself. The table's <c>CREATE TABLE</c>
    /// makes it, so it is not created on its own with the table; once the table exists it is changed like any other. Not compared.
    /// </summary>
    public string? CopiedByLike { get; init; }

    /// <summary>The kinds as written, in lower case; empty when none was written.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>The kinds in comparison form: sorted, and all three when none was written.</summary>
    public IReadOnlyList<string> NormalizedKinds { get; init; } = AllKinds;

    public IReadOnlyList<StatisticsElementDefinition> Elements { get; init; } = [];

    /// <summary>The <c>COMMENT ON STATISTICS</c> text; null when there is none (G-154).</summary>
    public string? Comment { get; init; }

    public bool IsRecovered { get; set; }

    public bool HasErrors { get; set; }

    /// <summary><see cref="Kinds"/> in comparison form: <paramref name="kinds"/> sorted and distinct, or all three when none is written.</summary>
    public static IReadOnlyList<string> NormalizeKinds(IEnumerable<string> kinds)
    {
        var sorted = kinds.Select(static kind => kind.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return sorted.Length == 0 ? AllKinds : sorted;
    }

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
