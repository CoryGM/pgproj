// Step 10 — SemanticBinder (Deep Binding)
// Implement the deep SemanticBinder for the semantic pipeline.
// This pass performs *semantic binding* — converting resolved names and types
// into fully bound semantic references. It runs AFTER TypeResolutionPass and
// BEFORE ExpressionNormalizationPass.
//
// === Goals ===
// 1. Bind resolved references into actual semantic objects:
//      - ResolvedTables        → BoundTables
//      - ResolvedColumns       → BoundColumns
//      - ResolvedTypes         → BoundTypes
//      - ResolvedFunctions     → BoundFunctions
// 2. Bind foreign key relationships:
//      - referenced table
//      - referenced columns
// 3. Bind index column references.
// 4. Bind constraint column references (PK, UNIQUE, CHECK, FK).
// 5. Bind function argument and return types.
// 6. Bind view column references (if exposed).
// 7. Emit diagnostics for binding failures.
// 8. Do NOT:
//      - normalize expressions (ExpressionNormalizationPass)
//      - validate expressions (ValidationPass)
//      - perform type coercion (ExpressionNormalizationPass)
//      - perform dependency correctness validation (DeepDependencyPass)
// 9. SemanticBinder must remain binding‑only.
//
// === Requirements ===
// - Create a SemanticBinder class with:
//       DatabaseModel Execute(DatabaseModel model)
// - For each semantic definition:
//       - Bind table references
//       - Bind column references
//       - Bind type references
//       - Bind function references
//       - Bind foreign key relationships
//       - Bind index column references
//       - Bind constraint column references
//       - Populate BoundTables / BoundColumns / BoundTypes / BoundFunctions
//       - Emit diagnostics for unresolved bindings
// - Add helper methods:
//       - BindTable(string key)
//       - BindColumn(string key)
//       - BindType(string key)
//       - BindFunction(string key)
//       - BindForeignKey(ConstraintDefinition constraint)
//       - BindIndex(IndexDefinition index)
//       - BindConstraint(ConstraintDefinition constraint)
//       - AddDiagnostic(List<SemanticDiagnostic> diagnostics, string message, string source)
//
// === Binding Rules ===
// - Table binding:
//       * resolved key must exist in model.Tables
// - Column binding:
//       * resolved key must exist in owning table.Columns
// - Type binding:
//       * resolved key must exist in model.Types
// - Function binding:
//       * resolved key must exist in model.Functions
// - Foreign key binding:
//       * referenced table must exist
//       * referenced columns must exist on referenced table
// - Index binding:
//       * referenced columns must exist on owning table
// - Constraint binding:
//       * PK/UNIQUE columns must exist on owning table
//       * FK columns must exist on owning table
//       * referenced FK columns must exist on referenced table
//
// === Diagnostics ===
// - Emit errors for unresolved bindings:
//       * missing tables
//       * missing columns
//       * missing types
//       * missing functions
//       * missing referenced FK columns
//       * missing referenced tables
//
// === TODO for deeper semantic handling (later passes) ===
// - Expression normalization (Step 11 — ExpressionNormalizationPass)
// - CHECK expression binding (Step 12)
// - Function body semantic mapping (Step 12)
// - View expression semantic mapping (Step 13)
// - DiffEngine semantic comparison (Step 14)
//
// === Copilot Guidance ===
// Generate scaffolding and initial implementations.
// Focus on semantic binding only.
// Emit diagnostics for unresolved bindings.
// Leave TODO comments for deeper semantic handling.
// Begin by generating SemanticBinder.cs with an Execute() method and stubs for
// BindTable(), BindColumn(), BindType(), BindFunction(), BindForeignKey(),
// BindIndex(), BindConstraint(), and AddDiagnostic().


namespace PgProj.Semantics;

internal sealed class SemanticBinder
{
    private DatabaseModel _model = DatabaseModel.Empty;
    private SymbolTable _symbolTable = new(DatabaseModel.Empty);
    private List<SemanticDiagnostic> _diagnostics = [];

    // The missing types TypeResolutionPass already reported, by message: the binder does not say the same thing again (G-326).
    private HashSet<string> _reportedMissingTypes = new(StringComparer.Ordinal);
    private ISemanticNode? _currentOwner;
    private TableDefinition? _currentOwningTable;

    public DatabaseModel Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model;
        _symbolTable = new SymbolTable(model);
        _diagnostics = [];
        _reportedMissingTypes = model.Diagnostics
            .Select(static diagnostic => diagnostic.Message)
            .Where(static message => message.Contains(" references missing type ", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var tables = model.Tables.Values
            .Select(BindTable)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var views = model.Views.Values
            .Select(BindView)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var functions = model.Functions.Values
            .Select(BindFunction)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var types = model.Types.Values
            .Select(type => (TypeDefinition)BindNode(type))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var indexes = model.Indexes.Values
            .Select(BindIndex)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var constraints = model.Constraints.Values
            .Select(BindConstraint)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var grants = model.Grants.Values
            .Select(grant => (GrantDefinition)BindNode(grant))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);

        var diagnostics = model.Diagnostics.Concat(_diagnostics).ToList();

        return model with
        {
            Tables = tables,
            Views = views,
            Functions = functions,
            Types = types,
            Indexes = indexes,
            Constraints = constraints,
            Grants = grants,
            Diagnostics = diagnostics,
        };
    }

    private TableDefinition BindTable(TableDefinition table)
    {
        var boundColumns = table.Columns
            .Select(column => BindColumn(column, table))
            .ToArray();

        var boundNode = (TableDefinition)BindNode(table, table);

        // TODO: Bind column default expressions once richer expression semantics are available.
        return boundNode with
        {
            Columns = boundColumns,
        };
    }

    private ColumnDefinition BindColumn(ColumnDefinition column, TableDefinition owningTable)
    {
        // TODO: Bind column default expressions and generated expressions against the resolved symbol table.
        return (ColumnDefinition)BindNode(column, owningTable);
    }

    private ViewDefinition BindView(ViewDefinition view)
    {
        // TODO: Bind view SELECT expressions, joins, and predicates against resolved semantic symbols.
        return (ViewDefinition)BindNode(view);
    }

    private FunctionDefinition BindFunction(FunctionDefinition function)
    {
        // TODO: Bind function bodies against resolved table, column, type, and function symbols.
        return (FunctionDefinition)BindNode(function);
    }

    private IndexDefinition BindIndex(IndexDefinition index)
    {
        var owningTable = ResolveOwningTable(index.SchemaName, index.TableName);
        var boundIndex = (IndexDefinition)BindNode(index, owningTable);

        BindIndex(index, owningTable, boundIndex.ResolvedColumns);

        // TODO: Bind index expressions and predicate clauses once deeper index metadata is modeled.
        return boundIndex;
    }

    private ConstraintDefinition BindConstraint(ConstraintDefinition constraint)
    {
        var owningTable = ResolveOwningTable(constraint.SchemaName, constraint.TableName);
        var boundConstraint = (ConstraintDefinition)BindNode(constraint, owningTable);

        BindConstraint(boundConstraint, owningTable);
        BindForeignKey(boundConstraint);

        // TODO: Bind constraint expressions once richer constraint semantics are modeled.
        return boundConstraint;
    }

    private ISemanticNode BindNode(ISemanticNode node, TableDefinition? owningTable = null)
    {
        var previousOwner = _currentOwner;
        var previousOwningTable = _currentOwningTable;

        _currentOwner = node;
        _currentOwningTable = owningTable;

        try
        {
            var resolvedTables = ResolveTableReferences(node);
            var resolvedColumns = ResolveColumnReferences(node);
            var resolvedTypes = ResolveTypeReferences(node);
            var resolvedFunctions = ResolveFunctionReferences(node);

            return node switch
            {
                TableDefinition table => table with
                {
                    ResolvedTables = resolvedTables.Nodes,
                    ResolvedColumns = resolvedColumns.Nodes,
                    ResolvedTypes = resolvedTypes.Nodes,
                    ResolvedFunctions = resolvedFunctions.Nodes,
                },
                ColumnDefinition column => column with
                {
                    ResolvedTables = resolvedTables.Nodes,
                    ResolvedColumns = resolvedColumns.Nodes,
                    ResolvedTypes = resolvedTypes.Nodes,
                    ResolvedFunctions = resolvedFunctions.Nodes,
                },
                ViewDefinition view => view with
                {
                    ResolvedTables = resolvedTables.Nodes,
                    ResolvedColumns = resolvedColumns.Nodes,
                    ResolvedTypes = resolvedTypes.Nodes,
                    ResolvedFunctions = resolvedFunctions.Nodes,
                },
                FunctionDefinition function => function with
                {
                    ResolvedTables = resolvedTables.Nodes,
                    ResolvedColumns = resolvedColumns.Nodes,
                    ResolvedTypes = resolvedTypes.Nodes,
                    ResolvedFunctions = resolvedFunctions.Nodes,
                },
                TypeDefinition type => type with
                {
                    ResolvedTables = resolvedTables.Nodes,
                    ResolvedColumns = resolvedColumns.Nodes,
                    ResolvedTypes = resolvedTypes.Nodes,
                    ResolvedFunctions = resolvedFunctions.Nodes,
                },
                IndexDefinition index => index with
                {
                    ResolvedTables = resolvedTables.Nodes,
                    ResolvedColumns = resolvedColumns.Nodes,
                    ResolvedTypes = resolvedTypes.Nodes,
                    ResolvedFunctions = resolvedFunctions.Nodes,
                },
                ConstraintDefinition constraint => constraint with
                {
                    ResolvedTables = resolvedTables.Nodes,
                    ResolvedColumns = resolvedColumns.Nodes,
                    ResolvedTypes = resolvedTypes.Nodes,
                    ResolvedFunctions = resolvedFunctions.Nodes,
                },
                GrantDefinition grant => grant with
                {
                    ResolvedTables = resolvedTables.Nodes,
                    ResolvedColumns = resolvedColumns.Nodes,
                    ResolvedTypes = resolvedTypes.Nodes,
                    ResolvedFunctions = resolvedFunctions.Nodes,
                },
                _ => node,
            };
        }
        finally
        {
            _currentOwner = previousOwner;
            _currentOwningTable = previousOwningTable;
        }
    }

    private ResolvedReferenceSet<TableDefinition> ResolveTableReferences(ISemanticNode owner)
    {
        var nodes = new List<TableDefinition>();
        var seenKeys = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var reference in owner.ReferencedTables)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            var trimmedReference = reference.Trim();
            if (PostgreSqlSystemReferences.IsBuiltInRelation(trimmedReference))
            {
                continue;
            }

            var resolvedNode = BindTable(trimmedReference) ?? ResolveTable(owner.SchemaName, trimmedReference);
            if (resolvedNode is not null)
            {
                if (seenKeys.Add(resolvedNode.Key))
                {
                    nodes.Add(resolvedNode);
                }

                continue;
            }

            if (owner is IndexDefinition && _model.FindMaterializedView(owner.SchemaName, trimmedReference) is not null)
            {
                // The relation an index is on may be a materialized view (G-171).
                continue;
            }

            if (_model.FindSequence(owner.SchemaName, trimmedReference) is not null)
            {
                // A query may read a sequence as a relation (G-231); the dependency analyzer orders it after the sequence.
                continue;
            }

            if (owner is ConstraintDefinition constraint && IsForeignKeyTarget(constraint, trimmedReference))
            {
                // A foreign key's own table is reported by DeepConstraintMappingPass, as a missing foreign key table (G-325).
                continue;
            }

            AddDiagnostic(
                _diagnostics,
                $"{owner.Kind} '{owner.Key}' references missing table '{trimmedReference}'.",
                nameof(SemanticBinder),
                SchemaScopeSeverity.ForReference(_model, SchemaScopeSeverity.SchemaOfObjectReference(trimmedReference)),
                PlaceOfTable(owner, trimmedReference));
        }

        return new ResolvedReferenceSet<TableDefinition>(nodes.ToArray());
    }

    /// <summary>Whether <paramref name="reference"/> names the table <paramref name="constraint"/>, a foreign key, references.</summary>
    private static bool IsForeignKeyTarget(ConstraintDefinition constraint, string reference)
    {
        if (string.IsNullOrWhiteSpace(constraint.ForeignKeyReferencedTable))
        {
            return false;
        }

        var comparer = PgProj.Parser.SqlIdentifiers.NameComparer;
        var schema = constraint.ForeignKeyReferencedSchemaName ?? constraint.SchemaName;
        return comparer.Equals(reference, constraint.ForeignKeyReferencedTable)
            || (!string.IsNullOrWhiteSpace(schema) && comparer.Equals(reference, DatabaseModel.CreateKey(schema, constraint.ForeignKeyReferencedTable)));
    }

        private ResolvedReferenceSet<ColumnDefinition> ResolveColumnReferences(ISemanticNode owner)
    {
        var nodes = new List<ColumnDefinition>();
        var seenKeys = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var reference in owner.ReferencedColumns)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            var trimmedReference = reference.Trim();
            // An unqualified name is the owning table's column first (G-229).
            var resolvedNode = _currentOwningTable is not null && !trimmedReference.Contains('.', StringComparison.Ordinal)
                ? ResolveColumn(owner.SchemaName, trimmedReference)
                : BindColumn(trimmedReference) ?? ResolveColumn(owner.SchemaName, trimmedReference);
            if (resolvedNode is not null)
            {
                if (seenKeys.Add(resolvedNode.Key))
                {
                    nodes.Add(resolvedNode);
                }

                continue;
            }

            if (owner is IndexDefinition indexOwner && _model.FindMaterializedView(indexOwner.SchemaName, indexOwner.TableName) is not null)
            {
                // The columns of a materialized view are its query's output columns, which are not modeled (G-171).
                continue;
            }

            AddDiagnostic(
                _diagnostics,
                $"{owner.Kind} '{owner.Key}' references missing column '{trimmedReference}'.",
                nameof(SemanticBinder),
                SchemaScopeSeverity.ForReference(_model, SchemaScopeSeverity.SchemaOfColumnReference(trimmedReference)),
                owner.Location);
        }

        return new ResolvedReferenceSet<ColumnDefinition>(nodes.ToArray());
    }

    private ResolvedReferenceSet<FunctionDefinition> ResolveFunctionReferences(ISemanticNode owner)
    {
        var nodes = new List<FunctionDefinition>();
        var seenKeys = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var reference in owner.ReferencedFunctions)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            var trimmedReference = reference.Trim();
            var resolvedNode = BindFunction(trimmedReference) ?? ResolveFunction(owner.SchemaName, trimmedReference);
            if (resolvedNode is not null)
            {
                if (seenKeys.Add(resolvedNode.Key))
                {
                    nodes.Add(resolvedNode);
                }

                continue;
            }

            // An aggregate is not a FunctionDefinition, but it is a function a call can name: resolved, with an edge in the dependency graph (G-130).
            if (AggregateReferences.Find(_model, owner.SchemaName, trimmedReference).Count > 0)
            {
                continue;
            }

            // A PostgreSQL built-in is not a project object: no edge, and not a missing reference (G-51).
            if (BuiltInFunctions.IsBuiltIn(trimmedReference))
            {
                continue;
            }

            AddDiagnostic(
                _diagnostics,
                $"{owner.Kind} '{owner.Key}' references missing function '{trimmedReference}'.",
                nameof(SemanticBinder),
                SchemaScopeSeverity.Lenient(
                    BuiltInFunctions.MissingFunctionSeverity(_model),
                    SchemaScopeSeverity.ForReference(_model, SchemaScopeSeverity.SchemaOfObjectReference(trimmedReference))),
                owner.Location);
        }

        return new ResolvedReferenceSet<FunctionDefinition>(nodes.ToArray());
    }

    private ResolvedReferenceSet<TypeDefinition> ResolveTypeReferences(ISemanticNode owner)
    {
        var nodes = new List<TypeDefinition>();
        var seenKeys = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var reference in owner.ReferencedTypes)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            var trimmedReference = reference.Trim();
            var resolvedType = BindType(trimmedReference) ?? ResolveType(owner.SchemaName, trimmedReference);
            if (resolvedType is not null)
            {
                if (seenKeys.Add(resolvedType.Key))
                {
                    nodes.Add(resolvedType);
                }

                continue;
            }

            var message = $"{owner.Kind} '{owner.Key}' references missing type '{trimmedReference}'.";
            if (!IsKnownBuiltInType(trimmedReference) && !IsRowType(owner.SchemaName, trimmedReference)
                && MultirangeTypes.FindRange(_model, owner.SchemaName, trimmedReference) is null
                && !_reportedMissingTypes.Contains(message)
                && !IsColumnType(owner, trimmedReference))
            {
                AddDiagnostic(
                    _diagnostics,
                    message,
                    nameof(SemanticBinder),
                    SchemaScopeSeverity.ForReference(_model, SchemaScopeSeverity.SchemaOfObjectReference(trimmedReference)),
                    PlaceOfType(owner, trimmedReference));
            }
        }

        return new ResolvedReferenceSet<TypeDefinition>(nodes.ToArray());
    }

    /// <summary>Whether <paramref name="reference"/> is a type one of <paramref name="owner"/>'s columns names: the column reports it, not its table (G-326).</summary>
    private static bool IsColumnType(ISemanticNode owner, string reference)
        => owner is TableDefinition table
            && table.Columns.Any(column => column.ReferencedTypes.Contains(reference, PgProj.Parser.SqlIdentifiers.NameComparer));

    /// <summary>
    /// A table or view name is also a type (its row type), so <c>RETURNS SETOF public.orders</c> or a parameter of a
    /// table's row type is a valid type reference rather than a missing type (G-50).
    /// </summary>
    private bool IsRowType(string? schemaName, string reference)
    {
        if (BindTable(reference) is not null || ResolveTable(schemaName, reference) is not null)
        {
            return true;
        }

        if (_model.TryGetView(reference, out var view) && view is not null)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(schemaName)
            && _model.TryGetView(DatabaseModel.CreateKey(schemaName, reference), out view)
            && view is not null;
    }

    private bool IsKnownBuiltInType(string typeReference)
    {
        if (string.IsNullOrWhiteSpace(typeReference))
        {
            return false;
        }

        var normalizedReference = NormalizeTypeReference(typeReference);
        var simpleName = GetSimpleName(normalizedReference);
        return _symbolTable.IsKnownBuiltInType(normalizedReference)
            || _symbolTable.IsKnownBuiltInType(simpleName)
            || IsSupplementalBuiltInType(simpleName);
    }

    private static bool IsSupplementalBuiltInType(string typeName)
        => typeName is "bit varying"
            or "daterange"
            or "int4range"
            or "int8range"
            or "numrange"
            or "time with time zone"
            or "time without time zone"
            or "timestamp with time zone"
            or "timestamp without time zone"
            or "tsmultirange"
            or "tsquery"
            or "tsrange"
            or "tstzmultirange"
            or "tstzrange";

    private static string NormalizeTypeReference(string typeReference)
    {
        var normalized = typeReference.Trim();
        while (normalized.EndsWith("[]", StringComparison.Ordinal))
        {
            normalized = normalized[..^2].TrimEnd();
        }

        var typeModifierIndex = normalized.IndexOf('(');
        if (typeModifierIndex >= 0)
        {
            normalized = normalized[..typeModifierIndex].TrimEnd();
        }

        normalized = string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        const string pgCatalogPrefix = "pg_catalog.";
        if (normalized.StartsWith(pgCatalogPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[pgCatalogPrefix.Length..];
        }

        return normalized;
    }

    private static string GetSimpleName(string typeReference)
    {
        var separatorIndex = typeReference.IndexOf('.');
        return separatorIndex <= 0 || separatorIndex >= typeReference.Length - 1
            ? typeReference
            : typeReference[(separatorIndex + 1)..].Trim();
    }

    private TableDefinition? BindTable(string key)
        => _model.TryGetTable(key, out var table) && table is not null ? table : null;

    private ColumnDefinition? BindColumn(string key)
        => _symbolTable.TryGetColumn(key, out var column) && column is not null ? column : null;

    private TypeDefinition? BindType(string key)
        => _model.TryGetType(key, out var type) && type is not null ? type : null;

    private FunctionDefinition? BindFunction(string key)
        => _model.TryGetFunction(key, out var function) && function is not null ? function : null;

    private void BindForeignKey(ConstraintDefinition constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint.ForeignKeyReferencedTable))
        {
            return;
        }

        var referencedTable = ResolveTable(constraint.SchemaName, constraint.ForeignKeyReferencedTable);
        if (referencedTable is null)
        {
            // DeepConstraintMappingPass reports a missing foreign key table once, schema-qualified, at its name; reporting it here as well said it three
            // times, once without its schema (G-325).
            return;
        }

        foreach (var referencedColumnName in constraint.ForeignKeyReferencedColumns)
        {
            if (string.IsNullOrWhiteSpace(referencedColumnName))
            {
                continue;
            }

            if (!referencedTable.Columns.Any(column => string.Equals(column.Name, referencedColumnName, StringComparison.Ordinal)))
            {
                AddDiagnostic(
                    _diagnostics,
                    $"Constraint '{constraint.Key}' references missing foreign key column '{referencedColumnName}' on table '{referencedTable.Key}'.",
                    nameof(SemanticBinder),
                    SchemaScopeSeverity.ForReference(_model, referencedTable.SchemaName),
                    constraint.Location);
            }
        }
    }

    private void BindIndex(IndexDefinition index, TableDefinition? owningTable, IReadOnlyList<ColumnDefinition> boundColumns)
    {
        if (owningTable is null && _model.FindMaterializedView(index.SchemaName, index.TableName) is not null)
        {
            // An index on a materialized view (G-171): its columns are the view's output columns, which are not modeled as columns.
            return;
        }

        if (owningTable is null)
        {
            AddDiagnostic(
                _diagnostics,
                $"Index '{index.Key}' references missing owning table '{index.TableName ?? "<null>"}'.",
                nameof(SemanticBinder),
                SchemaScopeSeverity.ForReference(_model, index.SchemaName),
                index.Location);
            return;
        }

        foreach (var boundColumn in boundColumns)
        {
            if (!owningTable.Columns.Any(column => string.Equals(column.Key, boundColumn.Key, StringComparison.Ordinal)))
            {
                AddDiagnostic(_diagnostics, $"Index '{index.Key}' binds column '{boundColumn.Key}' that does not belong to table '{owningTable.Key}'.", nameof(SemanticBinder), location: index.Location);
            }
        }
    }

    private void BindConstraint(ConstraintDefinition constraint, TableDefinition? owningTable)
    {
        if (owningTable is null)
        {
            AddDiagnostic(
                _diagnostics,
                $"Constraint '{constraint.Key}' references missing owning table '{constraint.TableName ?? "<null>"}'.",
                nameof(SemanticBinder),
                SchemaScopeSeverity.ForReference(_model, constraint.SchemaName),
                constraint.Location);
            return;
        }

        BindConstraintColumns(constraint, owningTable, constraint.PrimaryKeyColumns, "primary key");
        BindConstraintColumns(constraint, owningTable, constraint.UniqueColumns, "unique");
    }

    private void BindConstraintColumns(
        ConstraintDefinition constraint,
        TableDefinition owningTable,
        IEnumerable<string> columnNames,
        string constraintKind)
    {
        foreach (var columnName in columnNames)
        {
            if (string.IsNullOrWhiteSpace(columnName))
            {
                continue;
            }

            if (!owningTable.Columns.Any(column => string.Equals(column.Name, columnName, StringComparison.Ordinal)))
            {
                AddDiagnostic(
                    _diagnostics,
                    $"Constraint '{constraint.Key}' references missing {constraintKind} column '{columnName}' on table '{owningTable.Key}'.",
                    nameof(SemanticBinder),
                    SchemaScopeSeverity.ForReference(_model, owningTable.SchemaName),
                    constraint.Location);
            }
        }
    }

    private TableDefinition? ResolveTable(string? schemaName, string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        var trimmedReference = rawName.Trim();
        if (_model.TryGetTable(trimmedReference, out var directMatch) && directMatch is not null)
        {
            return directMatch;
        }

        if (!string.IsNullOrWhiteSpace(schemaName)
            && _model.TryGetTable(DatabaseModel.CreateKey(schemaName, trimmedReference), out var scopedMatch)
            && scopedMatch is not null)
        {
            return scopedMatch;
        }

        return _model.Tables.Values.FirstOrDefault(candidate => string.Equals(candidate.Name, trimmedReference, StringComparison.Ordinal));
    }

    private ColumnDefinition? ResolveColumn(string? schemaName, string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        var trimmedReference = rawName.Trim();

        // An unqualified name in an index, constraint, or other object on a table is that table's column (G-229). The symbol table, looked at first, could
        // return another table's column of the same name, and build then refused a valid index.
        if (_currentOwningTable is not null && !trimmedReference.Contains('.', StringComparison.Ordinal)
            && _currentOwningTable.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, trimmedReference, StringComparison.Ordinal)) is { } ownColumn)
        {
            return ownColumn;
        }

        if (_symbolTable.TryGetColumn(trimmedReference, out var directMatch) && directMatch is not null)
        {
            return directMatch;
        }

        if (TryResolveQualifiedColumn(schemaName, trimmedReference, out var qualifiedMatch))
        {
            return qualifiedMatch;
        }

        if (_currentOwningTable is not null)
        {
            var owningMatch = _currentOwningTable.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, trimmedReference, StringComparison.Ordinal));
            if (owningMatch is not null)
            {
                return owningMatch;
            }
        }

        return _model.Tables.Values
            .SelectMany(static table => table.Columns)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, trimmedReference, StringComparison.Ordinal));
    }

    private TypeDefinition? ResolveType(string? schemaName, string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        var trimmedReference = rawName.Trim();
        if (_model.TryGetType(trimmedReference, out var directMatch) && directMatch is not null)
        {
            return directMatch;
        }

        if (!string.IsNullOrWhiteSpace(schemaName)
            && _model.TryGetType(DatabaseModel.CreateKey(schemaName, trimmedReference), out var scopedMatch)
            && scopedMatch is not null)
        {
            return scopedMatch;
        }

        return _model.Types.Values.FirstOrDefault(candidate => string.Equals(candidate.Name, trimmedReference, StringComparison.Ordinal));
    }

    // A schema-qualified reference (public.bump) used to match nothing here, because the fallback compared the bare declared
    // name with the whole reference (G-54).
    private FunctionDefinition? ResolveFunction(string? schemaName, string rawName)
        => FunctionReferences.Resolve(_model, schemaName, rawName);

    private TableDefinition? ResolveOwningTable(string? schemaName, string? tableName)
        => string.IsNullOrWhiteSpace(tableName) ? null : ResolveTable(schemaName, tableName);

    private bool TryResolveQualifiedColumn(string? schemaName, string rawName, out ColumnDefinition? column)
    {
        column = null;

        var separatorIndex = rawName.LastIndexOf('.');
        if (separatorIndex <= 0 || separatorIndex >= rawName.Length - 1)
        {
            return false;
        }

        var qualifier = rawName[..separatorIndex].Trim();
        var columnName = rawName[(separatorIndex + 1)..].Trim();

        var table = ResolveTable(schemaName, qualifier);
        if (table is not null)
        {
            column = table.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, columnName, StringComparison.Ordinal));
            if (column is not null)
            {
                return true;
            }
        }

        column = _model.Tables.Values
            .Where(candidate => string.Equals(candidate.SchemaName, qualifier, StringComparison.OrdinalIgnoreCase))
            .SelectMany(static candidate => candidate.Columns)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, columnName, StringComparison.Ordinal));

        return column is not null;
    }

    private static void AddDiagnostic(List<SemanticDiagnostic> diagnostics, string message, string source, SemanticDiagnosticSeverity severity = SemanticDiagnosticSeverity.Error, SemanticLocation? location = null)
        => diagnostics.Add(severity == SemanticDiagnosticSeverity.Warning
            ? SemanticDiagnostics.Warning(message, location, source)
            : SemanticDiagnostics.Error(message, location, source));

    /// <summary>Where a missing table is named (G-309e): a foreign key's referenced table where it is written, else the object that names it.</summary>
    private static SemanticLocation? PlaceOfTable(ISemanticNode owner, string reference)
        => owner is ConstraintDefinition { ReferencedTableLocation: { } named } constraint
            && ForeignKeyNames(constraint).Contains(reference, PgProj.Parser.SqlIdentifiers.NameComparer)
                ? named
                : owner.Location;

    private static IEnumerable<string> ForeignKeyNames(ConstraintDefinition constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint.ForeignKeyReferencedTable))
        {
            yield break;
        }

        yield return constraint.ForeignKeyReferencedTable;
        if (!string.IsNullOrWhiteSpace(constraint.ForeignKeyReferencedSchemaName))
        {
            yield return $"{constraint.ForeignKeyReferencedSchemaName}.{constraint.ForeignKeyReferencedTable}";
        }
    }

    private static SemanticLocation? PlaceOfType(ISemanticNode owner, string reference) => SemanticPlaces.OfType(owner, reference);

    private sealed record ResolvedReferenceSet<TNode>(IReadOnlyList<TNode> Nodes)
        where TNode : class, ISemanticNode;
}

internal sealed class SymbolTable
{
    // Built-in type names as the parser gives them: an unquoted name is already folded to lower case, so they are matched exactly, as
    // PostgreSQL matches them. A quoted name with a capital ("Name", "Date") is never a built-in (G-275).
    private static readonly HashSet<string> BuiltInTypeNames = new(StringComparer.Ordinal)
    {
        "bigint",
        "bigserial",
        "bit",
        "bool",
        "boolean",
        "box",
        "bytea",
        "char",
        "character",
        "character varying",
        "cidr",
        "date",
        "decimal",
        "double precision",
        "inet",
        "int",
        "int2",
        "int4",
        "int8",
        "integer",
        "interval",
        "json",
        "jsonb",
        "line",
        "lseg",
        "macaddr",
        "money",
        "numeric",
        "path",
        "pg_lsn",
        "point",
        "polygon",
        "real",
        "serial",
        "smallint",
        "smallserial",
        "text",
        "time",
        "timetz",
        "timestamp",
        "timestamptz",
        "tsquery",
        "tsvector",
        "txid_snapshot",
        "uuid",
        "varchar",
        "xml",

        // PostgreSQL's internal names for the SQL-standard spellings above (libpg_query emits these, so
        // "double precision" arrives as float8, "char(3)" as bpchar, and "bit varying" as varbit) (G-48).
        "bpchar",
        "float4",
        "float8",
        "varbit",

        // Further built-in base types.
        "cid",
        "circle",
        "datemultirange",
        "int2vector",
        "int4multirange",
        "int8multirange",
        "jsonpath",
        "macaddr8",
        "name",
        "nummultirange",
        "oid",
        "oidvector",
        "pg_snapshot",
        "regclass",
        "regconfig",
        "regdictionary",
        "regnamespace",
        "regoper",
        "regoperator",
        "regproc",
        "regprocedure",
        "regrole",
        "regtype",
        "tid",
        "xid",
        "xid8",

        // Pseudo-types, valid in routine signatures (RETURNS trigger, RETURNS void, ...) (G-50).
        "any",
        "anyarray",
        "anycompatible",
        "anycompatiblearray",
        "anycompatiblemultirange",
        "anycompatiblenonarray",
        "anycompatiblerange",
        "anyelement",
        "anyenum",
        "anymultirange",
        "anynonarray",
        "anyrange",
        "cstring",
        "event_trigger",
        "fdw_handler",
        "index_am_handler",
        "internal",
        "language_handler",
        "opaque",
        "record",
        "table_am_handler",
        "trigger",
        "tsm_handler",
        "unknown",
        "void",
    };

    private readonly Dictionary<string, TableDefinition> _tables = new(PgProj.Parser.SqlIdentifiers.NameComparer);
    private readonly Dictionary<string, ColumnDefinition> _columns = new(PgProj.Parser.SqlIdentifiers.NameComparer);
    private readonly Dictionary<string, FunctionDefinition> _functions = new(PgProj.Parser.SqlIdentifiers.NameComparer);
    private readonly Dictionary<string, TypeDefinition> _types = new(PgProj.Parser.SqlIdentifiers.NameComparer);

    public SymbolTable(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        Populate(model);
    }

    public bool TryGetTable(string key, out TableDefinition? table)
        => _tables.TryGetValue(NormalizeKey(key), out table);

    public bool TryGetColumn(string key, out ColumnDefinition? column)
        => _columns.TryGetValue(NormalizeKey(key), out column);

    public bool TryGetFunction(string key, out FunctionDefinition? function)
        => _functions.TryGetValue(NormalizeKey(key), out function);

    public bool TryGetType(string key, out TypeDefinition? type)
        => _types.TryGetValue(NormalizeKey(key), out type);

    public bool TryGetTable(ISemanticNode owner, string reference, out TableDefinition? table)
        => TryResolve(owner, reference, TryGetTable, out table);

    public bool TryGetColumn(ISemanticNode owner, string reference, out ColumnDefinition? column)
        => TryResolve(owner, reference, TryGetColumn, out column);

    public bool TryGetFunction(ISemanticNode owner, string reference, out FunctionDefinition? function)
        => TryResolve(owner, reference, TryGetFunction, out function);

    public bool TryGetType(ISemanticNode owner, string reference, out TypeDefinition? type)
        => TryResolve(owner, reference, TryGetType, out type);

    public TableDefinition? ResolveTable(ISemanticNode owner, string reference)
        => TryGetTable(owner, reference, out var table) ? table : null;

    public ColumnDefinition? ResolveColumn(ISemanticNode owner, string reference)
        => TryGetColumn(owner, reference, out var column) ? column : null;

    public FunctionDefinition? ResolveFunction(ISemanticNode owner, string reference)
        => TryGetFunction(owner, reference, out var function) ? function : null;

    public TypeDefinition? ResolveType(ISemanticNode owner, string reference)
        => TryGetType(owner, reference, out var type) ? type : null;

    public bool IsKnownBuiltInType(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var normalized = NormalizeTypeReference(reference);
        return BuiltInTypeNames.Contains(normalized);
    }

    private void Populate(DatabaseModel model)
    {
        foreach (var table in model.Tables.Values)
        {
            Register(_tables, table.Key, table);
            Register(_tables, table.Name, table);

            foreach (var column in table.Columns)
            {
                Register(_columns, column.Key, column);
                Register(_columns, column.Name, column);

                if (!string.IsNullOrWhiteSpace(column.TableName))
                {
                    Register(_columns, $"{column.TableName}.{column.Name}", column);
                }
            }
        }

        foreach (var function in model.Functions.Values)
        {
            Register(_functions, function.Key, function);
            Register(_functions, function.Name, function);
        }

        foreach (var type in model.Types.Values)
        {
            Register(_types, type.Key, type);
            Register(_types, type.Name, type);
        }
    }

    private static bool TryResolve<TNode>(
        ISemanticNode owner,
        string reference,
        TryGet<TNode> tryGet,
        out TNode? node)
        where TNode : class, ISemanticNode
    {
        if (tryGet(reference, out node) && node is not null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(owner.SchemaName))
        {
            var scopedReference = DatabaseModel.CreateKey(owner.SchemaName, reference);
            if (tryGet(scopedReference, out node) && node is not null)
            {
                return true;
            }
        }

        node = null;
        return false;
    }

    private static void Register<TNode>(IDictionary<string, TNode> symbols, string? key, TNode value)
        where TNode : class, ISemanticNode
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var normalized = NormalizeKey(key);
        if (!symbols.TryGetValue(normalized, out var existing))
        {
            symbols[normalized] = value;
            return;
        }

        if (!string.Equals(existing.Key, value.Key, StringComparison.Ordinal))
        {
            symbols.Remove(normalized);
        }
    }

    private static string NormalizeKey(string key)
        => key.Trim();

    private static string NormalizeTypeReference(string reference)
    {
        var normalized = NormalizeKey(reference);
        if (normalized.EndsWith("[]", StringComparison.Ordinal))
        {
            normalized = normalized[..^2];
        }

        const string pgCatalogPrefix = "pg_catalog.";
        if (normalized.StartsWith(pgCatalogPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[pgCatalogPrefix.Length..];
        }

        return normalized;
    }

    private delegate bool TryGet<TNode>(string key, out TNode? node)
        where TNode : class, ISemanticNode;
}