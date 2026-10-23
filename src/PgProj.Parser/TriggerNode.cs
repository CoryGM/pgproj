namespace PgProj.Parser;

public sealed record TriggerNode(
    string? Name,
    string? SchemaName,
    string? TableName) : SqlNode("Trigger", Name, SchemaName)
{
    /// <summary>
    /// The fully qualified function invoked by the trigger.
    /// </summary>
    public string? FunctionName { get; init; }

    /// <summary>
    /// The trigger's firing mode when the project states one with <c>ALTER TABLE ... { ENABLE | DISABLE | ENABLE REPLICA | ENABLE ALWAYS } TRIGGER</c> (G-257); null when it states none.
    /// </summary>
    public string? EnabledState { get; init; }

    /// <summary>
    /// The firing mode of this trigger's copy on a partition (or sub-partition), where the project states one with <c>ALTER TABLE partition { ENABLE | DISABLE | ... } TRIGGER name</c> (G-293).
    /// PostgreSQL clones a row trigger of a partitioned table onto each partition, and each copy has a mode of its own.
    /// </summary>
    public IReadOnlyList<PartitionTriggerState> PartitionStates { get; init; } = [];

    /// <summary>
    /// The trigger timing keyword, such as <c>BEFORE</c>, <c>AFTER</c>, or <c>INSTEAD OF</c>.
    /// </summary>
    public string? Timing { get; init; }

    /// <summary>
    /// The events the trigger fires on, such as <c>INSERT</c> or <c>UPDATE</c>.
    /// </summary>
    public IReadOnlyList<string> Events { get; init; } = [];

    /// <summary>
    /// True when the trigger fires once per row rather than per statement.
    /// </summary>
    public bool IsRowLevel { get; init; }

    /// <summary>
    /// The complete <c>CREATE TRIGGER</c> statement exactly as the user wrote it,
    /// preserved verbatim for deployment.
    /// </summary>
    public string? RawStatement { get; init; }

    /// <summary>True for <c>CREATE CONSTRAINT TRIGGER</c> (G-40).</summary>
    public bool IsConstraintTrigger { get; init; }

    /// <summary>True when a constraint trigger is <c>DEFERRABLE</c> (G-40).</summary>
    public bool IsDeferrable { get; init; }

    /// <summary>True when a constraint trigger is <c>INITIALLY DEFERRED</c> (G-40).</summary>
    public bool IsInitiallyDeferred { get; init; }

    /// <summary>The table named by a constraint trigger's <c>FROM</c> clause, as <c>schema.table</c> or <c>table</c> (G-40).</summary>
    public string? ConstraintTableName { get; init; }

    /// <summary>The columns of <c>UPDATE OF a, b</c>, in the order written; empty when the trigger fires on any update (G-40).</summary>
    public IReadOnlyList<string> UpdateColumns { get; init; } = [];

    /// <summary>The literal arguments passed to the trigger function, in order, as PostgreSQL stores them (all strings) (G-40).</summary>
    public IReadOnlyList<string> FunctionArguments { get; init; } = [];

    /// <summary>The name given to <c>REFERENCING OLD TABLE AS name</c> (G-40).</summary>
    public string? OldTransitionTable { get; init; }

    /// <summary>The name given to <c>REFERENCING NEW TABLE AS name</c> (G-40).</summary>
    public string? NewTransitionTable { get; init; }

    /// <summary>
    /// The <c>WHEN (...)</c> condition exactly as written, without the outer parentheses of the clause; null when there is none.
    /// Deploy uses <see cref="RawStatement"/>; this is kept so a canonical comparison form never replaces what was written (G-40).
    /// </summary>
    public string? RawWhenCondition { get; init; }

    /// <summary>
    /// The pg_query JSON of the <c>WHEN</c> expression, which <see cref="TriggerWhenConditionCanonicalizer"/> turns into a form
    /// that is equal for spellings PostgreSQL treats as the same condition (G-40).
    /// </summary>
    public string? WhenConditionAst { get; init; }
}

/// <summary>
/// The firing mode a project states for a trigger's copy on one partition (G-293): <c>ENABLE</c>, <c>DISABLE</c>, <c>ENABLE REPLICA</c>, or <c>ENABLE ALWAYS</c>. <see cref="Depth"/> is how many partitions
/// down from the table that declares the trigger it is: PostgreSQL sets a mode on the partitions below a table too, so a parent's is written before its children's.
/// </summary>
public sealed record PartitionTriggerState(string? SchemaName, string TableName, string State, int Depth = 1);
