using PgProj.Parser;

namespace PgProj.Semantics;

internal sealed class ConstraintPass
{
    public DatabaseModel Execute(DatabaseModel model, IEnumerable<SqlSyntaxTree> syntaxTrees)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(syntaxTrees);

        var tables = new Dictionary<string, TableDefinition>(model.Tables, PgProj.Parser.SqlIdentifiers.NameComparer);
        var constraints = new Dictionary<string, ConstraintDefinition>(model.Constraints, PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var syntaxTree in syntaxTrees)
        {
            foreach (var constraintNode in syntaxTree.Constraints)
            {
                var constraint = MapConstraint(constraintNode) with
                {
                    Location = SemanticLocation.Of(syntaxTree.FilePath, constraintNode.Range),
                    ReferencedTableLocation = SemanticLocation.Of(syntaxTree.FilePath, constraintNode.ReferencedTableRange),
                };
                constraints[constraint.Key] = constraint;

                if (TryResolveTableKey(constraintNode, constraint, tables, out var tableKey)
                    && tables.TryGetValue(tableKey, out var table))
                {
                    tables[tableKey] = AttachConstraint(table, constraint);
                }
            }
        }

        ApplyComments(syntaxTrees, constraints, tables, model.Indexes);

        return model with
        {
            Tables = tables,
            Constraints = constraints,
        };
    }

    /// <summary>
    /// Applies each <c>COMMENT ON CONSTRAINT name ON table</c> to the constraint it names, wherever in the project the constraint was declared (G-161); the last statement wins. A comment that
    /// names no constraint the project declares is ignored. <c>IS NULL</c> and an empty string state that the constraint has no comment, as PostgreSQL stores them.
    /// </summary>
    private static void ApplyComments(IEnumerable<SqlSyntaxTree> syntaxTrees, Dictionary<string, ConstraintDefinition> constraints, Dictionary<string, TableDefinition> tables, IReadOnlyDictionary<string, IndexDefinition> indexes)
    {
        foreach (var tree in syntaxTrees)
        {
            // COMMENT ON INDEX naming the index of a key or exclusion constraint, which the model does not have as an index of its own (G-295). The index has the constraint's name.
            foreach (var node in tree.ObjectComments.Where(static node => node.ObjectKind == "Index"))
            {
                if (indexes.ContainsKey(DatabaseModel.CreateKey(node.SchemaName, node.TableName ?? string.Empty))
                    || indexes.Values.Any(index => string.Equals(index.Name, node.TableName, StringComparison.Ordinal) && (node.SchemaName is null || string.Equals(index.SchemaName, node.SchemaName, StringComparison.Ordinal))))
                {
                    continue;
                }

                var keyConstraint = constraints.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, node.TableName, StringComparison.Ordinal)
                    && (node.SchemaName is null || string.Equals(candidate.SchemaName, node.SchemaName, StringComparison.Ordinal))
                    && IsKeyOrExclusion(candidate.ConstraintType));
                if (keyConstraint is not null)
                {
                    constraints[keyConstraint.Key] = keyConstraint with { IndexComment = string.IsNullOrEmpty(node.CommentText) ? null : node.CommentText, IndexCommentStated = true };
                }
            }

            foreach (var node in tree.ObjectComments.Where(static node => node.ObjectKind == "Constraint"))
            {
                var key = DatabaseModel.CreateConstraintKey(node.SchemaName, node.TableName, node.ObjectName);
                if (constraints.TryGetValue(key, out var constraint))
                {
                    constraints[key] = constraint with { Comment = string.IsNullOrEmpty(node.CommentText) ? null : node.CommentText, CommentStated = true };
                }
                else if (node.TableName is not null && tables.TryGetValue(DatabaseModel.CreateKey(node.SchemaName, node.TableName), out var table)
                    && table.Columns.ToList().FindIndex(column => column.NotNullName is not null && !column.AllowsNull && string.Equals(column.NotNullName, node.ObjectName, StringComparison.Ordinal)) is var index and >= 0)
                {
                    // The comment names a column's NOT NULL constraint, which PostgreSQL 18 stores under that name (G-235).
                    var columns = table.Columns.ToArray();
                    columns[index] = columns[index] with { NotNullComment = string.IsNullOrEmpty(node.CommentText) ? null : node.CommentText, NotNullCommentStated = true };
                    tables[table.Key] = table with { Columns = columns };
                }
            }
        }
    }

    /// <summary>Whether a constraint type, as spelled before normalization (<c>CONSTR_PRIMARY</c>) or after (<c>primary key</c>), has an index of the constraint's name: a primary key, unique, or exclusion constraint.</summary>
    private static bool IsKeyOrExclusion(string? constraintType)
        => constraintType?.Trim().ToUpperInvariant() is { } type && (type.Contains("PRIMARY", StringComparison.Ordinal) || type.Contains("UNIQUE", StringComparison.Ordinal) || type.Contains("EXCLU", StringComparison.Ordinal));

    private static ConstraintDefinition MapConstraint(ConstraintNode node)
    {
        var constraintName = node.Name ?? (node.TableName is null ? "unknown_constraint" : $"{node.TableName}_constraint");
        var key = DatabaseModel.CreateConstraintKey(node.SchemaName, node.TableName, node.Name);

        // TODO: Populate exclusion details and deferrable options in a deeper semantic pass.
        return new ConstraintDefinition(
            key,
            constraintName,
            node.SchemaName,
            node.TableName,
            node.ConstraintType,
            PrimaryKeyColumns: [],
            UniqueColumns: [],
            ForeignKeyReferencedTable: null,
            ForeignKeyReferencedColumns: [],
            RawCheckExpression: node.RawCheckExpression,
            ReferencedTables: [],
            ReferencedColumns: [],
            ReferencedTypes: [],
            ReferencedFunctions: [],
            ResolvedTables: [],
            ResolvedColumns: [],
            ResolvedTypes: [],
            ResolvedFunctions: [])
        {
            DeclarationNode = node,
            IsDeferrable = node.IsDeferrable,
            IsInitiallyDeferred = node.IsInitiallyDeferred,
            IsEnforced = node.IsEnforced,
            IsNotValid = node.IsNotValid,
            IsNameGenerated = node.IsNameGenerated,
            WithoutOverlaps = node.WithoutOverlaps,
            NullsNotDistinct = node.NullsNotDistinct,
            ForeignKeyUsesPeriod = node.ForeignKeyUsesPeriod,
            ExclusionAccessMethod = node.ExclusionAccessMethod,
            ExclusionElements = node.ExclusionElements
                .Select(static element => new ConstraintExclusionElementDefinition(element.Name, element.RawExpression, element.OperatorName) { ExpressionAst = element.ExpressionAst })
                .ToArray(),
            RawExclusionPredicateExpression = node.RawExclusionPredicateExpression,
            IndexStorageOptions = node.IndexStorageOptions,
            IsClustered = node.IsClustered,
            IsReplicaIdentity = node.IsReplicaIdentity,
            IncludeColumns = node.IncludeColumns,
            IsNoInherit = node.IsNoInherit,
            CopiedByLike = node.CopiedByLike,
            IsInPartitionOfList = node.IsInPartitionOfList,
        };
    }

    private static TableDefinition AttachConstraint(TableDefinition table, ConstraintDefinition constraint)
    {
        var constraintKeys = table.ConstraintKeys
            .Append(constraint.Key)
            .Distinct(PgProj.Parser.SqlIdentifiers.NameComparer)
            .ToArray();

        // TODO: Attach deeper per-table constraint metadata once semantic table objects expose richer constraint models.
        return table with
        {
            ConstraintKeys = constraintKeys,
        };
    }

    private static bool TryResolveTableKey(
        ConstraintNode node,
        ConstraintDefinition constraint,
        IReadOnlyDictionary<string, TableDefinition> tables,
        out string tableKey)
    {
        tableKey = string.Empty;

        if (string.IsNullOrWhiteSpace(constraint.TableName))
        {
            return false;
        }

        tableKey = DatabaseModel.CreateKey(node.SchemaName, constraint.TableName);
        if (tables.ContainsKey(tableKey))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(node.SchemaName))
        {
            return false;
        }

        foreach (var table in tables.Values)
        {
            if (string.Equals(table.Name, constraint.TableName, StringComparison.Ordinal))
            {
                tableKey = table.Key;
                return true;
            }
        }

        return false;
    }
}
