using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>A table of a publication (G-134), with its column list and row filter.</summary>
public sealed record PublicationTableDefinition(string? SchemaName, string TableName)
{
    /// <summary>The identity of the table in the publication: <c>schema.table</c>.</summary>
    public string Key => DatabaseModel.CreateKey(SchemaName, TableName);

    /// <summary>The column list as written; empty when every column is published.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The columns as a set (PostgreSQL keeps them unordered), sorted, for the comparison.</summary>
    public IReadOnlyList<string> NormalizedColumns { get; init; } = [];

    /// <summary>The row filter as text, for the deployed SQL; null when there is none.</summary>
    public string? RawFilter { get; init; }

    /// <summary>The pg_query JSON of the row filter.</summary>
    public string? FilterAst { get; init; }

    /// <summary>The comparison form of the row filter, built from its tree against the table's column types; null when there is none.</summary>
    public string? NormalizedFilter { get; init; }

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
/// A <c>CREATE PUBLICATION</c> (G-134): a set of tables (or every table, or every table of some schemas) whose changes are sent to subscribers, and the operations it
/// publishes. It belongs to no schema; its name is its identity. The tables it names are dependencies (created first, dropped last). It is compared by what it
/// publishes and how, and a change to that is made in place with <c>ALTER PUBLICATION</c> except for <c>FOR ALL TABLES</c>, which cannot be altered into or out of.
/// </summary>
public sealed record PublicationDefinition(string Key, string Name) : ISemanticNode
{
    public string Kind => "Publication";

    public string? SchemaName => null;

    /// <summary>The tables the publication names, so it is created after them and dropped before them.</summary>
    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    public bool IsAllTables { get; init; }

    /// <summary>The tables named by <c>FOR TABLE</c>, in the order written.</summary>
    public IReadOnlyList<PublicationTableDefinition> Tables { get; init; } = [];

    /// <summary>The schemas named by <c>FOR TABLES IN SCHEMA</c>, as written.</summary>
    public IReadOnlyList<string> Schemas { get; init; } = [];

    /// <summary>The options as written, by lower-case name, for the deployed SQL.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary><see cref="Options"/> in comparison form (<see cref="PublicationOptions.Normalize"/>): the published operations as a sorted set, PostgreSQL's defaults left out.</summary>
    public IReadOnlyDictionary<string, string> NormalizedOptions { get; init; } = new Dictionary<string, string>();

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

/// <summary>What the options of a publication mean (G-134).</summary>
public static class PublicationOptions
{
    /// <summary>The operations a publication publishes when <c>publish</c> is not given.</summary>
    public static readonly IReadOnlyList<string> AllOperations = ["delete", "insert", "truncate", "update"];

    /// <summary>The key of a publication in the model: <c>publication:name</c>.</summary>
    public static string CreateKey(string name) => "publication:" + name;

    /// <summary>
    /// The options in the form two spellings share: <c>publish</c> as a sorted, lower-case set of operations (and left out when it is all four, PostgreSQL's default),
    /// the booleans as <c>true</c> or <c>false</c> and left out when false, and <c>publish_generated_columns</c> (PostgreSQL 18) as its keyword and left out when <c>none</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Normalize(IReadOnlyDictionary<string, string> options)
    {
        var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in options)
        {
            switch (name)
            {
                case "publish":
                    var operations = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(static operation => operation.ToLowerInvariant())
                        .Distinct()
                        .Order(StringComparer.Ordinal)
                        .ToArray();
                    if (!operations.SequenceEqual(AllOperations, StringComparer.Ordinal))
                    {
                        normalized[name] = string.Join(",", operations);
                    }

                    break;
                case "publish_via_partition_root":
                    if (IsTrue(value))
                    {
                        normalized[name] = "true";
                    }

                    break;
                case "publish_generated_columns":
                    if (!string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase))
                    {
                        normalized[name] = value.Trim().ToLowerInvariant();
                    }

                    break;
                default:
                    normalized[name] = value.Trim();
                    break;
            }
        }

        return normalized;
    }

    private static bool IsTrue(string value)
        => value.Trim().ToLowerInvariant() is "true" or "on" or "1" or "yes" or "t";
}
