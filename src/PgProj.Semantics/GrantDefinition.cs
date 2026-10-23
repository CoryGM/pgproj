namespace PgProj.Semantics;

public sealed record GrantDefinition(
    string Key,
    string Name,
    string? SchemaName,
    string? ObjectKey,
    string? Grantee,
    IReadOnlyList<string> Privileges,
    IReadOnlyList<string> ReferencedTables,
    IReadOnlyList<string> ReferencedColumns,
    IReadOnlyList<string> ReferencedTypes,
    IReadOnlyList<string> ReferencedFunctions,
    IReadOnlyList<TableDefinition> ResolvedTables,
    IReadOnlyList<ColumnDefinition> ResolvedColumns,
    IReadOnlyList<TypeDefinition> ResolvedTypes,
    IReadOnlyList<FunctionDefinition> ResolvedFunctions) : ISemanticNode
{
    public string Kind => "Grant";

    public bool IsRecovered { get; set; }

    public bool HasErrors { get; set; }

    /// <summary>
    /// For a grant on one named object (<c>table:</c>, <c>sequence:</c>, <c>function:</c>, <c>procedure:</c>), the object's name
    /// without its schema; a routine carries its canonical argument types, as in <c>f(int4,text)</c> (G-65). Null for the other
    /// forms, whose <see cref="ObjectKey"/> already says everything.
    /// </summary>
    public string? ObjectName { get; init; }

    /// <summary>
    /// For a routine grant, the argument types as the statement spelled them (<c>pg_catalog.int4</c>, <c>text[]</c>): what the
    /// <c>GRANT</c> is written with. <see cref="ObjectName"/> holds their canonical form, which is for comparison only.
    /// </summary>
    public IReadOnlyList<string> ObjectArgumentTypes { get; init; } = [];

    /// <summary>The columns a column-level privilege is limited to, sorted; empty for a privilege on the whole object.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary><c>WITH GRANT OPTION</c>; for role membership (<c>role:</c>), <c>WITH ADMIN OPTION</c>.</summary>
    public bool WithGrantOption { get; init; }

    /// <summary>The <c>FOR ROLE</c> of <c>ALTER DEFAULT PRIVILEGES</c>; null when the statement names none.</summary>
    public string? ForRole { get; init; }

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
