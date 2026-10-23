using Npgsql;
using PgProj.Deploy;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>The server's shared lock table: <c>max_locks_per_transaction * (max_connections + max_prepared_transactions)</c> locks (G-280).</summary>
internal sealed record LockTableSize(int MaxLocksPerTransaction, int MaxConnections, int MaxPreparedTransactions)
{
    public int Locks => MaxLocksPerTransaction * (MaxConnections + MaxPreparedTransactions);

    /// <summary>The <c>max_locks_per_transaction</c> that gives <paramref name="locks"/> a quarter of the table to spare, since other sessions share it.</summary>
    public int SettingFor(int locks)
        => (int)Math.Ceiling(locks * 1.25 / Math.Max(1, MaxConnections + MaxPreparedTransactions));

    public string Formula => $"max_locks_per_transaction {MaxLocksPerTransaction} * (max_connections {MaxConnections} + max_prepared_transactions {MaxPreparedTransactions}) = {Locks:N0}";
}

/// <summary>
/// Whether a script fits the server's lock table (G-280, option (b), the user's decision of 2026-10-13). <c>publish --execute</c> runs the script in one transaction, and
/// every relation it creates, alters, or drops stays locked until the commit; a script that needs more locks than the server's shared lock table holds fails partway with
/// <c>out of shared memory</c> and rolls back (GitLab's schema, 8,943 relations, into a server with the defaults). The count is an estimate, on the generous side: one lock
/// per relation the script creates, alters, or drops (a table and each partition, an index, a sequence, a view, a materialized view, the TOAST table and index of a new
/// table or materialized view), an index's copy on each partition of a partitioned table, and the existing table a new foreign key references. <c>publish</c> refuses over
/// the limit unless <c>--allow-lock-limit</c>, and warns from <see cref="WarningShare"/> of it; a printed or written script is only warned about.
/// </summary>
internal static class LockCapacity
{
    /// <summary>The option that lets <c>--execute</c> run a script over the limit (G-280).</summary>
    public const string OptionName = "--allow-lock-limit";

    /// <summary>The share of the lock table from which <c>publish</c> warns: the table is shared with every other session on the server.</summary>
    public const double WarningShare = 0.8;

    /// <summary>The locks <paramref name="batch"/> is estimated to hold at its commit.</summary>
    public static int Estimate(OperationBatch batch, DatabaseModel live, DatabaseModel project)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(project);

        var relations = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var operation in batch.Operations)
        {
            switch (operation)
            {
                case CreateOperation { Node: var node }:
                    AddCreated(relations, node, project);
                    break;
                case DropOperation { Node: var node }:
                    AddDropped(relations, node, live);
                    break;
                case AlterOperation { Change: { NewNode: var node } }:
                    if (IsRelation(node))
                    {
                        relations.Add(node.Key);
                    }

                    break;
            }
        }

        return relations.Count;
    }

    /// <summary>The server's lock table size.</summary>
    public static async Task<LockTableSize> ReadAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand(
            "SELECT current_setting('max_locks_per_transaction')::int, current_setting('max_connections')::int, current_setting('max_prepared_transactions')::int",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new LockTableSize(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    /// <summary>True when <paramref name="locks"/> is more than the lock table holds.</summary>
    public static bool IsOver(int locks, LockTableSize size) => locks > size.Locks;

    /// <summary>True when <paramref name="locks"/> is at least <see cref="WarningShare"/> of the lock table.</summary>
    public static bool IsNear(int locks, LockTableSize size) => locks >= size.Locks * WarningShare;

    /// <summary>The message for a script over the limit or near it: the estimate, the server's sizes, and the setting that would fit.</summary>
    public static string Describe(int locks, LockTableSize size)
    {
        ArgumentNullException.ThrowIfNull(size);

        var share = size.Locks == 0 ? 0 : (int)Math.Round(100.0 * locks / size.Locks);
        var what = IsOver(locks, size)
            ? $"more than the server's lock table holds ({size.Formula}), so it would fail partway with \"out of shared memory\" and roll back"
            : $"{share}% of the server's lock table ({size.Formula}), which other sessions share";
        return $"the script holds about {locks:N0} locks until it commits, {what}. Raise max_locks_per_transaction to at least {size.SettingFor(locks):N0} "
            + $"(it takes a server restart), or publish in smaller steps.";
    }

    private static void AddCreated(HashSet<string> relations, ISemanticNode node, DatabaseModel project)
    {
        switch (node)
        {
            case TableDefinition table:
                AddWithToast(relations, table.Key);
                break;
            case ViewDefinition { IsMaterialized: true } view:
                AddWithToast(relations, view.Key);
                break;
            case ViewDefinition or SequenceDefinition:
                relations.Add(node.Key);
                break;
            case IndexDefinition index:
                AddIndex(relations, index.Key, index.SchemaName, index.TableName, project);
                break;
            case ConstraintDefinition constraint when HasIndex(constraint):
                AddIndex(relations, constraint.Key, constraint.SchemaName, constraint.TableName, project);
                break;
            case ConstraintDefinition foreignKey when string.Equals(foreignKey.ConstraintType, "foreign key", StringComparison.OrdinalIgnoreCase):
                relations.Add(DatabaseModel.CreateKey(foreignKey.SchemaName, foreignKey.TableName ?? string.Empty));
                if (foreignKey.ForeignKeyReferencedTable is { } referenced)
                {
                    var referencedKey = DatabaseModel.CreateKey(foreignKey.ForeignKeyReferencedSchemaName ?? foreignKey.SchemaName, referenced);
                    relations.Add(referencedKey);
                    foreach (var partition in Partitions(project, referencedKey))
                    {
                        relations.Add(partition);
                    }
                }

                break;
        }
    }

    private static void AddDropped(HashSet<string> relations, ISemanticNode node, DatabaseModel live)
    {
        switch (node)
        {
            case TableDefinition table:
                AddWithToast(relations, table.Key);
                foreach (var index in live.Indexes.Values.Where(index => SameTable(index.SchemaName, index.TableName, table.Key)))
                {
                    relations.Add(index.Key);
                }

                break;
            case ViewDefinition { IsMaterialized: true } view:
                AddWithToast(relations, view.Key);
                break;
            case ViewDefinition or SequenceDefinition or IndexDefinition:
                relations.Add(node.Key);
                break;
            case ConstraintDefinition constraint when HasIndex(constraint):
                relations.Add(constraint.Key);
                relations.Add(DatabaseModel.CreateKey(constraint.SchemaName, constraint.TableName ?? string.Empty));
                break;
        }
    }

    /// <summary>An index (or a key's index) and its table; on a partitioned table, PostgreSQL makes and locks a copy on every partition.</summary>
    private static void AddIndex(HashSet<string> relations, string key, string? schemaName, string? tableName, DatabaseModel project)
    {
        relations.Add(key);
        var tableKey = DatabaseModel.CreateKey(schemaName, tableName ?? string.Empty);
        relations.Add(tableKey);
        foreach (var partition in Partitions(project, tableKey))
        {
            relations.Add(partition);
            relations.Add(partition + "#index:" + key);
        }
    }

    /// <summary>Every partition below <paramref name="tableKey"/>, at any depth.</summary>
    private static IEnumerable<string> Partitions(DatabaseModel model, string tableKey)
    {
        var pending = new Queue<string>([tableKey]);
        var seen = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer) { tableKey };
        while (pending.TryDequeue(out var parent))
        {
            foreach (var partition in model.Tables.Values.Where(table => table.PartitionOfTableName is not null
                && SameTable(table.PartitionOfSchemaName, table.PartitionOfTableName, parent)))
            {
                if (seen.Add(partition.Key))
                {
                    pending.Enqueue(partition.Key);
                    yield return partition.Key;
                }
            }
        }
    }

    private static void AddWithToast(HashSet<string> relations, string key)
    {
        relations.Add(key);
        relations.Add(key + "#toast");
        relations.Add(key + "#toast-index");
    }

    private static bool SameTable(string? schemaName, string? tableName, string tableKey)
        => tableName is not null && PgProj.Parser.SqlIdentifiers.NameComparer.Equals(DatabaseModel.CreateKey(schemaName, tableName), tableKey);

    /// <summary>A primary key, unique, or exclusion constraint, which has an index of its own (the model keeps the type in lower case).</summary>
    private static bool HasIndex(ConstraintDefinition constraint)
        => constraint.ConstraintType?.ToLowerInvariant() is "primary key" or "unique" or "exclude" or "exclusion";

    private static bool IsRelation(ISemanticNode node) => node is TableDefinition or ViewDefinition or SequenceDefinition or IndexDefinition;
}
