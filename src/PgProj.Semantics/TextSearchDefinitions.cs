using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// A <c>CREATE TEXT SEARCH DICTIONARY</c> (G-149): a template and its options, in a schema. The options can be changed in place (<c>ALTER TEXT SEARCH DICTIONARY</c>); the
/// template cannot, so a change of template is a drop and a create, which PostgreSQL refuses while a configuration uses the dictionary.
/// </summary>
public sealed record TextSearchDictionaryDefinition(string Key, string Name) : ISemanticNode
{
    public string Kind => "TextSearchDictionary";

    public string? SchemaName { get; init; }

    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The template as written (<c>snowball</c>, <c>pg_catalog.simple</c>).</summary>
    public string? Template { get; init; }

    /// <summary>The options as written, by lower-case name, after the project's own <c>ALTER TEXT SEARCH DICTIONARY</c> statements.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary>The template in comparison form: <c>pg_catalog.</c> left off, lower case.</summary>
    public string? NormalizedTemplate { get; init; }

    /// <summary>The <c>COMMENT ON TEXT SEARCH DICTIONARY</c> text; null when there is none (G-154).</summary>
    public string? Comment { get; init; }

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

/// <summary>
/// A <c>CREATE TEXT SEARCH CONFIGURATION</c> (G-149): which dictionaries handle each kind of token a parser finds. It is made with a parser or as a copy of another
/// configuration; its mappings are what the project's <c>ALTER TEXT SEARCH CONFIGURATION ... MAPPING</c> statements make of it. A copy starts with the source's mappings,
/// which are not known without the server, so for a copy only the mappings the project states are compared (<see cref="IsCopy"/>).
/// </summary>
public sealed record TextSearchConfigurationDefinition(string Key, string Name) : ISemanticNode
{
    public string Kind => "TextSearchConfiguration";

    public string? SchemaName { get; init; }

    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The parser as written (<c>pg_catalog.default</c>); null for a copy.</summary>
    public string? Parser { get; init; }

    /// <summary>The configuration copied, as written; null when a parser is named.</summary>
    public string? CopyFrom { get; init; }

    public bool IsCopy => !string.IsNullOrWhiteSpace(CopyFrom);

    /// <summary>
    /// The dictionaries tried for each token type (lower case), as written, in order. For a copy, a token type the project drops is present with no dictionaries, since the
    /// source's mapping for it is not known to be absent.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Mappings { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    public string? NormalizedParser { get; init; }

    public string? NormalizedCopyFrom { get; init; }

    /// <summary>The <c>COMMENT ON TEXT SEARCH CONFIGURATION</c> text; null when there is none (G-154).</summary>
    public string? Comment { get; init; }

    /// <summary><see cref="Mappings"/> with each dictionary in comparison form (<see cref="TextSearchNames.Canonical"/>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NormalizedMappings { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

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

/// <summary>How a text search object is named in comparison form (G-149).</summary>
public static class TextSearchNames
{
    /// <summary>
    /// A parser, template, dictionary, or configuration name in the form two spellings share: <c>pg_catalog.</c> left off (it is always searched, and a live read writes it), a
    /// name with another schema as it is, and an unqualified name that one of the project's own objects of that kind has in <paramref name="contextSchema"/> qualified with
    /// it, so a project that writes <c>my_stem</c> and a database that reports <c>app.my_stem</c> agree. Compared exactly, as PostgreSQL compares names (G-206).
    /// </summary>
    public static string BuiltIn(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : name.Trim().StartsWith("pg_catalog.", StringComparison.Ordinal) ? name.Trim()["pg_catalog.".Length..] : name.Trim();

    public static string Canonical(DatabaseModel model, string? contextSchema, string? name, bool dictionary)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();
        if (trimmed.StartsWith("pg_catalog.", StringComparison.Ordinal))
        {
            return trimmed["pg_catalog.".Length..];
        }

        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            return trimmed;
        }

        var key = DatabaseModel.CreateKey(contextSchema, trimmed);
        var declared = dictionary
            ? model.TextSearchDictionaries.ContainsKey("tsdictionary:" + key)
            : model.TextSearchConfigurations.ContainsKey("tsconfiguration:" + key);
        return declared ? key : trimmed;
    }
}
