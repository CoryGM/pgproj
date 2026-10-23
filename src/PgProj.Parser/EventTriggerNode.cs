namespace PgProj.Parser;

/// <summary>
/// A <c>CREATE EVENT TRIGGER</c> statement (G-127): a database-level trigger that runs a function on a DDL event
/// (<c>ddl_command_start</c>, <c>ddl_command_end</c>, <c>sql_drop</c>, <c>table_rewrite</c>, or PostgreSQL 17's <c>login</c>). It belongs to no
/// schema and to no table; only the function it runs is schema-qualified.
/// </summary>
public sealed record EventTriggerNode(string? Name) : SqlNode("EventTrigger", Name, null)
{
    /// <summary>The event the trigger fires on, as written (<c>ddl_command_end</c>).</summary>
    public string? EventName { get; init; }

    /// <summary>The command tags of <c>WHEN TAG IN (...)</c>, as written; empty when there is no filter.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The function the trigger runs, as written (<c>public.audit_ddl</c>), without its <c>()</c>.</summary>
    public string? FunctionName { get; init; }

    /// <summary>
    /// When the trigger fires, from <c>pg_event_trigger.evtenabled</c>: <c>O</c> enabled (the default), <c>D</c> disabled, <c>R</c> enabled for
    /// replica sessions only, <c>A</c> enabled always. <c>CREATE EVENT TRIGGER</c> has no clause for it; <c>ALTER EVENT TRIGGER</c> sets it
    /// (<see cref="AlterEventTriggerNode"/>).
    /// </summary>
    public string EnabledMode { get; init; } = "O";
}

/// <summary>
/// <c>ALTER EVENT TRIGGER name DISABLE|ENABLE [REPLICA|ALWAYS]</c> (G-127). A transient marker: <c>DeclarationPass</c> applies it to the event
/// trigger of that name, whichever file declares it, and warns when the project declares none.
/// </summary>
public sealed record AlterEventTriggerNode(string? Name, string EnabledMode) : SqlNode("AlterEventTrigger", Name, null);
