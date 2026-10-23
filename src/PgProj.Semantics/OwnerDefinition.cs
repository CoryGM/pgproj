using System.Text.Json.Serialization;
using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// The owner of one object (G-146): a project states it with <c>ALTER ... OWNER TO</c> (or <c>CREATE SCHEMA ... AUTHORIZATION</c>), a live database
/// has one for every object. Its identity is the object, never the role, so a project that names one owner and a database that has another are
/// <em>one</em> node that differs, and the change is an <c>ALTER ... OWNER TO</c> in place (<see cref="OwnerKeys.Create"/>). A project states an
/// owner for the objects it chooses to; an owner the database has and the project does not state is never compared.
/// </summary>
public sealed record OwnerDefinition(string Key, string Name) : ISemanticNode
{
    public string Kind => "Owner";

    /// <summary>The schema of the object the owner is of; null for a schema, an event trigger, and a publication.</summary>
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

    /// <summary>
    /// What kind of object this is the owner of: <c>schema</c>, <c>table</c>, <c>view</c>, <c>materialized view</c>, <c>sequence</c>,
    /// <c>function</c>, <c>procedure</c>, <c>aggregate</c>, <c>operator</c>, <c>type</c>, <c>domain</c>, <c>event trigger</c>, or <c>publication</c>.
    /// </summary>
    public string ObjectKind { get; init; } = string.Empty;

    /// <summary>The key of the object in the model (a schema's is its name), which is also its id in the dependency graph, so the owner is set after the object is created.</summary>
    public string ObjectKey { get; init; } = string.Empty;

    /// <summary>The name of the object, as written in the SQL that sets its owner; for a schema, the schema.</summary>
    public string ObjectName { get; init; } = string.Empty;

    /// <summary>The role that owns the object, as written. Compared exactly, as PostgreSQL compares role names (G-206).</summary>
    public string OwnerRole { get; init; } = string.Empty;

    /// <summary>
    /// The object's definition in the model, which the SQL that sets its owner is written from (a routine's argument types, an operator's operands).
    /// Null for a schema, which has none. Not part of the dump of the model.
    /// </summary>
    [JsonIgnore]
    public ISemanticNode? Target { get; init; }

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

/// <summary>The identity of an owner node (G-146).</summary>
public static class OwnerKeys
{
    /// <summary><c>owner:kind:objectKey</c>: the owner of that object, whoever it is. A key that already starts with its kind (an event trigger's, a publication's) is not given it twice.</summary>
    public static string Create(string objectKind, string objectKey)
        => objectKey.StartsWith(objectKind + ":", StringComparison.Ordinal) ? $"owner:{objectKey}" : $"owner:{objectKind}:{objectKey}";
}
