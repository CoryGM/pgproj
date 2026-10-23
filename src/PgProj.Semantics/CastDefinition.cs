using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// A <c>CREATE CAST</c> (G-131): a user-defined conversion from one type to another. It has no schema; its identity is the pair of types, in
/// their comparison form (<see cref="CastTypeNames.Canonicalize"/>), so <c>integer</c> and <c>pg_catalog.int4</c> name one cast. The function it
/// runs and the user-defined types it converts are dependencies: created first, dropped last.
/// </summary>
public sealed record CastDefinition(string Key, string Name) : ISemanticNode
{
    /// <summary>
    /// The types named in the statement as the author wrote them (G-207), keyed by the normalized name the node keeps
    /// (<c>pg_catalog.bpchar</c> to <c>char(1)</c>, <c>My S.My T</c> to <c>"My S"."My T"</c>): deployment writes these, and the normalized
    /// names stay the identity and comparison form. Never compared.
    /// </summary>
    public IReadOnlyDictionary<string, string> WrittenTypes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string Kind => "Cast";

    public string? SchemaName => null;

    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    /// <summary>The source and target types, so a user-defined one is created before the cast and dropped after it.</summary>
    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    /// <summary>The converting function, so it is created before the cast and dropped after it.</summary>
    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The source type as written, for the deployed SQL (<c>text</c>, <c>public.mytype</c>).</summary>
    public string? SourceType { get; init; }

    /// <summary>The target type as written, for the deployed SQL.</summary>
    public string? TargetType { get; init; }

    /// <summary>The converting function as written, without its arguments; null for <c>WITHOUT FUNCTION</c> and <c>WITH INOUT</c>.</summary>
    public string? FunctionName { get; init; }

    /// <summary>The argument types of <see cref="FunctionName"/> as written, for the deployed SQL.</summary>
    public IReadOnlyList<string> FunctionArguments { get; init; } = [];

    /// <summary><see cref="FunctionArguments"/> in comparison form, in order (a function's parameters are positional).</summary>
    public IReadOnlyList<string> NormalizedFunctionArguments { get; init; } = [];

    /// <summary>How the conversion is done: <c>function</c>, <c>binary</c> (<c>WITHOUT FUNCTION</c>), or <c>inout</c> (<c>WITH INOUT</c>).</summary>
    public string Method { get; init; } = "function";

    /// <summary>When the cast is applied: <c>explicit</c>, <c>assignment</c>, or <c>implicit</c>.</summary>
    public string Context { get; init; } = "explicit";

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

/// <summary>The comparison form of a cast's types (G-131).</summary>
public static class CastTypeNames
{
    /// <summary>
    /// A type as a cast is compared and keyed: the routine-parameter form (lower case, no modifiers, no <c>pg_catalog.</c>, the long spellings
    /// folded; <see cref="RoutineTypeNames.Canonicalize"/>), and without a leading <c>public.</c> (G-137 moved that into the routine form).
    /// </summary>
    public static string Canonicalize(string? type) => RoutineTypeNames.Canonicalize(type);

    /// <summary>The identity key of the cast from <paramref name="source"/> to <paramref name="target"/>: <c>cast:(source AS target)</c>, in comparison form.</summary>
    public static string CreateKey(string? source, string? target)
        => $"cast:({Canonicalize(source)} AS {Canonicalize(target)})";
}
