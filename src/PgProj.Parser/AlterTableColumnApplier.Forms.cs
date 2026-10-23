namespace PgProj.Parser;

/// <summary>
/// The <c>ALTER</c> forms <c>pg_dump</c> writes for what <c>CREATE TABLE</c> could say (G-236): a column's default, identity, storage and compression, a table's
/// <c>ATTACH PARTITION</c>, and a sequence's <c>OWNED BY</c>. Each is folded into the node it changes before the semantic passes run, like <c>ADD COLUMN</c> (G-47), so the model
/// sees what <c>CREATE TABLE</c> or <c>CREATE SEQUENCE</c> would have made.
/// </summary>
public static partial class AlterTableColumnApplier
{
    private static void ApplyColumnChange(
        string? tableName,
        string? schemaName,
        string columnName,
        string action,
        int treeIndex,
        Dictionary<string, (int TreeIndex, int NodeIndex)> tables,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree,
        Func<ColumnNode, ColumnNode> change)
    {
        var target = Describe(schemaName, tableName ?? string.Empty);
        if (tableName is null || !tables.TryGetValue(Key(schemaName, tableName), out var location))
        {
            diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Warning,
                $"ALTER TABLE {target} ALTER COLUMN {columnName} {action}: the project does not declare that table, so the action is ignored.",
                DiagnosticSource));
            return;
        }

        var table = (TableNode)nodesByTree[location.TreeIndex][location.NodeIndex];
        var index = table.Columns.ToList().FindIndex(column => string.Equals(column.Name, columnName, StringComparison.Ordinal));
        if (index < 0)
        {
            diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Warning,
                $"ALTER TABLE {target} ALTER COLUMN {columnName} {action}: the project does not declare that column, so the action is ignored.",
                DiagnosticSource));
            return;
        }

        var columns = table.Columns.ToArray();
        columns[index] = change(columns[index]);
        nodesByTree[location.TreeIndex][location.NodeIndex] = table with { Columns = columns };
    }

    private static void ApplyAttachPartition(
        AlterTableAttachPartitionNode attach,
        int treeIndex,
        Dictionary<string, (int TreeIndex, int NodeIndex)> tables,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree)
    {
        var parent = Describe(attach.SchemaName, attach.TableName ?? string.Empty);
        var child = Describe(attach.ChildSchemaName, attach.ChildName);
        if (!tables.TryGetValue(Key(attach.ChildSchemaName, attach.ChildName), out var location))
        {
            diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Warning,
                $"ALTER TABLE {parent} ATTACH PARTITION {child}: the project does not declare that table, so the action is ignored.",
                DiagnosticSource));
            return;
        }

        var table = (TableNode)nodesByTree[location.TreeIndex][location.NodeIndex];
        nodesByTree[location.TreeIndex][location.NodeIndex] = table with
        {
            PartitionOfSchemaName = attach.SchemaName,
            IsPartitionOfSchemaDefaulted = attach.IsSchemaDefaulted,
            PartitionOfTableName = attach.TableName,
            PartitionBoundRaw = attach.BoundRaw,
        };
    }

    private static void ApplySequenceOwnedBy(
        AlterSequenceOwnedByNode ownedBy,
        int treeIndex,
        Dictionary<string, (int TreeIndex, int NodeIndex)> sequences,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree)
    {
        if (ownedBy.Name is null || !sequences.TryGetValue(Key(ownedBy.SchemaName, ownedBy.Name), out var location))
        {
            diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Warning,
                $"ALTER SEQUENCE {Describe(ownedBy.SchemaName, ownedBy.Name ?? string.Empty)} OWNED BY: the project does not declare that sequence, so the action is ignored.",
                DiagnosticSource));
            return;
        }

        var sequence = (SequenceNode)nodesByTree[location.TreeIndex][location.NodeIndex];
        var options = new Dictionary<string, string>(sequence.Options ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        {
            ["owned_by"] = ownedBy.OwnedBy,
        };
        nodesByTree[location.TreeIndex][location.NodeIndex] = sequence with { Options = options };
    }

    /// <summary>
    /// <c>ALTER INDEX parent_idx ATTACH PARTITION child_idx</c> (G-236): the partition's own index (or key constraint) is the parent's, which PostgreSQL makes on every partition, so the
    /// statement written for the partition is dropped from the model. One that is no partition's index or constraint is left alone.
    /// </summary>
    private static void ApplyAttachIndex(AlterIndexAttachPartitionNode attach, List<List<SqlNode>> nodesByTree)
    {
        var partitions = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var tree in nodesByTree)
        {
            foreach (var table in tree.OfType<TableNode>().Where(static table => table.PartitionOfTableName is not null && table.Name is not null))
            {
                partitions.Add(Key(table.SchemaName, table.Name!));
            }
        }

        foreach (var nodes in nodesByTree)
        {
            var index = nodes.FindIndex(node =>
                (node is IndexNode { Name: { } indexName, TableName: { } indexTable } candidate
                    && string.Equals(indexName, attach.ChildIndexName, StringComparison.Ordinal)
                    && string.Equals(candidate.SchemaName, attach.ChildSchemaName, StringComparison.Ordinal)
                    && partitions.Contains(Key(candidate.SchemaName, indexTable)))
                || (node is ConstraintNode { Name: { } constraintName, TableName: { } constraintTable } constraint
                    && string.Equals(constraintName, attach.ChildIndexName, StringComparison.Ordinal)
                    && string.Equals(constraint.SchemaName, attach.ChildSchemaName, StringComparison.Ordinal)
                    && partitions.Contains(Key(constraint.SchemaName, constraintTable))));
            if (index >= 0)
            {
                // PostgreSQL makes a partition's index from the parent's under a generated name, so only an index with that name is dropped. One with another name was created on the partition
                // first and is adopted by the parent's index, so it is kept to be created first (G-237).
                if (IsGeneratedPartitionIndexName(nodes[index]))
                {
                    nodes.RemoveAt(index);
                }

                return;
            }
        }
    }

    /// <summary>True when the partition's index or key has the name PostgreSQL would give it (its table, columns, and <c>idx</c>/<c>pkey</c>/<c>key</c>, with a number while the name is taken).</summary>
    private static bool IsGeneratedPartitionIndexName(SqlNode node)
    {
        string? table;
        string? addition;
        string label;
        string? name;
        switch (node)
        {
            case IndexNode { TableName: { } indexTable } index:
                table = indexTable;
                addition = DefaultConstraintNamer.BuildAddition(TableLikeExpander.ChooseIndexColumnNames(
                    [.. index.KeyElements.Select(static element => element.Name ?? TableLikeExpander.FigureIndexColumnName(element.ExpressionAst)), .. index.IncludeColumns]));
                label = "idx";
                name = index.Name;
                break;
            case ConstraintNode constraint when DefaultConstraintNamer.TryGetNameParts(constraint, out var constraintTable, out var constraintAddition, out var constraintLabel):
                table = constraintTable;
                addition = constraintAddition;
                label = constraintLabel;
                name = constraint.Name;
                break;
            default:
                return true;
        }

        for (var pass = 0; pass < 100; pass++)
        {
            var candidate = DefaultConstraintNamer.MakeObjectName(table, addition, pass == 0 ? label : label + pass.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A partition has the <c>CHECK</c> constraints of its ancestors under their names, and <c>pg_dump</c> writes them on the partition again. Created as a partition, that
    /// copy exists already, so a <c>CHECK</c> of a partition with the name of an ancestor's is that copy and is dropped from the model, not added a second time (G-236).
    /// </summary>
    private static void DropInheritedChecks(List<List<SqlNode>> nodesByTree)
    {
        var partitionOf = new Dictionary<string, string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var table in nodesByTree.SelectMany(static nodes => nodes).OfType<TableNode>().Where(static table => table.PartitionOfTableName is not null && table.Name is not null))
        {
            partitionOf[Key(table.SchemaName, table.Name!)] = Key(table.PartitionOfSchemaName, table.PartitionOfTableName!);
        }

        if (partitionOf.Count == 0)
        {
            return;
        }

        static bool IsCheck(ConstraintNode constraint)
            => string.Equals(constraint.ConstraintType, "CONSTR_CHECK", StringComparison.OrdinalIgnoreCase) || string.Equals(constraint.ConstraintType, "CHECK", StringComparison.OrdinalIgnoreCase);

        var checksByTable = new Dictionary<string, HashSet<string>>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var constraint in nodesByTree.SelectMany(static nodes => nodes).OfType<ConstraintNode>().Where(static constraint => IsCheck(constraint) && constraint.Name is not null && constraint.TableName is not null))
        {
            var table = Key(constraint.SchemaName, constraint.TableName!);
            if (!checksByTable.TryGetValue(table, out var names))
            {
                names = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
                checksByTable[table] = names;
            }

            names.Add(constraint.Name!);
        }

        bool Inherited(string table, string name)
        {
            var seen = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
            while (partitionOf.TryGetValue(table, out var parent) && seen.Add(parent))
            {
                if (checksByTable.TryGetValue(parent, out var names) && names.Contains(name))
                {
                    return true;
                }

                table = parent;
            }

            return false;
        }

        foreach (var nodes in nodesByTree)
        {
            nodes.RemoveAll(node => node is ConstraintNode { Name: { } name, TableName: { } tableName } constraint
                && IsCheck(constraint)
                && partitionOf.ContainsKey(Key(constraint.SchemaName, tableName))
                && Inherited(Key(constraint.SchemaName, tableName), name));
        }
    }

    /// <summary>
    /// <c>ALTER TABLE t DISABLE TRIGGER x</c> and the like (G-257): sets the firing mode on the trigger or rule of that table and name, wherever in the project it is declared.
    /// </summary>
    private static bool ApplyObjectState(
        AlterTableObjectStateNode state,
        int treeIndex,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree,
        bool deferrable = false)
    {
        for (var tree = 0; tree < nodesByTree.Count; tree++)
        {
            var nodes = nodesByTree[tree];
            var index = nodes.FindIndex(node =>
                (state.ObjectKind == "trigger" && node is TriggerNode trigger
                    && string.Equals(trigger.Name, state.ObjectName, StringComparison.Ordinal)
                    && string.Equals(trigger.TableName, state.TableName, StringComparison.Ordinal)
                    && string.Equals(trigger.SchemaName, state.SchemaName, StringComparison.Ordinal))
                || (state.ObjectKind == "rule" && node is RuleNode rule
                    && string.Equals(rule.Name, state.ObjectName, StringComparison.Ordinal)
                    && string.Equals(rule.TableName, state.TableName, StringComparison.Ordinal)
                    && string.Equals(rule.SchemaName, state.SchemaName, StringComparison.Ordinal)));
            if (index < 0)
            {
                continue;
            }

            nodes[index] = nodes[index] switch
            {
                TriggerNode trigger => trigger with { EnabledState = state.State },
                RuleNode rule => rule with { EnabledState = state.State },
                var other => other,
            };
            return true;
        }

        if (deferrable)
        {
            return false;
        }

        diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
            SqlDiagnosticSeverity.Warning,
            $"ALTER TABLE {Describe(state.SchemaName, state.TableName ?? string.Empty)} {state.State} {state.ObjectKind.ToUpperInvariant()} {state.ObjectName}: the project does not declare that {state.ObjectKind}, so the action is ignored.",
            DiagnosticSource));
        return true;
    }

    /// <summary>
    /// <c>ALTER TABLE partition DISABLE TRIGGER name</c> where the partition declares no such trigger (G-293): PostgreSQL cloned it from the trigger of that name on the partition's parent, or the parent's
    /// parent, and the copy has a firing mode of its own. The mode is kept on the trigger the partition's ancestors declare, by partition. A table that is no partition's descendant of such a trigger
    /// gets the warning a missing trigger always did.
    /// </summary>
    private static void ApplyPartitionTriggerState(
        AlterTableObjectStateNode state,
        int treeIndex,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree)
    {
        var parents = new Dictionary<string, (string? Schema, string Table)>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var table in nodesByTree.SelectMany(static nodes => nodes).OfType<TableNode>().Where(static table => table.Name is not null && table.PartitionOfTableName is not null))
        {
            parents[Key(table.SchemaName, table.Name!)] = (table.PartitionOfSchemaName, table.PartitionOfTableName!);
        }

        var visited = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var current = state.TableName is null ? ((string? Schema, string Table)?)null : (state.SchemaName, state.TableName);
        var depth = 0;
        while (current is { } child && parents.TryGetValue(Key(child.Schema, child.Table), out var parent) && visited.Add(Key(child.Schema, child.Table)))
        {
            depth++;
            foreach (var nodes in nodesByTree)
            {
                var index = nodes.FindIndex(node => node is TriggerNode trigger
                    && string.Equals(trigger.Name, state.ObjectName, StringComparison.Ordinal)
                    && string.Equals(trigger.TableName, parent.Table, StringComparison.Ordinal)
                    && string.Equals(trigger.SchemaName, parent.Schema, StringComparison.Ordinal));
                if (index < 0)
                {
                    continue;
                }

                var owner = (TriggerNode)nodes[index];
                var states = owner.PartitionStates.Where(existing => !(string.Equals(existing.TableName, state.TableName, StringComparison.Ordinal) && string.Equals(existing.SchemaName, state.SchemaName, StringComparison.Ordinal))).ToList();
                states.Add(new PartitionTriggerState(state.SchemaName, state.TableName!, state.State, depth));
                nodes[index] = owner with { PartitionStates = [.. states.OrderBy(static item => item.Depth).ThenBy(static item => item.TableName, StringComparer.Ordinal)] };
                return;
            }

            current = parent;
        }

        diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
            SqlDiagnosticSeverity.Warning,
            $"ALTER TABLE {Describe(state.SchemaName, state.TableName ?? string.Empty)} {state.State} TRIGGER {state.ObjectName}: the project does not declare that trigger, so the action is ignored.",
            DiagnosticSource));
    }

    /// <summary>
    /// <c>ALTER TABLE t { ENABLE | DISABLE } TRIGGER USER</c> (G-259): sets the state of every trigger the project declares on the table, wherever it is declared.
    /// </summary>
    private static void ApplyUserTriggerState(
        AlterTableObjectStateNode state,
        int treeIndex,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree,
        bool warnWhenNone = true)
    {
        var found = false;
        foreach (var nodes in nodesByTree)
        {
            for (var index = 0; index < nodes.Count; index++)
            {
                if (nodes[index] is TriggerNode trigger
                    && string.Equals(trigger.TableName, state.TableName, StringComparison.Ordinal)
                    && string.Equals(trigger.SchemaName, state.SchemaName, StringComparison.Ordinal))
                {
                    nodes[index] = trigger with { EnabledState = state.State };
                    found = true;
                }
            }
        }

        if (!found && warnWhenNone)
        {
            diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Warning,
                $"ALTER TABLE {Describe(state.SchemaName, state.TableName ?? string.Empty)} {state.State} TRIGGER USER: the project declares no trigger on that table, so the action is ignored.",
                DiagnosticSource));
        }
    }

    /// <summary>
    /// <c>ALTER TABLE t { ENABLE | DISABLE } TRIGGER ALL</c> (G-261): the triggers the project declares on the table take the state as with <c>USER</c> (G-259), and the table's internal (foreign-key)
    /// triggers, which the project does not declare, are kept as the table's <c>InternalTriggerState</c>. A table the project does not declare is a warning.
    /// </summary>
    private static void ApplyAllTriggerState(
        AlterTableObjectStateNode state,
        int treeIndex,
        Dictionary<string, (int TreeIndex, int NodeIndex)> tables,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree)
    {
        if (state.TableName is null || !tables.TryGetValue(Key(state.SchemaName, state.TableName), out var location))
        {
            diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Warning,
                $"ALTER TABLE {Describe(state.SchemaName, state.TableName ?? string.Empty)} {state.State} TRIGGER ALL: the project does not declare that table, so the action is ignored.",
                DiagnosticSource));
            return;
        }

        ApplyUserTriggerState(state, treeIndex, nodesByTree, diagnosticsByTree, warnWhenNone: false);
        var table = (TableNode)nodesByTree[location.TreeIndex][location.NodeIndex];
        nodesByTree[location.TreeIndex][location.NodeIndex] = table with { InternalTriggerState = state.State };
    }

    /// <summary>
    /// <c>ALTER TABLE t CLUSTER ON i</c> and <c>REPLICA IDENTITY USING INDEX i</c> (G-255, G-256): marks the index, or the primary key or unique constraint, of that name on the table, wherever in the
    /// project it is declared.
    /// </summary>
    private static void ApplyIndexFlag(
        string? schemaName,
        string? tableName,
        string indexName,
        string action,
        bool replicaIdentity,
        int treeIndex,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree)
    {
        foreach (var nodes in nodesByTree)
        {
            var index = nodes.FindIndex(node =>
                (node is IndexNode candidate && string.Equals(candidate.Name, indexName, StringComparison.Ordinal)
                    && string.Equals(candidate.TableName, tableName, StringComparison.Ordinal) && string.Equals(candidate.SchemaName, schemaName, StringComparison.Ordinal))
                || (node is ConstraintNode constraint && string.Equals(constraint.Name is { Length: > 0 } ? constraint.Name : DefaultKeyName(constraint), indexName, StringComparison.Ordinal)
                    && string.Equals(constraint.TableName, tableName, StringComparison.Ordinal) && string.Equals(constraint.SchemaName, schemaName, StringComparison.Ordinal)));
            if (index < 0)
            {
                continue;
            }

            nodes[index] = nodes[index] switch
            {
                IndexNode found => replicaIdentity ? found with { IsReplicaIdentity = true } : found with { IsClustered = true },
                ConstraintNode found => replicaIdentity ? found with { IsReplicaIdentity = true } : found with { IsClustered = true },
                var other => other,
            };
            return;
        }

        diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
            SqlDiagnosticSeverity.Warning,
            $"ALTER TABLE {Describe(schemaName, tableName ?? string.Empty)} {action} {indexName}: the project does not declare that index, so the action is ignored.",
            DiagnosticSource));
    }

    /// <summary>The name PostgreSQL gives an unnamed key (<c>t_pkey</c>, <c>t_a_key</c>), before any collision suffix, so a flag can name it before names are assigned (G-255, G-256).</summary>
    private static string? DefaultKeyName(ConstraintNode constraint)
        => DefaultConstraintNamer.IsIndexBacked(constraint.ConstraintType) && DefaultConstraintNamer.TryGetNameParts(constraint, out var table, out var addition, out var label)
            ? DefaultConstraintNamer.MakeObjectName(table, addition, label)
            : null;

    /// <summary><c>ALTER TABLE t SET WITHOUT CLUSTER</c> (G-260): the project states that the table is clustered on no index.</summary>
    private static void ApplyWithoutCluster(
        AlterTableClusterOnNode withoutCluster,
        int treeIndex,
        Dictionary<string, (int TreeIndex, int NodeIndex)> tables,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree)
    {
        if (withoutCluster.TableName is null || !tables.TryGetValue(Key(withoutCluster.SchemaName, withoutCluster.TableName), out var location))
        {
            diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Warning,
                $"ALTER TABLE {Describe(withoutCluster.SchemaName, withoutCluster.TableName ?? string.Empty)} SET WITHOUT CLUSTER: the project does not declare that table, so the action is ignored.",
                DiagnosticSource));
            return;
        }

        var table = (TableNode)nodesByTree[location.TreeIndex][location.NodeIndex];
        nodesByTree[location.TreeIndex][location.NodeIndex] = table with { WithoutCluster = true };
    }

    /// <summary>
    /// A table the project both clusters on an index and sets without cluster (G-260): PostgreSQL's answer depends on statement order, which pgproj cannot know across files, so the index wins and the
    /// table gets a warning.
    /// </summary>
    private static void ResolveWithoutClusterConflicts(List<List<SqlNode>> nodesByTree, List<List<SqlDiagnostic>> diagnosticsByTree)
    {
        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            var nodes = nodesByTree[treeIndex];
            for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                if (nodes[nodeIndex] is not TableNode { WithoutCluster: true, Name: { } tableName } table)
                {
                    continue;
                }

                var clustered = nodesByTree.Any(tree => tree.Any(node =>
                    (node is IndexNode { IsClustered: true } index && string.Equals(index.TableName, tableName, StringComparison.Ordinal) && string.Equals(index.SchemaName, table.SchemaName, StringComparison.Ordinal))
                    || (node is ConstraintNode { IsClustered: true } constraint && string.Equals(constraint.TableName, tableName, StringComparison.Ordinal) && string.Equals(constraint.SchemaName, table.SchemaName, StringComparison.Ordinal))));
                if (!clustered)
                {
                    continue;
                }

                nodes[nodeIndex] = table with { WithoutCluster = false };
                diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                    SqlDiagnosticSeverity.Warning,
                    $"ALTER TABLE {Describe(table.SchemaName, tableName)} SET WITHOUT CLUSTER: the project also says CLUSTER ON an index of that table; pgproj cannot know which statement runs last, so CLUSTER ON wins.",
                    DiagnosticSource));
            }
        }
    }

    /// <summary>
    /// <c>ALTER TABLE child ALTER COLUMN c SET|DROP DEFAULT</c> for a column the child only inherits (G-237): the child lists no such column, so the default is kept on the table and applied once it
    /// has its parents' columns. True when it was recorded.
    /// </summary>
    private static bool TryRecordInheritedDefault(
        AlterTableColumnDefaultNode columnDefault,
        Dictionary<string, (int TreeIndex, int NodeIndex)> tables,
        List<List<SqlNode>> nodesByTree)
    {
        if (columnDefault.TableName is null || !tables.TryGetValue(Key(columnDefault.SchemaName, columnDefault.TableName), out var location))
        {
            return false;
        }

        var table = (TableNode)nodesByTree[location.TreeIndex][location.NodeIndex];
        if ((table.InheritsFrom.Count == 0 && string.IsNullOrWhiteSpace(table.PartitionOfTableName))
            || table.Columns.Any(column => string.Equals(column.Name, columnDefault.ColumnName, StringComparison.Ordinal)))
        {
            return false;
        }

        nodesByTree[location.TreeIndex][location.NodeIndex] = table with
        {
            InheritedColumnDefaults = [.. table.InheritedColumnDefaults.Where(existing => !string.Equals(existing.ColumnName, columnDefault.ColumnName, StringComparison.Ordinal)),
                new InheritedColumnDefault(columnDefault.ColumnName, columnDefault.RawExpression, columnDefault.ExpressionAst)],
        };
        return true;
    }

    /// <summary>
    /// <c>ALTER VIEW v ALTER COLUMN c SET|DROP DEFAULT</c>, or <c>ALTER TABLE ONLY v ...</c> as <c>pg_dump</c> writes it, for a view the project declares (G-291): the default is kept on the
    /// view. True when the statement names a view and no table. A later statement for the same column replaces the earlier one; <c>DROP DEFAULT</c> removes it.
    /// </summary>
    private static bool TryApplyViewColumnDefault(
        AlterTableColumnDefaultNode columnDefault,
        Dictionary<string, (int TreeIndex, int NodeIndex)> tables,
        List<List<SqlNode>> nodesByTree)
    {
        if (columnDefault.TableName is null || tables.ContainsKey(Key(columnDefault.SchemaName, columnDefault.TableName)))
        {
            return false;
        }

        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            var nodes = nodesByTree[treeIndex];
            for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                if (nodes[nodeIndex] is not ViewNode { IsMaterialized: false } view
                    || !string.Equals(view.Name, columnDefault.TableName, StringComparison.Ordinal)
                    || !string.Equals(view.SchemaName, columnDefault.SchemaName, StringComparison.Ordinal))
                {
                    continue;
                }

                var defaults = view.ColumnDefaults.Where(existing => !string.Equals(existing.ColumnName, columnDefault.ColumnName, StringComparison.Ordinal)).ToList();
                if (columnDefault.RawExpression is { } expression)
                {
                    defaults.Add(new ViewColumnDefault(columnDefault.ColumnName, expression));
                }

                nodes[nodeIndex] = view with { ColumnDefaults = defaults };
                return true;
            }
        }

        return false;
    }

    private static void ApplyReplicaIdentity(
        AlterTableReplicaIdentityNode replicaIdentity,
        int treeIndex,
        Dictionary<string, (int TreeIndex, int NodeIndex)> tables,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree)
    {
        if (replicaIdentity.TableName is null || !tables.TryGetValue(Key(replicaIdentity.SchemaName, replicaIdentity.TableName), out var location))
        {
            diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Warning,
                $"ALTER TABLE {Describe(replicaIdentity.SchemaName, replicaIdentity.TableName ?? string.Empty)} REPLICA IDENTITY: the project does not declare that table, so the action is ignored.",
                DiagnosticSource));
            return;
        }

        var table = (TableNode)nodesByTree[location.TreeIndex][location.NodeIndex];
        nodesByTree[location.TreeIndex][location.NodeIndex] = table with { ReplicaIdentity = replicaIdentity.Mode };
        if (replicaIdentity.IndexName is not null)
        {
            ApplyIndexFlag(replicaIdentity.SchemaName, replicaIdentity.TableName, replicaIdentity.IndexName, "REPLICA IDENTITY USING INDEX", replicaIdentity: true, treeIndex, nodesByTree, diagnosticsByTree);
        }
    }
}
