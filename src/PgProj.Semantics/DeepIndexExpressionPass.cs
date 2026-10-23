using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Binds what an index refers to from the parser's structured index elements (G-90 part 3): each key element's column name, its
/// expression's pg_query tree (G-72), its operator class and collation, and the predicate's tree. It used to find the
/// <c>CREATE INDEX</c> statement in the file's text, split its key list on commas, and scan each part (and the <c>WHERE</c> clause,
/// when there was no tree) with regular expressions; only a key expression and a predicate with a tree were already read from it.
/// </summary>
internal sealed class DeepIndexExpressionPass
{
    private DatabaseModel _model = DatabaseModel.Empty;
    private TableDefinition? _currentTable;
    private ColumnKeyLookup? _columns;
    private bool _ownerIsMaterializedView;
    private readonly HashSet<string> _qualifiedColumnErrors = new(PgProj.Parser.SqlIdentifiers.NameComparer);
    private readonly List<string> _operatorNames = [];

    public DatabaseModel Execute(DatabaseModel model, IEnumerable<SqlSyntaxTree> syntaxTrees)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(syntaxTrees);

        _model = model;
        _columns = null;

        var indexes = new Dictionary<string, IndexDefinition>(model.Indexes, PgProj.Parser.SqlIdentifiers.NameComparer);
        var diagnostics = new List<SemanticDiagnostic>(model.Diagnostics);

        foreach (var syntaxTree in syntaxTrees)
        {
            foreach (var indexNode in syntaxTree.Indexes)
            {
                var indexKey = IndexKeys.Find(indexes, indexNode);
                if (!indexes.TryGetValue(indexKey, out var indexDefinition))
                {
                    diagnostics.Add(SemanticDiagnostics.Error(
                        $"Deep index expression mapping could not find semantic index '{indexKey}'.",
                        SemanticLocation.Of(syntaxTree.FilePath, indexNode.Range),
                        nameof(DeepIndexExpressionPass)));
                    continue;
                }

                _currentTable = TryResolveTableDefinition(indexNode.SchemaName, indexNode.TableName);
                // The columns of a materialized view are its query's output columns, which are not modeled, so none can be checked (G-171).
                _ownerIsMaterializedView = _currentTable is null && _model.FindMaterializedView(indexNode.SchemaName, indexNode.TableName) is not null;
                _qualifiedColumnErrors.Clear();
                _operatorNames.Clear();

                var visitor = new SqlDependencyVisitor();
                visitor.Visit(indexNode);

                MapKeyElements(indexNode, visitor);
                if (indexNode.PredicateAst is not null)
                {
                    ApplyTreeReferences(ExpressionAstReader.Collect(indexNode.PredicateAst), visitor);
                }

                indexes[indexKey] = AttachDependencies(indexDefinition, visitor.Info) with
                {
                    ReferencedOperators = OperatorReferences.Merge(indexDefinition.ReferencedOperators, OperatorReferences.Resolve(_model, _operatorNames, indexDefinition.SchemaName)),
                };
                var placedFrom = diagnostics.Count;
                AddDiagnostics(indexNode, diagnostics);
                SemanticDiagnostics.PlaceFrom(diagnostics, placedFrom, indexDefinition.Location);
            }
        }

        return model with
        {
            Indexes = indexes,
            Diagnostics = diagnostics,
        };
    }

    private void MapKeyElements(IndexNode node, SqlDependencyVisitor visitor)
    {
        foreach (var element in node.KeyElements)
        {
            if (element.ExpressionAst is not null)
            {
                ApplyTreeReferences(ExpressionAstReader.Collect(element.ExpressionAst), visitor);
            }
            else if (!string.IsNullOrWhiteSpace(element.Name)
                && _currentTable?.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, element.Name, StringComparison.Ordinal)) is { } column)
            {
                visitor.Info.AddColumn(column.Key);
            }

            // An operator class is not a type (G-74): the built-in ones (text_pattern_ops, int4_ops) and an extension's (gin_trgm_ops)
            // are not in the model, and reporting them missing refused every such index. Only a name the project defines is recorded,
            // and the same for a collation.
            foreach (var name in new[] { element.OperatorClassName, element.CollationName })
            {
                if (!string.IsNullOrWhiteSpace(name) && IsProjectType(name))
                {
                    visitor.Info.AddType(name);
                }
            }
        }
    }

    private bool IsProjectType(string name)
    {
        var typeName = name.Split('.')[^1];
        return _model.Types.Values.Any(candidate => string.Equals(candidate.Name, typeName, StringComparison.Ordinal));
    }

    /// <summary>Records what an expression's tree refers to, resolving columns against the index's table.</summary>
    private void ApplyTreeReferences(ExpressionAstReader.References references, SqlDependencyVisitor visitor)
    {
        foreach (var parts in references.Columns)
        {
            if (parts.Count == 1)
            {
                var column = _currentTable?.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, parts[0], StringComparison.OrdinalIgnoreCase));
                if (column is not null)
                {
                    visitor.Info.AddColumn(column.Key);
                }

                continue;
            }

            var qualifier = parts[^2];
            var columnName = parts[^1];
            if (TryResolveQualifiedColumnReference(qualifier, columnName, out var columnReference))
            {
                visitor.Info.AddColumn(columnReference);
            }
            else
            {
                var unresolvedReference = $"{qualifier}.{columnName}";
                visitor.Info.AddColumn(unresolvedReference);
                if (!_ownerIsMaterializedView)
                {
                    _qualifiedColumnErrors.Add(unresolvedReference);
                }
            }
        }

        _operatorNames.AddRange(references.Operators);

        foreach (var function in references.Functions)
        {
            visitor.Info.AddFunction(function);
        }

        // Only a type the project defines is a dependency. PostgreSQL prints an implicit cast (<c>'x%'::text</c>) that the author never wrote,
        // so recording built-in types would make the same index differ between the project and the server.
        foreach (var type in references.Types)
        {
            if (IsProjectType(type))
            {
                visitor.Info.AddType(type);
            }
        }
    }

    private IndexDefinition AttachDependencies(IndexDefinition definition, DependencyInfo info)
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

    private void AddDiagnostics(IndexNode node, ICollection<SemanticDiagnostic> diagnostics)
    {
        if (!PostgreSqlSystemReferences.IsBuiltInRelation(FormatReference(node.SchemaName, node.TableName ?? "<null>"))
            && !TryResolveTableKey(node.SchemaName, node.TableName, out _)
            && _model.FindMaterializedView(node.SchemaName, node.TableName) is null)
        {
            AddDiagnostic(
                diagnostics,
                $"Index '{ResolveIndexDefinitionKey(node)}' references missing table '{FormatReference(node.SchemaName, node.TableName ?? "<null>")}'.",
                nameof(DeepIndexExpressionPass),
                SchemaScopeSeverity.ForReference(_model, node.SchemaName));
        }

        foreach (var columnReference in _qualifiedColumnErrors)
        {
            AddDiagnostic(
                diagnostics,
                $"Index '{ResolveIndexDefinitionKey(node)}' references missing column '{columnReference}'.",
                nameof(DeepIndexExpressionPass),
                SchemaScopeSeverity.ForReference(_model, SchemaScopeSeverity.SchemaOfColumnReference(columnReference)));
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

    private TableDefinition? TryResolveTableDefinition(string? schemaName, string? tableName)
        => TryResolveTableKey(schemaName, tableName, out var tableKey) && _model.Tables.TryGetValue(tableKey, out var table)
            ? table
            : null;

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

        // An index's expression can name only its own table's columns (G-229): a bare name, or one qualified by the table's name, is that table's column.
        // Searching every table first bound it to another table's column of the same name, and build refused the index.
        if (_currentTable is not null)
        {
            var columnName = reference.Contains('.', StringComparison.Ordinal) && reference.StartsWith(_currentTable.Name + ".", StringComparison.Ordinal)
                ? reference[(_currentTable.Name.Length + 1)..]
                : reference;
            if (!columnName.Contains('.', StringComparison.Ordinal)
                && _currentTable.Columns.FirstOrDefault(column => PgProj.Parser.SqlIdentifiers.NameComparer.Equals(column.Name, columnName)) is { } own)
            {
                return own.Key;
            }
        }

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

    private static string ResolveIndexDefinitionKey(IndexNode node)
    {
        var indexName = node.Name ?? (node.TableName is null ? "unknown_index" : $"{node.TableName}_idx");
        return DatabaseModel.CreateKey(node.SchemaName, indexName);
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
