using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// A <c>CREATE OPERATOR</c> (G-129): a user-defined operator in a schema. Its identity is the schema, its name, and the types of its two operands in
/// comparison form (<see cref="OperatorNames"/>), so <c>integer</c> and <c>pg_catalog.int4</c> name one operand type and <c>a + b</c> can be defined for several
/// pairs. The function it runs, the estimator functions, and the user-defined operand types are dependencies: created first, dropped last.
/// </summary>
public sealed record OperatorDefinition(string Key, string Name) : ISemanticNode
{
    /// <summary>
    /// The types named in the statement as the author wrote them (G-207), keyed by the normalized name the node keeps
    /// (<c>pg_catalog.bpchar</c> to <c>char(1)</c>, <c>My S.My T</c> to <c>"My S"."My T"</c>): deployment writes these, and the normalized
    /// names stay the identity and comparison form. Never compared.
    /// </summary>
    public IReadOnlyDictionary<string, string> WrittenTypes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string Kind => "Operator";

    public string? SchemaName { get; init; }

    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    /// <summary>The operand types, so a user-defined one is created before the operator and dropped after it.</summary>
    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    /// <summary>The function the operator runs and its estimator functions, so they are created before the operator and dropped after it.</summary>
    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The operator's own name, without its schema (<c>===</c>).</summary>
    public string OperatorName { get; init; } = string.Empty;

    /// <summary>The left operand's type as written, for the deployed SQL; null for a prefix operator.</summary>
    public string? LeftType { get; init; }

    /// <summary>The right operand's type as written, for the deployed SQL; null for a postfix operator.</summary>
    public string? RightType { get; init; }

    /// <summary>The function the operator runs, as written.</summary>
    public string? FunctionName { get; init; }

    /// <summary>The operator with the operands swapped, as <c>schema.name</c>; null when there is none (a live read fills in the one PostgreSQL links either way).</summary>
    public string? Commutator { get; init; }

    /// <summary>The operator with the opposite truth value, as <c>schema.name</c>; null when there is none.</summary>
    public string? Negator { get; init; }

    /// <summary>The <c>RESTRICT</c> selectivity estimator, as written; null when there is none.</summary>
    public string? RestrictFunction { get; init; }

    /// <summary>The <c>JOIN</c> selectivity estimator, as written; null when there is none.</summary>
    public string? JoinFunction { get; init; }

    public bool IsHashes { get; init; }

    public bool IsMerges { get; init; }

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

/// <summary>The comparison form of an operator's identity (G-129).</summary>
public static class OperatorNames
{
    /// <summary>The identity key <c>schema.name(left,right)</c>, the operand types in comparison form (<see cref="RoutineTypeNames.Canonicalize"/>) and <c>none</c> for a missing one.</summary>
    public static string CreateKey(string? schemaName, string name, string? leftType, string? rightType)
        => $"{DatabaseModel.CreateKey(schemaName, name)}({Operand(leftType)},{Operand(rightType)})";

    /// <summary><c>schema.name</c> into its two parts; an operator name has no dots, so the last dot splits them.</summary>
    public static (string? Schema, string Name) Split(string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        return dot < 0 ? (null, qualified) : (qualified[..dot], qualified[(dot + 1)..]);
    }

    private static string Operand(string? type) => string.IsNullOrWhiteSpace(type) ? "none" : RoutineTypeNames.Canonicalize(type);
}
