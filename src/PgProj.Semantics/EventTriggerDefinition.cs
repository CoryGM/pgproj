using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// A <c>CREATE EVENT TRIGGER</c> (G-127): a database-level trigger that runs a function on a DDL event. It has no schema and no table, so its
/// identity is its name alone (<see cref="DatabaseModel.CreateEventTriggerKey"/>); the function it runs is a dependency, created first.
/// </summary>
public sealed record EventTriggerDefinition(string Key, string Name) : ISemanticNode
{
    public string Kind => "EventTrigger";

    public string? SchemaName => null;

    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    /// <summary>The function the trigger runs, so the dependency graph creates it first and drops it last.</summary>
    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The event it fires on (<c>ddl_command_end</c>), lower case: PostgreSQL's event names are keywords.</summary>
    public string? EventName { get; init; }

    /// <summary>The command tags of <c>WHEN TAG IN (...)</c> as written; empty for no filter. <see cref="NormalizedTags"/> is the comparison form.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// <see cref="Tags"/> as a set, upper case: PostgreSQL stores the filter upper-cased and unordered, so <c>('create table', 'ALTER TABLE')</c> and
    /// <c>('ALTER TABLE', 'CREATE TABLE')</c> are one filter. Empty for no filter.
    /// </summary>
    public IReadOnlyList<string> NormalizedTags { get; init; } = [];

    /// <summary>The function it runs as written (<c>public.audit_ddl</c>), without its <c>()</c>.</summary>
    public string? FunctionName { get; init; }

    /// <summary>When it fires: <c>O</c> enabled (the default), <c>D</c> disabled, <c>R</c> replica sessions only, <c>A</c> always (<c>evtenabled</c>).</summary>
    public string EnabledMode { get; init; } = "O";

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
