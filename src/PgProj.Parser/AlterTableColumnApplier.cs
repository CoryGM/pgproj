namespace PgProj.Parser;

/// <summary>
/// Applies <c>ALTER TABLE ... ADD COLUMN</c> across all syntax trees (G-47). A column has always come from the
/// <c>CREATE TABLE</c> that declares it, so an added column was silently missing from the model, and <c>publish</c> reported success
/// without creating it. The table and the <c>ALTER TABLE</c> may be in different files, so this runs over every tree before any
/// semantic pass, like <see cref="ConstraintNameAssigner"/>: the added column is appended to its table's
/// <see cref="TableNode.Columns"/> (in tree order, then statement order), and everything downstream sees an ordinary column.
/// </summary>
/// <remarks>
/// <c>ADD COLUMN IF NOT EXISTS</c> for a column that exists is a no-op. A column that already exists without <c>IF NOT EXISTS</c> is
/// an error, as it is in PostgreSQL. A column added to a table the project does not declare, and every other <c>ALTER TABLE</c>
/// action the model has no place for, is reported as a warning and ignored. Constraints written on an added column, and
/// <c>ADD CONSTRAINT</c>, are handled as they always were. The result contains none of the marker nodes, so applying it again
/// changes nothing.
/// </remarks>
public static partial class AlterTableColumnApplier
{
    /// <summary>The <see cref="SqlDiagnostic.Source"/> of the diagnostics this class adds to a syntax tree.</summary>
    public const string DiagnosticSource = "AlterTable";

    private const string DefaultSchema = "public";

    public static IReadOnlyList<SqlSyntaxTree> Apply(IReadOnlyList<SqlSyntaxTree> trees)
    {
        ArgumentNullException.ThrowIfNull(trees);

        if (!trees.Any(static tree => tree.Nodes.Any(static node => node is AlterTableAddColumnNode or UnsupportedAlterTableNode or AlterTableRowSecurityNode or NotNullConstraintNode or AlterTableColumnDefaultNode or AlterTableAddIdentityNode or AlterTableColumnStorageNode or AlterTableAttachPartitionNode or AlterSequenceOwnedByNode or AlterIndexAttachPartitionNode or AlterTableObjectStateNode or AlterTableClusterOnNode or AlterTableReplicaIdentityNode)))
        {
            return trees;
        }

        var nodesByTree = trees.Select(static tree => tree.Nodes.ToList()).ToList();
        var diagnosticsByTree = trees.Select(static tree => tree.Diagnostics.ToList()).ToList();

        // Where each table lives, so a column added from another file lands in the tree that declares the table.
        var tables = new Dictionary<string, (int TreeIndex, int NodeIndex)>(PgProj.Parser.SqlIdentifiers.NameComparer);
        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            for (var nodeIndex = 0; nodeIndex < nodesByTree[treeIndex].Count; nodeIndex++)
            {
                if (nodesByTree[treeIndex][nodeIndex] is TableNode { Name: { } tableName } table)
                {
                    tables.TryAdd(Key(table.SchemaName, tableName), (treeIndex, nodeIndex));
                }
            }
        }

        // Where each sequence lives, for ALTER SEQUENCE ... OWNED BY (G-236).
        var deferredTriggerStates = new List<(AlterTableObjectStateNode State, int TreeIndex)>();
        var sequences = new Dictionary<string, (int TreeIndex, int NodeIndex)>(PgProj.Parser.SqlIdentifiers.NameComparer);
        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            for (var nodeIndex = 0; nodeIndex < nodesByTree[treeIndex].Count; nodeIndex++)
            {
                if (nodesByTree[treeIndex][nodeIndex] is SequenceNode { Name: { } sequenceName } sequence)
                {
                    sequences.TryAdd(Key(sequence.SchemaName, sequenceName), (treeIndex, nodeIndex));
                }
            }
        }

        // TRIGGER USER first, so that a statement naming one trigger overrides it wherever it is written (G-259).
        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            foreach (var node in nodesByTree[treeIndex].ToList())
            {
                if (node is AlterTableObjectStateNode { ObjectKind: AlterTableObjectStateNode.UserTriggersKind } userTriggers)
                {
                    ApplyUserTriggerState(userTriggers, treeIndex, nodesByTree, diagnosticsByTree);
                }
                else if (node is AlterTableObjectStateNode { ObjectKind: AlterTableObjectStateNode.AllTriggersKind } allTriggers)
                {
                    ApplyAllTriggerState(allTriggers, treeIndex, tables, nodesByTree, diagnosticsByTree);
                }
            }
        }

        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            foreach (var node in nodesByTree[treeIndex].ToList())
            {
                switch (node)
                {
                    case AlterTableObjectStateNode { ObjectKind: AlterTableObjectStateNode.UserTriggersKind or AlterTableObjectStateNode.AllTriggersKind }:
                        break;
                    case AlterTableColumnDefaultNode inheritedDefault when TryRecordInheritedDefault(inheritedDefault, tables, nodesByTree):
                        break;
                    case AlterTableColumnDefaultNode viewDefault when TryApplyViewColumnDefault(viewDefault, tables, nodesByTree):
                        break;
                    case AlterTableColumnDefaultNode columnDefault:
                        ApplyColumnChange(columnDefault.TableName, columnDefault.SchemaName, columnDefault.ColumnName, "SET/DROP DEFAULT", treeIndex, tables, nodesByTree, diagnosticsByTree,
                            column => column with { RawDefaultExpression = columnDefault.RawExpression, DefaultExpressionAst = columnDefault.ExpressionAst });
                        break;
                    case AlterTableAddIdentityNode addIdentity:
                        ApplyColumnChange(addIdentity.TableName, addIdentity.SchemaName, addIdentity.ColumnName, "ADD GENERATED", treeIndex, tables, nodesByTree, diagnosticsByTree,
                            column => column with
                            {
                                IdentityGeneration = addIdentity.Generation,
                                IdentityOptions = addIdentity.Options,
                                IdentityOptionsText = addIdentity.OptionsText,
                                IsNullable = false,
                            });
                        break;
                    case AlterTableColumnStorageNode columnStorage:
                        ApplyColumnChange(columnStorage.TableName, columnStorage.SchemaName, columnStorage.ColumnName, columnStorage.Storage is not null ? "SET STORAGE" : "SET COMPRESSION", treeIndex, tables, nodesByTree, diagnosticsByTree,
                            column => column with { Storage = columnStorage.Storage ?? column.Storage, IsStorageSetByAlter = columnStorage.Storage is not null || column.IsStorageSetByAlter, Compression = columnStorage.Compression ?? column.Compression });
                        break;
                    case AlterTableAttachPartitionNode attach:
                        ApplyAttachPartition(attach, treeIndex, tables, nodesByTree, diagnosticsByTree);
                        break;
                    case AlterSequenceOwnedByNode ownedBy:
                        ApplySequenceOwnedBy(ownedBy, treeIndex, sequences, nodesByTree, diagnosticsByTree);
                        break;
                    case AlterTableObjectStateNode objectState:
                        if (!ApplyObjectState(objectState, treeIndex, nodesByTree, diagnosticsByTree, deferrable: objectState.ObjectKind == "trigger"))
                        {
                            deferredTriggerStates.Add((objectState, treeIndex));
                        }

                        break;
                    case AlterTableClusterOnNode { IndexName: null } withoutCluster:
                        ApplyWithoutCluster(withoutCluster, treeIndex, tables, nodesByTree, diagnosticsByTree);
                        break;
                    case AlterTableClusterOnNode { IndexName: { } clusterIndex } clusterOn:
                        ApplyIndexFlag(clusterOn.SchemaName, clusterOn.TableName, clusterIndex, "CLUSTER ON", replicaIdentity: false, treeIndex, nodesByTree, diagnosticsByTree);
                        break;
                    case AlterTableReplicaIdentityNode replicaIdentity:
                        ApplyReplicaIdentity(replicaIdentity, treeIndex, tables, nodesByTree, diagnosticsByTree);
                        break;
                    case AlterTableAddColumnNode { TableName: { } tableName } add:
                        ApplyAddColumn(add, tableName, treeIndex, tables, nodesByTree, diagnosticsByTree);
                        break;
                    case AlterTableRowSecurityNode { TableName: { } rlsTableName } rowSecurity:
                        ApplyRowSecurity(rowSecurity, rlsTableName, treeIndex, tables, nodesByTree, diagnosticsByTree);
                        break;
                    case NotNullConstraintNode { TableName: { } notNullTableName, ColumnName: { } notNullColumnName } notNull:
                        ApplyNotNull(notNull, notNullTableName, notNullColumnName, treeIndex, tables, nodesByTree, diagnosticsByTree);
                        break;
                    case UnsupportedAlterTableNode unsupported:
                        diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                            SqlDiagnosticSeverity.Warning,
                            $"ALTER TABLE {Describe(unsupported.SchemaName, unsupported.TableName)}: {unsupported.Action} is not modeled; the action is ignored.",
                            DiagnosticSource));
                        break;
                }
            }
        }

        ResolveWithoutClusterConflicts(nodesByTree, diagnosticsByTree);

        // A trigger statement for a table that declares no such trigger may be about a partition's copy of its parent's (G-293), which is known only once every ATTACH PARTITION is applied.
        foreach (var (deferred, deferredTree) in deferredTriggerStates)
        {
            ApplyPartitionTriggerState(deferred, deferredTree, nodesByTree, diagnosticsByTree);
        }

        // After every ATTACH PARTITION, whichever file it is in: only then is it known which tables are partitions (G-236).
        DropInheritedChecks(nodesByTree);
        foreach (var attachIndex in nodesByTree.SelectMany(static nodes => nodes).OfType<AlterIndexAttachPartitionNode>().ToList())
        {
            ApplyAttachIndex(attachIndex, nodesByTree);
        }

        var result = new List<SqlSyntaxTree>(trees.Count);
        for (var treeIndex = 0; treeIndex < trees.Count; treeIndex++)
        {
            result.Add(trees[treeIndex] with
            {
                Nodes = nodesByTree[treeIndex].Where(static node => node is not (AlterTableAddColumnNode or UnsupportedAlterTableNode or AlterTableRowSecurityNode or NotNullConstraintNode or AlterTableColumnDefaultNode or AlterTableAddIdentityNode or AlterTableColumnStorageNode or AlterTableAttachPartitionNode or AlterSequenceOwnedByNode or AlterIndexAttachPartitionNode or AlterTableObjectStateNode or AlterTableClusterOnNode or AlterTableReplicaIdentityNode)).ToArray(),
                Diagnostics = diagnosticsByTree[treeIndex],
            });
        }

        return result;
    }

    /// <summary>
    /// Folds an <c>ALTER TABLE ... ENABLE/DISABLE ROW LEVEL SECURITY</c> or <c>FORCE/NO FORCE ROW LEVEL SECURITY</c> onto its
    /// table's <see cref="TableNode.IsRowSecurityEnabled"/>/<see cref="TableNode.IsRowSecurityForced"/> (G-76), the same way
    /// <see cref="ApplyAddColumn"/> folds an added column onto <see cref="TableNode.Columns"/>. A table the project does not
    /// declare is reported the same way an add-column to an undeclared table is: a warning, the action ignored.
    /// </summary>
    private static void ApplyRowSecurity(
        AlterTableRowSecurityNode rowSecurity,
        string tableName,
        int treeIndex,
        Dictionary<string, (int TreeIndex, int NodeIndex)> tables,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree)
    {
        if (!tables.TryGetValue(Key(rowSecurity.SchemaName, tableName), out var location))
        {
            var target = Describe(rowSecurity.SchemaName, tableName);
            var action = rowSecurity.EnableRowSecurity switch
            {
                true => "ENABLE ROW LEVEL SECURITY",
                false => "DISABLE ROW LEVEL SECURITY",
                null => rowSecurity.ForceRowSecurity == true ? "FORCE ROW LEVEL SECURITY" : "NO FORCE ROW LEVEL SECURITY",
            };
            diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Warning,
                $"ALTER TABLE {target} {action}: the project does not declare that table, so the action is ignored.",
                DiagnosticSource));
            return;
        }

        var table = (TableNode)nodesByTree[location.TreeIndex][location.NodeIndex];
        nodesByTree[location.TreeIndex][location.NodeIndex] = table with
        {
            IsRowSecurityEnabled = rowSecurity.EnableRowSecurity ?? table.IsRowSecurityEnabled,
            IsRowSecurityForced = rowSecurity.ForceRowSecurity ?? table.IsRowSecurityForced,
        };
    }

    /// <summary>
    /// Makes the column a <c>NOT NULL</c> table constraint names not nullable (G-126), as if it had been written on the column (a <c>NOT VALID</c>
    /// one also says so, and keeps its name, which <c>VALIDATE CONSTRAINT</c> needs; G-121). A table or
    /// column the project does not declare is reported the way an add-column to an undeclared table is: a warning, the constraint ignored.
    /// </summary>
    private static void ApplyNotNull(
        NotNullConstraintNode notNull,
        string tableName,
        string columnName,
        int treeIndex,
        Dictionary<string, (int TreeIndex, int NodeIndex)> tables,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree)
    {
        var target = Describe(notNull.SchemaName, tableName);
        if (!tables.TryGetValue(Key(notNull.SchemaName, tableName), out var location))
        {
            diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Warning,
                $"NOT NULL on {target}.{columnName}: the project does not declare that table, so the constraint is ignored.",
                DiagnosticSource));
            return;
        }

        var table = (TableNode)nodesByTree[location.TreeIndex][location.NodeIndex];
        var index = table.Columns.ToList().FindIndex(column => string.Equals(column.Name, columnName, StringComparison.Ordinal));
        if (index < 0 && (table.InheritsFrom.Count > 0 || notNull.IsInPartitionOfList) && !notNull.IsNotValid)
        {
            // The child's own constraint on a column it only inherits (G-253), or a partition's on a column it takes from its parent (G-266); the semantic layer
            // applies it once the table has its parents' columns.
            nodesByTree[location.TreeIndex][location.NodeIndex] = table with
            {
                InheritedColumnNotNulls = [.. table.InheritedColumnNotNulls.Where(existing => !string.Equals(existing.ColumnName, columnName, StringComparison.Ordinal)),
                    new InheritedColumnNotNull(columnName, notNull.ConstraintName, notNull.NoInherit) { IsTableConstraintInPartitionOfList = notNull.IsInPartitionOfList }],
            };
            return;
        }

        if (index < 0)
        {
            diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Warning,
                $"NOT NULL on {target}.{columnName}: the project does not declare that column, so the constraint is ignored.",
                DiagnosticSource));
            return;
        }

        var columns = table.Columns.ToArray();
        columns[index] = columns[index] with
        {
            IsNullable = false,
            NotNullIsNotValid = notNull.IsNotValid,
            NotNullConstraintName = notNull.IsNotValid ? notNull.ConstraintName : null,
            NotNullName = notNull.ConstraintName ?? columns[index].NotNullName,
            NotNullNoInherit = notNull.NoInherit || columns[index].NotNullNoInherit,
        };
        nodesByTree[location.TreeIndex][location.NodeIndex] = table with { Columns = columns };
    }

    private static void ApplyAddColumn(
        AlterTableAddColumnNode add,
        string tableName,
        int treeIndex,
        Dictionary<string, (int TreeIndex, int NodeIndex)> tables,
        List<List<SqlNode>> nodesByTree,
        List<List<SqlDiagnostic>> diagnosticsByTree)
    {
        var columnName = add.Column.Name ?? string.Empty;
        var target = Describe(add.SchemaName, tableName);

        if (!tables.TryGetValue(Key(add.SchemaName, tableName), out var location))
        {
            diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Warning,
                $"ALTER TABLE {target} ADD COLUMN {columnName}: the project does not declare that table, so the column is ignored.",
                DiagnosticSource));
            return;
        }

        var table = (TableNode)nodesByTree[location.TreeIndex][location.NodeIndex];
        if (table.Columns.Any(column => string.Equals(column.Name, columnName, StringComparison.Ordinal)))
        {
            if (!add.IfNotExists)
            {
                diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                    SqlDiagnosticSeverity.Error,
                    $"ALTER TABLE {target} ADD COLUMN {columnName}: column '{columnName}' already exists.",
                    DiagnosticSource));
            }

            return;
        }

        // The column is keyed by the table as CREATE TABLE spelled it, whatever the ALTER TABLE called it.
        var column = add.Column with { TableName = table.Name, SchemaName = table.SchemaName };
        nodesByTree[location.TreeIndex][location.NodeIndex] = table with { Columns = [.. table.Columns, column] };
    }

    private static string Key(string? schemaName, string tableName)
        => $"{(string.IsNullOrWhiteSpace(schemaName) ? DefaultSchema : schemaName.Trim())}.{tableName.Trim()}";

    private static string Describe(string? schemaName, string? tableName)
        => string.IsNullOrWhiteSpace(schemaName) ? tableName ?? string.Empty : $"{schemaName}.{tableName}";
}
