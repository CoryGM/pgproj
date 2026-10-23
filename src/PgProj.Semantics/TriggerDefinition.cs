using PgProj.Parser;

namespace PgProj.Semantics;

public sealed record TriggerDefinition(
    string Key,
    string Name,
    string? SchemaName,
    string? TableName) : ISemanticNode
{
    public string Kind => "Trigger";

    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The trigger's comment (G-163); null when it has none.</summary>
    public string? Comment { get; init; }

    /// <summary>
    /// True when the model states the comment: a project has a <c>COMMENT ON TRIGGER</c> for it (<c>IS NULL</c> states that it has none), a live database has one when the trigger has a comment. A comment is
    /// compared only for a trigger whose comment is stated, so a database's comment on a trigger the project does not mention is left alone (G-160, G-161).
    /// </summary>
    public bool CommentStated { get; init; }

    public string? FunctionName { get; init; }

    /// <summary>
    /// The trigger's firing mode: <c>ENABLE</c>, <c>DISABLE</c>, <c>ENABLE REPLICA</c>, or <c>ENABLE ALWAYS</c> (G-257). A project states it with <c>ALTER TABLE ... TRIGGER</c>; a live trigger has it
    /// when it is not the default (enabled), so null means enabled. Compared only when the project states one: a trigger the project says nothing about keeps whatever the database has.
    /// </summary>
    public string? EnabledState { get; init; }

    /// <summary>
    /// The firing mode of the trigger's copy on a partition, by the partition's name (<c>schema.table</c>), where the model states one (G-293). A project states one with <c>ALTER TABLE partition ... TRIGGER</c>;
    /// a live partition has one when its copy is not enabled or differs from its parent's copy. Compared only for the partitions the project states: the others keep what the database has.
    /// </summary>
    public IReadOnlyDictionary<string, string> PartitionStates { get; init; } = ColumnCommentMap.Empty;

    public string? Timing { get; init; }

    public IReadOnlyList<string> Events { get; init; } = [];

    public bool IsRowLevel { get; init; }

    /// <summary>
    /// The complete CREATE TRIGGER statement exactly as the user wrote it.
    /// Deploy-only state, excluded from diffing.
    /// </summary>
    public string? RawStatement { get; init; }

    /// <summary>True for a constraint trigger (<c>CREATE CONSTRAINT TRIGGER</c>) (G-40).</summary>
    public bool IsConstraintTrigger { get; init; }

    /// <summary>True when a constraint trigger is <c>DEFERRABLE</c> (G-40).</summary>
    public bool IsDeferrable { get; init; }

    /// <summary>True when a constraint trigger is <c>INITIALLY DEFERRED</c> (G-40).</summary>
    public bool IsInitiallyDeferred { get; init; }

    /// <summary>The table of a constraint trigger's <c>FROM</c> clause, canonicalized like other names (G-40).</summary>
    public string? ConstraintTableName { get; init; }

    /// <summary>
    /// The columns of <c>UPDATE OF</c>. An order-insensitive set: <c>SemanticDiffNormalizationPass</c> sorts it, since PostgreSQL
    /// reports the columns in attribute order whatever order they were written in (G-40).
    /// </summary>
    public IReadOnlyList<string> UpdateColumns { get; init; } = [];

    /// <summary>The literal arguments passed to the trigger function. Order-sensitive: the function receives them by position (G-40).</summary>
    public IReadOnlyList<string> FunctionArguments { get; init; } = [];

    /// <summary>The name given to <c>REFERENCING OLD TABLE AS</c> (G-40).</summary>
    public string? OldTransitionTable { get; init; }

    /// <summary>The name given to <c>REFERENCING NEW TABLE AS</c> (G-40).</summary>
    public string? NewTransitionTable { get; init; }

    /// <summary>The <c>WHEN</c> condition as written, kept beside <see cref="NormalizedWhenCondition"/> because normalization is lossy (G-40).</summary>
    public string? RawWhenCondition { get; init; }

    /// <summary>
    /// The <c>WHEN</c> condition in the form the diff compares: a structural form that ignores whitespace, case, and parentheses and
    /// undoes what PostgreSQL rewrites when it deparses a stored condition (see <c>TriggerWhenConditionCanonicalizer</c>) (G-40).
    /// </summary>
    public string? NormalizedWhenCondition { get; init; }

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
