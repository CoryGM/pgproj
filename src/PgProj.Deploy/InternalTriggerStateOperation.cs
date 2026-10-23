using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>
/// <c>ALTER TABLE t { ENABLE | DISABLE } TRIGGER ALL</c> for the table's internal (foreign-key) triggers (G-261). Those triggers exist only once every foreign key
/// that involves the table has been created, and a statement that names <c>ALL</c> also reaches the triggers the project declares, so it is written at the end of
/// the script, after every table, key, and trigger, and the declared triggers' own states are written again after it. Like <see cref="SequenceOwnershipOperation"/> it
/// is synthesized rather than derived from a change of its own. It needs a superuser (<c>SuperuserStatements</c>).
/// </summary>
public sealed record class InternalTriggerStateOperation : SqlOperation
{
    public required TableDefinition Table { get; init; }

    /// <summary>The declared triggers of the table with a state of their own, which <c>ALL</c> overwrote and are written again (a state other than <c>ENABLE</c>, or any when the table's is <c>DISABLE</c>).</summary>
    public IReadOnlyList<TriggerDefinition> Triggers { get; init; } = [];

    public override string GenerateSql()
    {
        var statements = new List<string> { CommentSqlHelper.GenerateTriggerAllState(Table.SchemaName, Table.Name, Table.InternalTriggerState) };
        statements.AddRange(Triggers.Select(trigger => CommentSqlHelper.GenerateTriggerState(trigger.SchemaName, trigger.TableName ?? Table.Name, trigger.Name, trigger.EnabledState)));
        return string.Join("\n", statements);
    }
}
