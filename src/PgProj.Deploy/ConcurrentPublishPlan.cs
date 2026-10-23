using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>
/// One statement of <c>publish --concurrently</c> that runs after the transaction commits, on its own (G-271): <c>CREATE INDEX CONCURRENTLY</c> and the like cannot run
/// in a transaction block. <see cref="CleanupSql"/>, when set, is run if it fails, so a half-built index is not left behind.
/// </summary>
/// <param name="Sql">The statement, with its semicolon.</param>
/// <param name="Description">What it does, for the progress line and a failure (<c>build index s.i concurrently</c>).</param>
/// <param name="CleanupSql">What undoes a failed <paramref name="Sql"/>: <c>DROP INDEX CONCURRENTLY IF EXISTS</c> of the index it was building.</param>
public sealed record AfterCommitStep(string Sql, string Description, string? CleanupSql = null);

/// <summary>
/// <c>publish --concurrently</c> (G-271, design approved with corrections on 2026-10-14): the script split into the part that runs in one transaction and the statements
/// that run after it commits, one at a time, so building an index or checking a constraint on a table that already exists does not block its writes for as long as it
/// takes.
/// <list type="bullet">
/// <item>A new index on such a table is built with <c>CREATE INDEX CONCURRENTLY</c>. On a partitioned table, where PostgreSQL refuses that, it is created <c>ON ONLY</c> the
/// parent in the transaction, then built concurrently on each partition and attached to it, partition by partition.</item>
/// <item>A changed plain index is built concurrently under a temporary name, the old one dropped concurrently, and the new one renamed.</item>
/// <item>A new foreign key or <c>CHECK</c> is added <c>NOT VALID</c> in the transaction and validated after it (a foreign key on a partitioned table stays in the
/// transaction before PostgreSQL 18, which is the first to accept <c>NOT VALID</c> for one).</item>
/// <item>A new unique constraint, and a primary key whose columns are already <c>NOT NULL</c>, are built as a unique index concurrently and then added
/// <c>USING INDEX</c>.</item>
/// </list>
/// Everything else stays in the transaction: a table the same publish creates is empty, an exclusion constraint has no <c>USING INDEX</c> form, a constraint's own
/// index cannot be dropped concurrently, and anything a foreign key in the transaction relies on must exist before it.
/// </summary>
public sealed record ConcurrentPublishPlan(OperationBatch Transactional, IReadOnlyList<AfterCommitStep> AfterCommit)
{
    /// <summary>The line a printed or written script puts between the two parts.</summary>
    public const string Marker = "-- pgproj --concurrently: the statements below run outside a transaction, one at a time";

    /// <summary>
    /// Splits <paramref name="batch"/>. <paramref name="existingRelations"/> is every relation name the database has in the schemas involved (schema, name), which an index
    /// built on a partition must not take; <paramref name="serverMajorVersion"/> decides whether a foreign key on a partitioned table can be added <c>NOT VALID</c>.
    /// </summary>
    public static ConcurrentPublishPlan Create(OperationBatch batch, DatabaseModel liveModel, int serverMajorVersion, IReadOnlySet<(string Schema, string Name)> existingRelations)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(liveModel);
        ArgumentNullException.ThrowIfNull(existingRelations);

        var operations = batch.Operations;
        var droppedTables = operations.OfType<DropOperation>().Select(static drop => drop.Node).OfType<TableDefinition>().Select(static table => TableId(table.SchemaName, table.Name))
            .Concat(operations.OfType<DropOperation>().Select(static drop => drop.Node).OfType<ViewDefinition>().Select(static view => TableId(view.SchemaName, view.Name)))
            .ToHashSet();
        // The columns of each table this publish drops or changes the type of: an index on one of them cannot be swapped in after the commit.
        var changedColumns = operations.OfType<AlterOperation>()
            .Where(static alter => alter.Change is { OldNode: TableDefinition, NewNode: TableDefinition })
            .ToDictionary(
                static alter => TableId(((TableDefinition)alter.Change.NewNode).SchemaName, ((TableDefinition)alter.Change.NewNode).Name),
                static alter => ChangedColumns((TableDefinition)alter.Change.OldNode, (TableDefinition)alter.Change.NewNode));
        var newTables = operations.OfType<CreateOperation>().Select(static create => create.Node).OfType<TableDefinition>().ToList();
        var liveTables = liveModel.Tables.Values.ToDictionary(static table => TableId(table.SchemaName, table.Name));
        var liveRelations = liveTables.Keys.Concat(liveModel.Views.Values.Where(static view => view.IsMaterialized).Select(static view => TableId(view.SchemaName, view.Name))).ToHashSet();

        // A table whose rows a foreign key in this publish refers to: its key or unique index must exist before that foreign key is added, so it stays in the transaction.
        var referencedByNewForeignKeys = operations.OfType<CreateOperation>().Select(static create => create.Node).OfType<ConstraintDefinition>()
            .Where(static constraint => IsType(constraint, "FOREIGN KEY") && constraint.ForeignKeyReferencedTable is not null)
            .Select(static constraint => TableId(constraint.ForeignKeyReferencedSchemaName ?? constraint.SchemaName, constraint.ForeignKeyReferencedTable!))
            .ToHashSet();
        var referencedByLiveForeignKeys = liveModel.Constraints.Values
            .Where(static constraint => IsType(constraint, "FOREIGN KEY") && constraint.ForeignKeyReferencedTable is not null)
            .Select(static constraint => TableId(constraint.ForeignKeyReferencedSchemaName ?? constraint.SchemaName, constraint.ForeignKeyReferencedTable!))
            .ToHashSet();

        bool IsExistingTable(string? schema, string? table)
            => table is not null && liveRelations.Contains(TableId(schema, table)) && !droppedTables.Contains(TableId(schema, table));

        var takenNames = new HashSet<(string, string)>(existingRelations);
        var dropsById = operations.OfType<DropOperation>().Where(static drop => drop.Node is IndexDefinition).ToDictionary(static drop => (drop.NodeId, drop.NodeKind));
        var moved = new HashSet<SqlOperation>();
        var replaced = new Dictionary<SqlOperation, SqlOperation>();
        var movedNodes = new HashSet<(string, ChangeNodeKind)>();
        var indexSteps = new List<AfterCommitStep>();
        var validationSteps = new List<AfterCommitStep>();
        var transactionalTail = new List<SqlOperation>();

        foreach (var operation in operations)
        {
            if (operation is not CreateOperation create)
            {
                continue;
            }

            switch (create.Node)
            {
                case IndexDefinition index when index.CopiedByLike is null && index.KeyElements.Count > 0 && IsExistingTable(index.SchemaName, index.TableName):
                {
                    dropsById.TryGetValue((create.NodeId, create.NodeKind), out var drop);
                    var table = TableId(index.SchemaName, index.TableName);
                    // A unique index a foreign key may rely on: one being added in the transaction needs it first, and an existing one cannot lose it for a moment.
                    if ((index.IsUnique && referencedByNewForeignKeys.Contains(table)) || (drop?.Node is IndexDefinition { IsUnique: true } && referencedByLiveForeignKeys.Contains(table)))
                    {
                        break;
                    }

                    if (liveTables.TryGetValue(table, out var liveTable) && liveTable.PartitionStrategy is not null)
                    {
                        // A changed index on a partitioned table is dropped in the transaction (its partitions' indexes go with it) and made again as a new one.
                        if (PartitionSteps(index, create.NodeId, liveModel, newTables, takenNames) is not { } partitionSteps)
                        {
                            break;
                        }

                        replaced[create] = create with { OnlyParent = true };
                        indexSteps.AddRange(partitionSteps.AfterCommit);
                        transactionalTail.AddRange(partitionSteps.InTransaction);
                        break;
                    }

                    if (drop is null)
                    {
                        moved.Add(create);
                        movedNodes.Add((create.NodeId, create.NodeKind));
                        indexSteps.AddRange(NewIndexSteps(index));
                        break;
                    }

                    // A changed index is replaced without a moment with neither: built under a temporary name, the old one dropped, the new one renamed. Not when the table
                    // changes too (a column the old index uses may be changing), and not for the index the table replicates by.
                    var oldIndex = (IndexDefinition)drop.Node;
                    if ((changedColumns.TryGetValue(table, out var changed) && UsesAny(oldIndex, changed)) || oldIndex.IsReplicaIdentity)
                    {
                        break;
                    }

                    moved.Add(create);
                    moved.Add(drop);
                    movedNodes.Add((create.NodeId, create.NodeKind));
                    indexSteps.AddRange(ChangedIndexSteps(oldIndex, index));
                    break;
                }

                case ConstraintDefinition constraint when constraint.CopiedByLike is null && IsExistingTable(constraint.SchemaName, constraint.TableName):
                {
                    var table = TableId(constraint.SchemaName, constraint.TableName);
                    var isPartitioned = liveTables.TryGetValue(table, out var liveTable) && liveTable.PartitionStrategy is not null;
                    if ((IsType(constraint, "FOREIGN KEY") || IsType(constraint, "CHECK")) && constraint.IsEnforced && !constraint.IsNotValid)
                    {
                        if (IsType(constraint, "FOREIGN KEY") && isPartitioned && serverMajorVersion < 18)
                        {
                            break;
                        }

                        replaced[create] = create with { Node = constraint with { IsNotValid = true } };
                        validationSteps.Add(new AfterCommitStep(
                            $"ALTER TABLE {Qualify(constraint.SchemaName, constraint.TableName!)} VALIDATE CONSTRAINT {Quote(constraint.Name)};",
                            $"validate constraint {constraint.Name} on {Display(constraint.SchemaName, constraint.TableName!)}"));
                        break;
                    }

                    if (IsKeyBuiltConcurrently(constraint, liveTable, isPartitioned, referencedByNewForeignKeys.Contains(table), dropsByConstraint: operations.OfType<DropOperation>().Any(drop => drop.NodeId == create.NodeId && drop.NodeKind == create.NodeKind)))
                    {
                        moved.Add(create);
                        movedNodes.Add((create.NodeId, create.NodeKind));
                        indexSteps.AddRange(KeySteps(constraint));
                    }

                    break;
                }
            }
        }

        if (moved.Count == 0 && replaced.Count == 0)
        {
            return new ConcurrentPublishPlan(batch, []);
        }

        // A statement that follows a moved object (the comment set on a new constraint) goes with it.
        var followers = operations.OfType<PreservedStatementOperation>().Where(operation => movedNodes.Contains((operation.NodeId, operation.NodeKind))).ToList();
        var followerSteps = followers.Select(static operation => new AfterCommitStep(operation.GenerateSql(), $"run {FirstLine(operation.GenerateSql())}"));

        var transactional = batch with
        {
            Operations =
            [
                .. operations.Where(operation => !moved.Contains(operation) && !followers.Contains(operation)).Select(operation => replaced.TryGetValue(operation, out var replacement) ? replacement : operation),
                .. transactionalTail,
            ],
        };
        return new ConcurrentPublishPlan(transactional, [.. indexSteps, .. followerSteps, .. validationSteps]);
    }

    /// <summary>The statements after the commit as a script: each on its own, with its semicolon, under <see cref="Marker"/>; empty when there are none.</summary>
    public string AfterCommitScript()
        => AfterCommit.Count == 0
            ? string.Empty
            : Marker + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, AfterCommit.Select(static step => step.Sql));

    private static IEnumerable<AfterCommitStep> NewIndexSteps(IndexDefinition index)
    {
        yield return new AfterCommitStep(
            CreateOperation.CreateIndexStatement(index, concurrently: true),
            $"build index {Display(index.SchemaName, index.Name)} concurrently",
            DropIndexConcurrently(index.SchemaName, index.Name));
        foreach (var step in IndexFollowUps(index, index.Name))
        {
            yield return step;
        }
    }

    private static IEnumerable<AfterCommitStep> ChangedIndexSteps(IndexDefinition oldIndex, IndexDefinition index)
    {
        var temporaryName = TemporaryName(index.Name);
        yield return new AfterCommitStep(
            CreateOperation.CreateIndexStatement(index, name: temporaryName, concurrently: true),
            $"build the new {Display(index.SchemaName, index.Name)} concurrently, as {temporaryName}",
            DropIndexConcurrently(index.SchemaName, temporaryName));
        yield return new AfterCommitStep(
            $"DROP INDEX CONCURRENTLY {Qualify(oldIndex.SchemaName, oldIndex.Name)};",
            $"drop the old {Display(oldIndex.SchemaName, oldIndex.Name)} concurrently",
            DropIndexConcurrently(index.SchemaName, temporaryName));
        yield return new AfterCommitStep(
            $"ALTER INDEX {Qualify(index.SchemaName, temporaryName)} RENAME TO {Quote(index.Name)};",
            $"rename {temporaryName} to {index.Name}");
        foreach (var step in IndexFollowUps(index, index.Name))
        {
            yield return step;
        }
    }

    /// <summary>
    /// The partitions' indexes of a new index on a partitioned table: on each partition the database has, built concurrently under the name PostgreSQL gives a clone and
    /// attached at once, so a failure leaves only attached indexes (dropped with the parent's) and none of its own. A partition this publish creates is empty: its index is
    /// made and attached at the end of the transaction (<c>IF NOT EXISTS</c>, since a partition created after the parent's index already has its clone). Null when it cannot
    /// be done so: a partition is itself partitioned, or an expression key cannot be named without its parsed tree.
    /// </summary>
    private static (List<AfterCommitStep> AfterCommit, List<SqlOperation> InTransaction)? PartitionSteps(
        IndexDefinition index, string nodeId, DatabaseModel liveModel, IReadOnlyList<TableDefinition> newTables, HashSet<(string, string)> takenNames)
    {
        var keyElements = index.DeclarationNode?.KeyElements is { Count: > 0 } declared && declared.Count == index.KeyElements.Count
            ? declared
            : index.KeyElements.All(static element => element.Name is not null)
                ? index.KeyElements.Select(static element => new IndexKeyElementNode(element.Name, null, null, null, [], false, null)).ToList()
                : null;
        if (keyElements is null)
        {
            return null;
        }

        var parent = TableId(index.SchemaName, index.TableName);
        var partitions = liveModel.Tables.Values
            .Where(table => table.PartitionOfTableName is not null && TableId(table.PartitionOfSchemaName ?? table.SchemaName, table.PartitionOfTableName) == parent)
            .OrderBy(static table => table.SchemaName, StringComparer.Ordinal)
            .ThenBy(static table => table.Name, StringComparer.Ordinal)
            .ToList();
        var newPartitions = newTables
            .Where(table => table.PartitionOfTableName is not null && TableId(table.PartitionOfSchemaName ?? table.SchemaName, table.PartitionOfTableName) == parent)
            .ToList();
        if (partitions.Concat(newPartitions).Any(static partition => partition.PartitionStrategy is not null))
        {
            return null;
        }

        var inTransaction = new List<SqlOperation>();
        foreach (var partition in newPartitions)
        {
            var schema = partition.SchemaName ?? "public";
            var name = GeneratedIndexNames.Choose(partition.Name, keyElements, index.IncludeColumns, candidate => takenNames.Contains((schema, candidate)));
            takenNames.Add((schema, name));
            var partitionIndex = index with { SchemaName = partition.SchemaName, TableName = partition.Name };
            var create = CreateOperation.CreateIndexStatement(partitionIndex, name: name).Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal)
                .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", StringComparison.Ordinal);
            inTransaction.Add(new PreservedStatementOperation { NodeId = nodeId, NodeKind = ChangeNodeKind.Index, Statement = create });
            inTransaction.Add(new PreservedStatementOperation
            {
                NodeId = nodeId,
                NodeKind = ChangeNodeKind.Index,
                Statement = $"ALTER INDEX {Qualify(index.SchemaName, index.Name)} ATTACH PARTITION {Qualify(partition.SchemaName, name)};",
            });
        }

        var steps = new List<AfterCommitStep>();
        foreach (var partition in partitions)
        {
            var schema = partition.SchemaName ?? "public";
            var name = GeneratedIndexNames.Choose(partition.Name, keyElements, index.IncludeColumns, candidate => takenNames.Contains((schema, candidate)));
            takenNames.Add((schema, name));
            var partitionIndex = index with { SchemaName = partition.SchemaName, TableName = partition.Name };
            steps.Add(new AfterCommitStep(
                CreateOperation.CreateIndexStatement(partitionIndex, name: name, concurrently: true),
                $"build index {Display(partition.SchemaName, name)} on partition {Display(partition.SchemaName, partition.Name)} concurrently",
                DropIndexConcurrently(partition.SchemaName, name)));
            steps.Add(new AfterCommitStep(
                $"ALTER INDEX {Qualify(index.SchemaName, index.Name)} ATTACH PARTITION {Qualify(partition.SchemaName, name)};",
                $"attach {Display(partition.SchemaName, name)} to {Display(index.SchemaName, index.Name)}"));
        }

        return (steps, inTransaction);
    }

    /// <summary>The columns of a table that a change drops or gives another type.</summary>
    private static HashSet<string> ChangedColumns(TableDefinition oldTable, TableDefinition newTable)
    {
        var newColumns = newTable.Columns.ToDictionary(static column => column.Name, StringComparer.Ordinal);
        return oldTable.Columns
            .Where(column => !newColumns.TryGetValue(column.Name, out var newColumn)
                || !string.Equals(column.GetComparableTypeSignature(), newColumn.GetComparableTypeSignature(), StringComparison.OrdinalIgnoreCase))
            .Select(static column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>True when <paramref name="index"/> may use one of <paramref name="columns"/>: it names one, or it has an expression or a predicate, which could.</summary>
    private static bool UsesAny(IndexDefinition index, HashSet<string> columns)
        => columns.Count > 0
            && (index.KeyElements.Any(static element => element.Name is null)
                || !string.IsNullOrWhiteSpace(index.RawPredicateExpression)
                || index.KeyElements.Select(static element => element.Name!).Concat(index.KeyColumns).Concat(index.IncludeColumns).Any(columns.Contains));

    private static bool IsKeyBuiltConcurrently(ConstraintDefinition constraint, TableDefinition? liveTable, bool isPartitioned, bool referencedByNewForeignKey, bool dropsByConstraint)
    {
        if (isPartitioned || referencedByNewForeignKey || dropsByConstraint || constraint.WithoutOverlaps || liveTable is null)
        {
            return false;
        }

        if (IsType(constraint, "UNIQUE"))
        {
            return constraint.UniqueColumns.Count > 0;
        }

        // USING INDEX makes a primary key's columns NOT NULL, which scans the table under ACCESS EXCLUSIVE; only columns that already are NOT NULL avoid it.
        return IsType(constraint, "PRIMARY KEY")
            && constraint.PrimaryKeyColumns.Count > 0
            && constraint.PrimaryKeyColumns.All(column => liveTable.Columns.Any(live => string.Equals(live.Name, column, StringComparison.Ordinal) && !live.AllowsNull));
    }

    private static IEnumerable<AfterCommitStep> KeySteps(ConstraintDefinition constraint)
    {
        var table = constraint.TableName!;
        var isPrimaryKey = IsType(constraint, "PRIMARY KEY");
        var columns = isPrimaryKey ? constraint.PrimaryKeyColumns : constraint.UniqueColumns;
        var include = CreateOperation.BuildIncludeClause(constraint.IncludeColumns);
        var nulls = constraint.NullsNotDistinct ? " NULLS NOT DISTINCT" : string.Empty;
        yield return new AfterCommitStep(
            $"CREATE UNIQUE INDEX CONCURRENTLY {Quote(constraint.Name)} ON {Qualify(constraint.SchemaName, table)} ({string.Join(", ", columns.Select(Quote))}){include}{nulls}{CreateOperation.BuildIndexStorageClause(constraint.IndexStorageOptions)};",
            $"build the index of {(isPrimaryKey ? "primary key" : "unique constraint")} {constraint.Name} on {Display(constraint.SchemaName, table)} concurrently",
            DropIndexConcurrently(constraint.SchemaName, constraint.Name));
        var deferrable = constraint.IsDeferrable ? (constraint.IsInitiallyDeferred == true ? " DEFERRABLE INITIALLY DEFERRED" : " DEFERRABLE INITIALLY IMMEDIATE") : string.Empty;
        yield return new AfterCommitStep(
            $"ALTER TABLE {Qualify(constraint.SchemaName, table)} ADD CONSTRAINT {Quote(constraint.Name)} {(isPrimaryKey ? "PRIMARY KEY" : "UNIQUE")} USING INDEX {Quote(constraint.Name)}{deferrable};",
            $"add {(isPrimaryKey ? "primary key" : "unique constraint")} {constraint.Name} using its index",
            DropIndexConcurrently(constraint.SchemaName, constraint.Name));
        foreach (var flag in Flags(constraint.SchemaName, table, constraint.Name, constraint.IsClustered, constraint.IsReplicaIdentity))
        {
            yield return flag;
        }
    }

    private static IEnumerable<AfterCommitStep> IndexFollowUps(IndexDefinition index, string name)
    {
        if (CommentSqlHelper.GenerateIndexComment(index.SchemaName, name, index.Comment) is { } comment)
        {
            yield return new AfterCommitStep(comment, $"comment on index {Display(index.SchemaName, name)}");
        }

        foreach (var flag in Flags(index.SchemaName, index.TableName!, name, index.IsClustered, index.IsReplicaIdentity))
        {
            yield return flag;
        }
    }

    private static IEnumerable<AfterCommitStep> Flags(string? schema, string table, string index, bool isClustered, bool isReplicaIdentity)
    {
        if (isClustered)
        {
            yield return new AfterCommitStep(CommentSqlHelper.GenerateClusterOn(schema, table, index), $"cluster {Display(schema, table)} on {index}");
        }

        if (isReplicaIdentity)
        {
            yield return new AfterCommitStep(CommentSqlHelper.GenerateReplicaIdentityUsingIndex(schema, table, index), $"set the replica identity of {Display(schema, table)} to {index}");
        }
    }

    /// <summary>The name a changed index is built under before the old one is dropped: its own name, clipped to fit 63 bytes, and <c>_pgproj_new</c>.</summary>
    public static string TemporaryName(string name)
    {
        const string Suffix = "_pgproj_new";
        var limit = 63 - Suffix.Length;
        var clipped = name;
        while (System.Text.Encoding.UTF8.GetByteCount(clipped) > limit)
        {
            clipped = clipped[..^1];
        }

        return clipped + Suffix;
    }

    private static string DropIndexConcurrently(string? schema, string name) => $"DROP INDEX CONCURRENTLY IF EXISTS {Qualify(schema, name)};";

    private static bool IsType(ConstraintDefinition constraint, string type)
        => string.Equals(constraint.ConstraintType?.Trim(), type, StringComparison.OrdinalIgnoreCase);

    private static (string, string) TableId(string? schema, string? name) => (schema ?? "public", name ?? string.Empty);

    private static string FirstLine(string sql)
    {
        var line = sql.Split('\n')[0].Trim();
        return line.Length > 80 ? line[..80] + "..." : line;
    }

    private static string Display(string? schema, string name) => $"{schema ?? "public"}.{name}";

    private static string Qualify(string? schema, string name) => schema is null ? Quote(name) : $"{Quote(schema)}.{Quote(name)}";

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
