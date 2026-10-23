namespace PgProj.Parser;

/// <summary>
/// A <c>CREATE TEXT SEARCH DICTIONARY name (TEMPLATE = template, option = value, ...)</c> statement (G-149): a dictionary that turns a token into a lexeme (or drops it, as a stop
/// word does), made from a template (<c>simple</c>, <c>snowball</c>, <c>synonym</c>, <c>thesaurus</c>, <c>ispell</c>, or an extension's such as <c>unaccent</c>) and its options.
/// It lives in a schema. Templates and parsers are compiled code and are not modeled.
/// </summary>
public sealed record TextSearchDictionaryNode(string? Name, string? SchemaName) : SqlNode("TextSearchDictionary", Name, SchemaName)
{
    /// <summary>The template as its name parts joined by dots (<c>snowball</c>, <c>pg_catalog.simple</c>).</summary>
    public string? Template { get; init; }

    /// <summary>The options other than the template, by lower-case name, their values as text (a quoted value without its quotes).</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// An <c>ALTER TEXT SEARCH DICTIONARY name (option = value, option, ...)</c> statement (G-149): a value replaces or adds the option, and an option written without a value is
/// removed (a <see langword="null"/> value here).
/// </summary>
public sealed record TextSearchDictionaryAlterNode(string? Name, string? SchemaName) : SqlNode("TextSearchDictionaryAlter", Name, SchemaName)
{
    public IReadOnlyDictionary<string, string?> Options { get; init; } = new Dictionary<string, string?>();
}

/// <summary>
/// A <c>CREATE TEXT SEARCH CONFIGURATION name (PARSER = parser | COPY = configuration)</c> statement (G-149): which dictionaries handle each kind of token a parser finds.
/// The mappings are made by <c>ALTER TEXT SEARCH CONFIGURATION</c> statements (<see cref="TextSearchMappingNode"/>); a configuration made with <c>COPY</c> starts with the
/// source's mappings, which are not known without the server.
/// </summary>
public sealed record TextSearchConfigurationNode(string? Name, string? SchemaName) : SqlNode("TextSearchConfiguration", Name, SchemaName)
{
    /// <summary>The parser as its name parts joined by dots (<c>pg_catalog.default</c>); null when the configuration is a <c>COPY</c>.</summary>
    public string? Parser { get; init; }

    /// <summary>The configuration copied, as its name parts joined by dots (<c>pg_catalog.english</c>); null when a parser is named.</summary>
    public string? CopyFrom { get; init; }
}

public enum TextSearchMappingAction
{
    /// <summary><c>ADD MAPPING FOR tokens WITH dictionaries</c>.</summary>
    Add,

    /// <summary><c>ALTER MAPPING FOR tokens WITH dictionaries</c>.</summary>
    Alter,

    /// <summary><c>DROP MAPPING [IF EXISTS] FOR tokens</c>.</summary>
    Drop,
}

/// <summary>
/// An <c>ALTER TEXT SEARCH CONFIGURATION name ADD|ALTER|DROP MAPPING FOR token types [WITH dictionaries]</c> statement (G-149). The <c>REPLACE</c> forms and the
/// forms with no token type are not read; they are reported as not modeled.
/// </summary>
public sealed record TextSearchMappingNode(string? Name, string? SchemaName) : SqlNode("TextSearchMapping", Name, SchemaName)
{
    public TextSearchMappingAction Action { get; init; }

    /// <summary>The token types in lower case (<c>asciiword</c>, <c>word</c>).</summary>
    public IReadOnlyList<string> Tokens { get; init; } = [];

    /// <summary>The dictionaries in the order they are tried, each as its name parts joined by dots; empty for a drop.</summary>
    public IReadOnlyList<string> Dictionaries { get; init; } = [];
}
