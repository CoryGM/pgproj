using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// What a project (or a live database) says about a schema as such: that it is declared (<c>CREATE SCHEMA</c>, G-284) and its comment (G-160). A project has one for
/// each schema it declares or states a <c>COMMENT ON SCHEMA</c> for (<c>IS NULL</c> states that it has none); a live model has one for each schema it reads. A comment
/// is compared only where the project states one, so a database's comment on a schema the project does not comment on is never removed (the same rule as an owner,
/// G-146), and pgproj never drops a schema. A schema is also created for the objects in it.
/// </summary>
public sealed record SchemaDefinition(string Key, string Name) : ISemanticNode
{
    public string Kind => IsDeclared ? "Schema" : "SchemaComment";

    /// <summary>True when a <c>CREATE SCHEMA</c> declares the schema (G-284), so it is created even with nothing in it.</summary>
    public bool IsDeclared { get; init; }

    /// <summary>True when a <c>COMMENT ON SCHEMA</c> states the schema's comment (G-160); only then is <see cref="Comment"/> compared.</summary>
    public bool IsCommentStated { get; init; }

    /// <summary>The schema's own name, so a script that creates objects in it creates it first.</summary>
    public string? SchemaName => Name;

    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The schema's comment; null when it states none.</summary>
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

public static class SchemaKeys
{
    /// <summary>The key of the definition for <paramref name="schemaName"/>. It is not <c>schema:name</c>, which names the operation that creates the schema.</summary>
    public static string Create(string schemaName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        return $"schema-comment:{schemaName.Trim()}";
    }
}
