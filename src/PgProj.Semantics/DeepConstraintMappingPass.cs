using PgProj.Parser;

namespace PgProj.Semantics;

internal sealed class DeepConstraintMappingPass
{
    private DatabaseModel _model = DatabaseModel.Empty;

    public DatabaseModel Execute(DatabaseModel model, IEnumerable<SqlSyntaxTree> syntaxTrees)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(syntaxTrees);

        _model = model;

        var constraints = new Dictionary<string, ConstraintDefinition>(model.Constraints, PgProj.Parser.SqlIdentifiers.NameComparer);
        var diagnostics = new List<SemanticDiagnostic>(model.Diagnostics);

        foreach (var syntaxTree in syntaxTrees)
        {
            foreach (var constraintNode in syntaxTree.Constraints)
            {
                var constraintKey = ResolveConstraintDefinitionKey(constraintNode);
                if (!constraints.TryGetValue(constraintKey, out var constraint))
                {
                    diagnostics.Add(SemanticDiagnostics.Error(
                        $"Deep constraint mapping could not find semantic constraint '{constraintKey}'.",
                        SemanticLocation.Of(syntaxTree.FilePath, constraintNode.Range),
                        nameof(DeepConstraintMappingPass)));
                    continue;
                }

                var visitor = new SqlDependencyVisitor();
                var mappedConstraint = MapConstraint(constraintNode, constraint, visitor);
                mappedConstraint = AttachDependencies(mappedConstraint, visitor.Info);

                constraints[constraintKey] = mappedConstraint;
                AddDiagnostics(constraintNode, mappedConstraint, diagnostics);
            }
        }

        ResolveImplicitForeignKeyTargets(constraints, diagnostics);

        return model with
        {
            Constraints = constraints,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// A foreign key written without a referenced column list (<c>REFERENCES p</c>) targets the primary key of
    /// the referenced table. Resolves that implicit target to the primary key's columns, in key order, so the
    /// constraint can be deployed and compares equal to the deployed form, where PostgreSQL always reports the
    /// explicit list (G-43). This runs after every constraint of every file is mapped because the key may be
    /// declared later, by <c>ALTER TABLE</c>, or in another file.
    /// </summary>
    private void ResolveImplicitForeignKeyTargets(
        IDictionary<string, ConstraintDefinition> constraints,
        ICollection<SemanticDiagnostic> diagnostics)
    {
        var primaryKeysByTable = new Dictionary<string, ConstraintDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var constraint in constraints.Values)
        {
            if (NormalizeConstraintType(constraint.ConstraintType) == ConstraintKinds.PrimaryKey
                && constraint.PrimaryKeyColumns.Count > 0
                && TryResolveTableKey(constraint.SchemaName, constraint.TableName, out var tableKey))
            {
                primaryKeysByTable[tableKey] = constraint;
            }
        }

        foreach (var key in constraints.Keys.ToArray())
        {
            var foreignKey = constraints[key];
            if (foreignKey.ForeignKeyColumns.Count == 0
                || foreignKey.ForeignKeyReferencedColumns.Count > 0
                || string.IsNullOrWhiteSpace(foreignKey.ForeignKeyReferencedTable)
                || !TryResolveTableKey(foreignKey.ForeignKeyReferencedSchemaName ?? foreignKey.SchemaName, foreignKey.ForeignKeyReferencedTable, out var targetTableKey))
            {
                // Explicit column list, not a foreign key, or a missing table (already reported).
                continue;
            }

            if (!primaryKeysByTable.TryGetValue(targetTableKey, out var primaryKey))
            {
                diagnostics.Add(SemanticDiagnostics.Error(
                    $"Foreign key '{foreignKey.Key}' omits its referenced columns, but table '{targetTableKey}' has no primary key to reference.",
                    foreignKey.ReferencedTableLocation ?? foreignKey.Location,
                    nameof(DeepConstraintMappingPass)));
                continue;
            }

            if (primaryKey.PrimaryKeyColumns.Count != foreignKey.ForeignKeyColumns.Count)
            {
                diagnostics.Add(SemanticDiagnostics.Error(
                    $"Foreign key '{foreignKey.Key}' has {foreignKey.ForeignKeyColumns.Count} column(s) but the primary key of '{targetTableKey}' has {primaryKey.PrimaryKeyColumns.Count}.",
                    foreignKey.Location,
                    nameof(DeepConstraintMappingPass)));
                continue;
            }

            constraints[key] = foreignKey with { ForeignKeyReferencedColumns = primaryKey.PrimaryKeyColumns };
        }
    }

    private ConstraintDefinition MapConstraint(ConstraintNode node, ConstraintDefinition definition, SqlDependencyVisitor visitor)
    {
        var normalizedConstraintType = NormalizeConstraintType(node.ConstraintType ?? definition.ConstraintType);
        var normalizedDefinition = AttachConstraintDependencies(node, definition with
        {
            ConstraintType = normalizedConstraintType ?? definition.ConstraintType,
            IsDeferrable = node.IsDeferrable || definition.IsDeferrable,
            IsInitiallyDeferred = node.IsInitiallyDeferred ?? definition.IsInitiallyDeferred,
            IsEnforced = node.IsEnforced && definition.IsEnforced,
            IsNotValid = node.IsNotValid || definition.IsNotValid,
        }, visitor);

        return normalizedConstraintType switch
        {
            ConstraintKinds.PrimaryKey => MapPrimaryKey(node, normalizedDefinition),
            ConstraintKinds.Unique => MapUnique(node, normalizedDefinition),
            ConstraintKinds.ForeignKey => MapForeignKey(node, normalizedDefinition),
            ConstraintKinds.Check => MapCheck(node, normalizedDefinition, visitor),
            ConstraintKinds.Exclusion => MapExclusion(node, normalizedDefinition, visitor),
            _ => normalizedDefinition,
        };
    }

    private ConstraintDefinition MapPrimaryKey(ConstraintNode node, ConstraintDefinition definition)
    {
        var keyColumns = node.KeyColumns.Count > 0 ? node.KeyColumns : definition.PrimaryKeyColumns;
        return definition with
        {
            PrimaryKeyColumns = NormalizeOrderedReferences(keyColumns),
        };
    }

    private ConstraintDefinition MapUnique(ConstraintNode node, ConstraintDefinition definition)
    {
        // UNIQUE column sets are treated as order-insensitive for comparison purposes: two UNIQUE
        // constraints over the same columns in a different order are semantically equivalent.
        var keyColumns = node.KeyColumns.Count > 0 ? node.KeyColumns : definition.UniqueColumns;
        return definition with
        {
            UniqueColumns = NormalizeReferences(keyColumns),
        };
    }

    private ConstraintDefinition MapForeignKey(ConstraintNode node, ConstraintDefinition definition)
    {
        var referencedTableName = node.ForeignKeyReferencedTableName ?? definition.ForeignKeyReferencedTable;
        var referencedSchemaName = node.ForeignKeyReferencedSchemaName ?? node.SchemaName;

        var referencedTables = definition.ReferencedTables;
        if (!string.IsNullOrWhiteSpace(referencedTableName))
        {
            referencedTables = Merge(referencedTables, [ResolveTableKey(referencedSchemaName, referencedTableName)]);
        }

        var referencedColumns = node.ForeignKeyReferencedColumns.Count > 0
            ? node.ForeignKeyReferencedColumns
            : definition.ForeignKeyReferencedColumns;

        var localColumns = node.ForeignKeyColumns.Count > 0
            ? node.ForeignKeyColumns
            : definition.ForeignKeyColumns;

        return definition with
        {
            ForeignKeyReferencedTable = referencedTableName,
            ForeignKeyReferencedSchemaName = referencedSchemaName,
            ForeignKeyColumns = NormalizeOrderedReferences(localColumns),
            ReferencedTables = referencedTables,
            ForeignKeyReferencedColumns = NormalizeOrderedReferences(referencedColumns),
            ForeignKeyUpdateAction = node.ForeignKeyUpdateAction ?? definition.ForeignKeyUpdateAction,
            ForeignKeyDeleteAction = node.ForeignKeyDeleteAction ?? definition.ForeignKeyDeleteAction,
            ForeignKeyDeleteSetColumns = NormalizeOrderedReferences(node.ForeignKeyDeleteSetColumns.Count > 0 ? node.ForeignKeyDeleteSetColumns : definition.ForeignKeyDeleteSetColumns),
            ForeignKeyMatchType = node.ForeignKeyMatchType ?? definition.ForeignKeyMatchType,
        };
    }

    private ConstraintDefinition MapCheck(ConstraintNode node, ConstraintDefinition definition, SqlDependencyVisitor visitor)
    {
        var mappedDefinition = AttachConstraintDependencies(node, definition, visitor);

        // TODO: Traverse parsed CHECK predicates, CASE expressions, subqueries, and complex boolean trees when PgProj.Parser surfaces them.
        return mappedDefinition;
    }

    private ConstraintDefinition MapExclusion(ConstraintNode node, ConstraintDefinition definition, SqlDependencyVisitor visitor)
    {
        var mappedDefinition = AttachConstraintDependencies(node, definition, visitor);

        return mappedDefinition with
        {
            ExclusionAccessMethod = node.ExclusionAccessMethod ?? definition.ExclusionAccessMethod,
            ExclusionElements = node.ExclusionElements.Count > 0
                ? node.ExclusionElements
                    .Select(static element => new ConstraintExclusionElementDefinition(element.Name, element.RawExpression, element.OperatorName) { ExpressionAst = element.ExpressionAst })
                    .ToArray()
                : definition.ExclusionElements,
            RawExclusionPredicateExpression = node.RawExclusionPredicateExpression ?? definition.RawExclusionPredicateExpression,
        };
    }

    private static ConstraintDefinition AttachConstraintDependencies(ConstraintNode node, ConstraintDefinition definition, SqlDependencyVisitor visitor)
    {
        visitor.Visit(node);
        return definition;
    }

    private ConstraintDefinition AttachDependencies(ConstraintDefinition definition, DependencyInfo info)
    {
        var referencedTables = Merge(definition.ReferencedTables, info.ReferencedTables.Select(name => ResolveTableKey(definition.SchemaName, name)));
        var referencedColumns = Merge(definition.ReferencedColumns, info.ReferencedColumns.Select(name => ResolveColumnKey(definition.SchemaName, name)));
        var referencedTypes = Merge(definition.ReferencedTypes, info.ReferencedTypes.Select(name => ResolveTypeKey(definition.SchemaName, name)));
        var referencedFunctions = Merge(definition.ReferencedFunctions, info.ReferencedFunctions.Select(name => ResolveFunctionKey(definition.SchemaName, name)));

        return definition with
        {
            ReferencedTables = referencedTables,
            ReferencedColumns = referencedColumns,
            ReferencedTypes = referencedTypes,
            ReferencedFunctions = referencedFunctions,
        };
    }

    private void AddDiagnostics(
        ConstraintNode node,
        ConstraintDefinition definition,
        ICollection<SemanticDiagnostic> diagnostics)
    {
        if (!TryResolveTableKey(node.SchemaName, definition.TableName, out _))
        {
            AddDiagnostic(
                diagnostics,
                $"Constraint '{definition.Key}' references missing table '{FormatReference(node.SchemaName, definition.TableName)}'.",
                nameof(DeepConstraintMappingPass),
                SchemaScopeSeverity.ForReference(_model, node.SchemaName), location: definition.Location);
        }

        if (!string.IsNullOrWhiteSpace(definition.ForeignKeyReferencedTable)
            && !TryResolveTableKey(definition.ForeignKeyReferencedSchemaName ?? node.SchemaName, definition.ForeignKeyReferencedTable, out _))
        {
            var referencedSchema = string.IsNullOrWhiteSpace(definition.ForeignKeyReferencedSchemaName)
                ? SchemaScopeSeverity.SchemaOfObjectReference(definition.ForeignKeyReferencedTable)
                : definition.ForeignKeyReferencedSchemaName;
            AddDiagnostic(
                diagnostics,
                $"Constraint '{definition.Key}' references missing foreign key table '{FormatReference(definition.ForeignKeyReferencedSchemaName ?? node.SchemaName, definition.ForeignKeyReferencedTable)}'.",
                nameof(DeepConstraintMappingPass),
                SchemaScopeSeverity.ForReference(_model, referencedSchema), location: definition.ReferencedTableLocation ?? definition.Location);
        }
    }

    private static void AddDiagnostic(ICollection<SemanticDiagnostic> diagnostics, string message, string source, SemanticDiagnosticSeverity severity = SemanticDiagnosticSeverity.Error, SemanticLocation? location = null)
        => diagnostics.Add(severity == SemanticDiagnosticSeverity.Warning
            ? SemanticDiagnostics.Warning(message, location, source)
            : SemanticDiagnostics.Error(message, location, source));

    private bool TryResolveTableKey(string? schemaName, string? tableName, out string tableKey)
    {
        tableKey = string.Empty;

        if (string.IsNullOrWhiteSpace(tableName))
        {
            return false;
        }

        tableKey = DatabaseModel.CreateKey(schemaName, tableName);
        if (_model.Tables.ContainsKey(tableKey))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            return false;
        }

        foreach (var table in _model.Tables.Values)
        {
            if (string.Equals(table.Name, tableName, StringComparison.Ordinal))
            {
                tableKey = table.Key;
                return true;
            }
        }

        return false;
    }

    private string ResolveTableKey(string? schemaName, string? name)
        => ResolveKey<TableDefinition>(name, schemaName, _model.TryGetTable);

    private string ResolveFunctionKey(string? schemaName, string? name)
        => ResolveKey<FunctionDefinition>(name, schemaName, _model.TryGetFunction);

    private string ResolveTypeKey(string? schemaName, string? name)
        => ResolveKey<TypeDefinition>(name, schemaName, _model.TryGetType);

    private string ResolveColumnKey(string? schemaName, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var reference = name.Trim();

        foreach (var column in _model.Tables.Values.SelectMany(static table => table.Columns))
        {
            if (string.Equals(column.Key, reference, StringComparison.Ordinal))
            {
                return column.Key;
            }

            var tableQualifiedName = column.TableName is null ? column.Name : $"{column.TableName}.{column.Name}";
            if (string.Equals(tableQualifiedName, reference, StringComparison.Ordinal))
            {
                return column.Key;
            }
        }

        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            var scopedReference = DatabaseModel.CreateKey(schemaName, reference);
            foreach (var column in _model.Tables.Values.SelectMany(static table => table.Columns))
            {
                if (string.Equals(column.Key, scopedReference, StringComparison.Ordinal))
                {
                    return column.Key;
                }
            }
        }

        return reference;
    }

    private static IReadOnlyList<string> NormalizeReferences(IEnumerable<string> references)
        => references
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(PgProj.Parser.SqlIdentifiers.NameComparer)
            .ToArray();

    /// <summary>
    /// Trims and de-duplicates references while preserving their original order. Use this for
    /// order-sensitive column lists (e.g. primary key and foreign key column sequences) where
    /// <see cref="NormalizeReferences"/>'s ordering guarantees are not sufficient to document intent.
    /// </summary>
    private static IReadOnlyList<string> NormalizeOrderedReferences(IEnumerable<string> references)
        => references
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(PgProj.Parser.SqlIdentifiers.NameComparer)
            .ToArray();

    private static IReadOnlyList<string> Merge(IEnumerable<string> current, IEnumerable<string> additional)
        => current
            .Concat(additional)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ResolveKey<TNode>(
        string? reference,
        string? schemaName,
        TryGetNode<TNode> tryGetNode)
        where TNode : class, ISemanticNode
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        var trimmedReference = reference.Trim();
        if (tryGetNode(trimmedReference, out var node) && node is not null)
        {
            return node.Key;
        }

        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            var scopedReference = DatabaseModel.CreateKey(schemaName, trimmedReference);
            if (tryGetNode(scopedReference, out node) && node is not null)
            {
                return node.Key;
            }

            // Unresolved: keep the schema qualifier in the placeholder (rather than the bare name) so a
            // diagnostic reporting this reference as missing can still tell which schema it named (G-83).
            if (trimmedReference.Contains('.', StringComparison.Ordinal))
            {
                return trimmedReference;
            }

            return scopedReference;
        }

        return trimmedReference;
    }

    private static string? NormalizeConstraintType(string? constraintType)
    {
        if (string.IsNullOrWhiteSpace(constraintType))
        {
            return null;
        }

        var normalizedValue = constraintType.Trim().Replace('_', ' ').ToUpperInvariant();
        return normalizedValue switch
        {
            "P" or "PK" or "CONSTR PRIMARY" or ConstraintKinds.PrimaryKey => ConstraintKinds.PrimaryKey,
            "U" or "CONSTR UNIQUE" or ConstraintKinds.Unique => ConstraintKinds.Unique,
            "F" or "FK" or "CONSTR FOREIGN" or ConstraintKinds.ForeignKey => ConstraintKinds.ForeignKey,
            "C" or "CONSTR CHECK" or ConstraintKinds.Check => ConstraintKinds.Check,
            "X" or "CONSTR EXCLUSION" or ConstraintKinds.Exclusion => ConstraintKinds.Exclusion,
            _ => constraintType.Trim(),
        };
    }

    private static string ResolveConstraintDefinitionKey(ConstraintNode node)
        => DatabaseModel.CreateConstraintKey(node.SchemaName, node.TableName, node.Name);

    private static string FormatReference(string? schemaName, string? objectName)
        => string.IsNullOrWhiteSpace(objectName)
            ? "<unknown>"
            : string.IsNullOrWhiteSpace(schemaName)
                ? objectName.Trim()
                : DatabaseModel.CreateKey(schemaName, objectName);

    private delegate bool TryGetNode<TNode>(string key, out TNode? node)
        where TNode : class, ISemanticNode;

    private static class ConstraintKinds
    {
        public const string PrimaryKey = "PRIMARY KEY";
        public const string Unique = "UNIQUE";
        public const string ForeignKey = "FOREIGN KEY";
        public const string Check = "CHECK";
        public const string Exclusion = "EXCLUDE";
    }
}
