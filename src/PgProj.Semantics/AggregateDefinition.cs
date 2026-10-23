using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// A <c>CREATE AGGREGATE</c> (G-130): a user-defined aggregate function in a schema. It shares PostgreSQL's function namespace, so it is identified like an overloaded
/// routine, by schema, name, and argument types (<see cref="DatabaseModel.CreateRoutineKey"/>), and a live object that uses an aggregate is recorded against that key.
/// The state, final, combine, and moving-aggregate functions, the sort operator, and the user-defined argument and state types are dependencies: created first,
/// dropped last. An aggregate cannot be altered in place, so a change is a drop and a create.
/// </summary>
public sealed record AggregateDefinition(string Key, string Name) : ISemanticNode
{
    /// <summary>
    /// The types named in the statement as the author wrote them (G-207), keyed by the normalized name the node keeps
    /// (<c>pg_catalog.bpchar</c> to <c>char(1)</c>, <c>My S.My T</c> to <c>"My S"."My T"</c>): deployment writes these, and the normalized
    /// names stay the identity and comparison form. Never compared.
    /// </summary>
    public IReadOnlyDictionary<string, string> WrittenTypes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string Kind => "Aggregate";

    public string? SchemaName { get; init; }

    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    /// <summary>The argument types and the state types, so a user-defined one is created before the aggregate and dropped after it.</summary>
    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    /// <summary>The state, final, combine, serial, deserial, and moving-aggregate functions, so they are created before the aggregate and dropped after it.</summary>
    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    /// <summary>The sort operator, when it is one of the project's.</summary>
    public IReadOnlyList<string> ReferencedOperators { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The argument types as written, for the deployed SQL, each with a <c>VARIADIC </c> prefix when variadic; empty for <c>(*)</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The name each argument was given, parallel to <see cref="Arguments"/> (null for none); deployed, never compared (G-292).</summary>
    public IReadOnlyList<string?> ArgumentNames { get; init; } = [];

    /// <summary>Negative for an ordinary aggregate; for an ordered-set one, how many of <see cref="Arguments"/> come before <c>ORDER BY</c>.</summary>
    public int DirectArgumentCount { get; init; } = -1;

    /// <summary>The options as written, by lower-case name, for the deployed SQL.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary><see cref="Options"/> in comparison form (<see cref="AggregateOptions.Normalize"/>): types canonical, defaults left out.</summary>
    public IReadOnlyDictionary<string, string> NormalizedOptions { get; init; } = new Dictionary<string, string>();

    public bool IsOrderedSet => DirectArgumentCount >= 0;

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

/// <summary>What the options of an aggregate mean (G-130).</summary>
public static class AggregateOptions
{
    /// <summary>The options whose value is the name of a function.</summary>
    public static readonly IReadOnlySet<string> FunctionOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "sfunc", "finalfunc", "combinefunc", "serialfunc", "deserialfunc", "msfunc", "minvfunc", "mfinalfunc",
    };

    /// <summary>The options whose value is a type.</summary>
    public static readonly IReadOnlySet<string> TypeOptions = new HashSet<string>(StringComparer.Ordinal) { "stype", "mstype" };

    /// <summary>The options that are a flag: present or not.</summary>
    public static readonly IReadOnlySet<string> FlagOptions = new HashSet<string>(StringComparer.Ordinal) { "finalfunc_extra", "mfinalfunc_extra", "hypothetical" };

    /// <summary>
    /// The options in the form two spellings share: types canonical (<see cref="RoutineTypeNames"/>), keywords lower case, a flag only when it is set, and an option
    /// that is PostgreSQL's default left out (<c>PARALLEL = UNSAFE</c>, a zero <c>SSPACE</c>, and <c>FINALFUNC_MODIFY</c> when it is what the kind of aggregate gets:
    /// <c>READ_ONLY</c>, or <c>READ_WRITE</c> for an ordered-set aggregate), so a live aggregate, which always has a value, compares with a project one that leaves it out.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Normalize(int directArgumentCount, IReadOnlyDictionary<string, string> options)
    {
        var defaultModify = directArgumentCount >= 0 ? "read_write" : "read_only";
        var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in options)
        {
            if (TypeOptions.Contains(name))
            {
                normalized[name] = RoutineTypeNames.Canonicalize(value);
            }
            else if (FlagOptions.Contains(name))
            {
                if (!string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                {
                    normalized[name] = "true";
                }
            }
            else if (name is "parallel" or "finalfunc_modify" or "mfinalfunc_modify")
            {
                var keyword = value.Trim().ToLowerInvariant();
                var isDefault = name == "parallel" ? keyword == "unsafe" : keyword == defaultModify;
                if (!isDefault)
                {
                    normalized[name] = keyword;
                }
            }
            else if (name is "sspace" or "msspace")
            {
                if (!string.Equals(value.Trim(), "0", StringComparison.Ordinal))
                {
                    normalized[name] = value.Trim();
                }
            }
            else
            {
                normalized[name] = value.Trim();
            }
        }

        return normalized;
    }

    /// <summary>The argument types of <paramref name="arguments"/> without their <c>VARIADIC</c> markers, joined the way a routine key joins them.</summary>
    public static string ArgumentSignature(IEnumerable<string> arguments)
        => string.Join(",", arguments.Select(static argument => argument.StartsWith("VARIADIC ", StringComparison.Ordinal) ? argument["VARIADIC ".Length..] : argument));

    /// <summary>The key of an aggregate: a routine's, since an aggregate and a function share one namespace.</summary>
    public static string CreateKey(string? schemaName, string name, IEnumerable<string> arguments)
        => DatabaseModel.CreateRoutineKey(schemaName, name, ArgumentSignature(arguments));
}
