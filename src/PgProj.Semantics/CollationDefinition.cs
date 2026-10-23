using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// A <c>CREATE COLLATION</c> (G-148): a named way of ordering text, in a schema. Its identity is its schema and name. A collation depends on nothing the project
/// defines (its locale is the operating system's or ICU's), and columns, index keys, domains, and range types depend on it: it is created first and dropped last.
/// It has no <c>ALTER</c> for its provider, locale, or rules, so a change is a drop and a create (which PostgreSQL refuses while anything uses it).
/// </summary>
public sealed record CollationDefinition(string Key, string Name) : ISemanticNode
{
    public string Kind => "Collation";

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

    /// <summary><c>libc</c>, <c>icu</c>, or <c>builtin</c>, in lower case.</summary>
    public string Provider { get; init; } = "libc";

    /// <summary>The locale of an <c>icu</c> or <c>builtin</c> collation; null for <c>libc</c>.</summary>
    public string? Locale { get; init; }

    /// <summary>A <c>libc</c> collation's <c>LC_COLLATE</c>; null for the other providers.</summary>
    public string? LcCollate { get; init; }

    /// <summary>A <c>libc</c> collation's <c>LC_CTYPE</c>; null for the other providers.</summary>
    public string? LcCtype { get; init; }

    public bool IsDeterministic { get; init; } = true;

    /// <summary>An <c>icu</c> collation's tailoring rules; null when there are none.</summary>
    public string? Rules { get; init; }

    /// <summary>The <c>COMMENT ON COLLATION</c> text; null when there is none (G-154).</summary>
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

/// <summary>How two spellings of a collation's locale are told apart (G-148).</summary>
public static class CollationLocales
{
    /// <summary>
    /// A locale in the form two spellings share. An ICU locale is a language tag, in which <c>_</c> and <c>-</c> are the same separator and case does not matter
    /// (<c>en_US</c> and <c>en-us</c> name one locale), and PostgreSQL may store the standard form of what was written. A libc locale is a name the operating
    /// system looks up, so it is compared as written.
    /// </summary>
    public static string? Canonical(string provider, string? locale)
        => locale is null
            ? null
            : string.Equals(provider, "icu", StringComparison.OrdinalIgnoreCase)
                ? (locale.Trim().Length == 0 ? "und" : locale.Trim().Replace('_', '-').ToLowerInvariant())
                : locale.Trim();
}
