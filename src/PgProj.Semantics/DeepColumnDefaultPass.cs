using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Binds what each column's <c>DEFAULT</c> refers to (functions, cast types, and any column it names) from pg_query's tree for the
/// expression, <see cref="ColumnNode.DefaultExpressionAst"/> (G-90 part 2). It used to find the <c>CREATE TABLE</c> statement and the
/// column's <c>DEFAULT</c> clause in the file's text with regular expressions and scan that text for names, so a default added by
/// <c>ALTER TABLE ... ADD COLUMN</c> (not in the <c>CREATE TABLE</c> text) was never read, and words had to be told apart from names by
/// keyword lists. In the tree a call is a <c>FuncCall</c>, a cast a <c>TypeCast</c>, a column a <c>ColumnRef</c>, and nothing inside a
/// string literal is any of them.
/// </summary>
internal sealed class DeepColumnDefaultPass
{
    private DatabaseModel _model = DatabaseModel.Empty;
    private TableDefinition? _currentTable;
    private ColumnKeyLookup? _columns;
    private readonly HashSet<string> _qualifiedColumnErrors = new(PgProj.Parser.SqlIdentifiers.NameComparer);

    public DatabaseModel Execute(DatabaseModel model, IEnumerable<SqlSyntaxTree> syntaxTrees)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(syntaxTrees);

        _model = model;
        _columns = null;

        var tables = new Dictionary<string, TableDefinition>(model.Tables, PgProj.Parser.SqlIdentifiers.NameComparer);
        var diagnostics = new List<SemanticDiagnostic>(model.Diagnostics);

        foreach (var syntaxTree in syntaxTrees)
        {
            foreach (var tableNode in syntaxTree.Tables)
            {
                var tableKey = ResolveTableDefinitionKey(tableNode);
                if (!tables.TryGetValue(tableKey, out var tableDefinition))
                {
                    diagnostics.Add(SemanticDiagnostics.Error(
                        $"Deep column default mapping could not find semantic table '{tableKey}'.",
                        SemanticLocation.Of(syntaxTree.FilePath, tableNode.Range),
                        nameof(DeepColumnDefaultPass)));
                    continue;
                }

                _currentTable = tableDefinition;

                var columnsByKey = tableDefinition.Columns.ToDictionary(static column => column.Key, PgProj.Parser.SqlIdentifiers.NameComparer);

                foreach (var columnNode in tableNode.Columns)
                {
                    var columnKey = ResolveColumnDefinitionKey(tableDefinition.SchemaName, tableDefinition.Name, columnNode.Name);
                    if (!columnsByKey.TryGetValue(columnKey, out var columnDefinition))
                    {
                        diagnostics.Add(SemanticDiagnostics.Error(
                            $"Deep column default mapping could not find semantic column '{columnKey}'.",
                            SemanticLocation.Of(syntaxTree.FilePath, columnNode.Range ?? tableNode.Range),
                            nameof(DeepColumnDefaultPass)));
                        continue;
                    }

                    _qualifiedColumnErrors.Clear();

                    var visitor = new SqlDependencyVisitor();
                    columnsByKey[columnKey] = MapDefaultExpression(columnNode, columnDefinition, visitor);

                    AddDiagnostics(columnNode, columnDefinition, visitor.Info, diagnostics);
                }

                tables[tableKey] = tableDefinition with
                {
                    Columns = tableDefinition.Columns
                        .Select(column => columnsByKey.TryGetValue(column.Key, out var updatedColumn) ? updatedColumn : column)
                        .ToArray(),
                };
            }
        }

        return model with
        {
            Tables = tables,
            Diagnostics = diagnostics,
        };
    }

    private ColumnDefinition MapDefaultExpression(ColumnNode node, ColumnDefinition definition, SqlDependencyVisitor visitor)
    {
        // No written default: nothing to bind. A SERIAL column's DEFAULT nextval(...) is synthesized by the parser, which gives it no
        // tree; its implicit owned sequence is synthesized separately in ColumnPass.
        if (string.IsNullOrWhiteSpace(node.DefaultExpressionAst))
        {
            return definition;
        }

        visitor.Visit(node);

        var references = ExpressionAstReader.Collect(node.DefaultExpressionAst);
        foreach (var parts in references.Columns)
        {
            if (parts.Count == 1)
            {
                AddUnqualifiedColumn(parts[0], definition, visitor);
            }
            else
            {
                AddQualifiedColumn(string.Join('.', parts.Take(parts.Count - 1)), parts[^1], visitor);
            }
        }

        foreach (var function in references.Functions)
        {
            visitor.Info.AddFunction(function);
        }

        foreach (var type in references.Types)
        {
            visitor.Info.AddType(type);
        }

        return AttachDependencies(definition, visitor.Info) with
        {
            ReferencedOperators = OperatorReferences.Merge(definition.ReferencedOperators, OperatorReferences.Resolve(_model, references.Operators, definition.SchemaName)),
        };
    }

    private void AddQualifiedColumn(string qualifier, string columnName, SqlDependencyVisitor visitor)
    {
        if (TryResolveQualifiedColumnReference(qualifier, columnName, out var columnReference))
        {
            visitor.Info.AddColumn(columnReference);
            return;
        }

        var unresolvedReference = $"{qualifier}.{columnName}";
        visitor.Info.AddColumn(unresolvedReference);
        _qualifiedColumnErrors.Add(unresolvedReference);
    }

    /// <summary>A bare name that is another column of the same table (PostgreSQL rejects it in a DEFAULT, but it is still what it names).</summary>
    private void AddUnqualifiedColumn(string name, ColumnDefinition definition, SqlDependencyVisitor visitor)
    {
        var column = _currentTable?.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (column is not null && !string.Equals(column.Key, definition.Key, StringComparison.Ordinal))
        {
            visitor.Info.AddColumn(column.Key);
        }
    }

    private ColumnDefinition AttachDependencies(ColumnDefinition definition, DependencyInfo info)
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
        ColumnNode node,
        ColumnDefinition definition,
        DependencyInfo info,
        ICollection<SemanticDiagnostic> diagnostics)
    {
        foreach (var tableReference in info.ReferencedTables)
        {
            if (TryResolveTableKey(node.SchemaName, tableReference, out _))
            {
                continue;
            }

            AddDiagnostic(
                diagnostics,
                $"Column '{definition.Key}' default expression references missing table '{FormatReference(node.SchemaName, tableReference)}'.",
                nameof(DeepColumnDefaultPass),
                SchemaScopeSeverity.ForReference(_model, SchemaScopeSeverity.SchemaOfObjectReference(tableReference)), location: definition.Location);
        }

        foreach (var columnReference in _qualifiedColumnErrors)
        {
            AddDiagnostic(
                diagnostics,
                $"Column '{definition.Key}' default expression references missing column '{columnReference}'.",
                nameof(DeepColumnDefaultPass),
                SchemaScopeSeverity.ForReference(_model, SchemaScopeSeverity.SchemaOfColumnReference(columnReference)), location: definition.Location);
        }
    }

    private static void AddDiagnostic(ICollection<SemanticDiagnostic> diagnostics, string message, string source, SemanticDiagnosticSeverity severity = SemanticDiagnosticSeverity.Error, SemanticLocation? location = null)
        => diagnostics.Add(severity == SemanticDiagnosticSeverity.Warning
            ? SemanticDiagnostics.Warning(message, location, source)
            : SemanticDiagnostics.Error(message, location, source));

    private bool TryResolveQualifiedColumnReference(string qualifier, string columnName, out string columnReference)
    {
        columnReference = string.Empty;

        if (_currentTable is null)
        {
            return false;
        }

        var validQualifiers = new[]
        {
            _currentTable.Name,
            _currentTable.Key,
            DatabaseModel.CreateKey(_currentTable.SchemaName, _currentTable.Name),
        };

        if (!validQualifiers.Any(candidate => string.Equals(candidate, qualifier, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var column = _currentTable.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, columnName, StringComparison.Ordinal));
        if (column is null)
        {
            return false;
        }

        columnReference = column.Key;
        return true;
    }

    private bool TryResolveTableKey(string? schemaName, string? tableName, out string tableKey)
    {
        tableKey = string.Empty;

        if (string.IsNullOrWhiteSpace(tableName))
        {
            return false;
        }

        var normalizedTableName = tableName.Trim();
        if (_model.TryGetTable(normalizedTableName, out var directMatch) && directMatch is not null)
        {
            tableKey = directMatch.Key;
            return true;
        }

        if (normalizedTableName.Contains('.', StringComparison.Ordinal))
        {
            tableKey = normalizedTableName;
            return _model.Tables.ContainsKey(tableKey);
        }

        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            tableKey = DatabaseModel.CreateKey(schemaName, normalizedTableName);
            if (_model.Tables.ContainsKey(tableKey))
            {
                return true;
            }
        }

        foreach (var table in _model.Tables.Values)
        {
            if (string.Equals(table.Name, normalizedTableName, StringComparison.Ordinal))
            {
                tableKey = table.Key;
                return true;
            }
        }

        tableKey = normalizedTableName;
        return false;
    }

    private string ResolveTableKey(string? schemaName, string? name)
        => ResolveKey<TableDefinition>(name, schemaName, _model.TryGetTable);

    private string ResolveColumnKey(string? schemaName, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var reference = name.Trim();

        // One lookup over the model's columns per run of the pass, not a scan of them per reference (G-314).
        _columns ??= new ColumnKeyLookup(_model);
        if (_columns.Find(reference) is { } found)
        {
            return found.Key;
        }

        if (!string.IsNullOrWhiteSpace(schemaName) && _columns.FindByKey(DatabaseModel.CreateKey(schemaName, reference)) is { } scoped)
        {
            return scoped.Key;
        }

        return reference;
    }

    private string ResolveTypeKey(string? schemaName, string? name)
        => ResolveKey<TypeDefinition>(name, schemaName, _model.TryGetType);

    private string ResolveFunctionKey(string? schemaName, string? name)
        => ResolveKey<FunctionDefinition>(name, schemaName, _model.TryGetFunction);

    private static IReadOnlyList<string> Merge(IEnumerable<string> current, IEnumerable<string> additional)
        => current
            .Concat(additional)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ResolveTableDefinitionKey(TableNode node)
        => DatabaseModel.CreateKey(node.SchemaName, node.Name ?? "unknown_table");

    private static string ResolveColumnDefinitionKey(string? schemaName, string? tableName, string? columnName)
    {
        var resolvedColumnName = columnName ?? "unknown_column";
        var qualifiedColumnName = tableName is null ? resolvedColumnName : $"{tableName}.{resolvedColumnName}";
        return DatabaseModel.CreateKey(schemaName, qualifiedColumnName);
    }

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
            var schemaQualifiedReference = DatabaseModel.CreateKey(schemaName, trimmedReference);
            if (tryGetNode(schemaQualifiedReference, out node) && node is not null)
            {
                return node.Key;
            }
        }

        return trimmedReference;
    }

    private static string FormatReference(string? schemaName, string reference)
        => reference.Contains('.', StringComparison.Ordinal)
            ? reference
            : string.IsNullOrWhiteSpace(schemaName)
                ? reference
                : DatabaseModel.CreateKey(schemaName, reference);

    private delegate bool TryGetNode<TNode>(string key, out TNode? node)
        where TNode : class, ISemanticNode;
}
