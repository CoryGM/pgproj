namespace PgProj.Semantics;

internal sealed class ExpressionSemanticBindingPass
{
    private DatabaseModel _model = DatabaseModel.Empty;
    private List<SemanticDiagnostic> _diagnostics = [];
    private SymbolTable _symbolTable = new(DatabaseModel.Empty);

    public DatabaseModel Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model;
        _diagnostics = [];
        _symbolTable = new SymbolTable(model);

        var tables = model.Tables.Values
            .Select(BindTableExpressions)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var views = model.Views.Values
            .Select(BindViewExpressions)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var functions = model.Functions.Values
            .Select(BindFunctionExpressions)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var indexes = model.Indexes.Values
            .Select(BindIndexExpressions)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var constraints = model.Constraints.Values
            .Select(BindConstraintExpressions)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);

        return model with
        {
            Tables = tables,
            Views = views,
            Functions = functions,
            Indexes = indexes,
            Constraints = constraints,
            Diagnostics = model.Diagnostics.Concat(_diagnostics).ToList(),
        };
    }

    private TableDefinition BindTableExpressions(TableDefinition table)
        => table with
        {
            Columns = table.Columns.Select(column => BindColumnExpression(column, table)).ToArray(),
        };

    private ColumnDefinition BindColumnExpression(ColumnDefinition column, TableDefinition owningTable)
        => column.DefaultExpressionNode is null
            ? column
            : column with
            {
                DefaultExpressionNode = BindExpression(column.DefaultExpressionNode, column, _model),
            };

    private ViewDefinition BindViewExpressions(ViewDefinition view)
        => view.SelectExpressionNode is null
            ? view
            : view with
            {
                SelectExpressionNode = BindExpression(view.SelectExpressionNode, view, _model),
            };

    private FunctionDefinition BindFunctionExpressions(FunctionDefinition function)
        => function.BodyExpressionNode is null
            ? function
            : function with
            {
                BodyExpressionNode = BindExpression(function.BodyExpressionNode, function, _model),
            };

    private IndexDefinition BindIndexExpressions(IndexDefinition index)
        => index.PredicateExpressionNode is null
            ? index
            : index with
            {
                PredicateExpressionNode = BindExpression(index.PredicateExpressionNode, index, _model),
            };

    private ConstraintDefinition BindConstraintExpressions(ConstraintDefinition constraint)
        => constraint.CheckExpressionNode is null
            ? constraint
            : constraint with
            {
                CheckExpressionNode = BindExpression(constraint.CheckExpressionNode, constraint, _model),
            };

    private ExpressionNode BindExpression(ExpressionNode node, ISemanticNode context, DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(node.Kind))
        {
            AddDiagnostic(_diagnostics, $"{context.Kind} '{context.Key}' contains a malformed expression node with no kind.", nameof(ExpressionSemanticBindingPass), location: context.Location);
            return node;
        }

        return node.Kind switch
        {
            "Identifier" => BindIdentifier(node, context, model),
            "FunctionCall" => BindFunctionCall(node, context, model),
            "Operator" => BindOperator(node, context, model),
            "Literal" => BindLiteral(node),
            "SimpleExpression" => node with
            {
                Children = node.Children.Select(child => BindExpression(child, context, model)).ToArray(),
            },
            _ => BindUnknown(node, context, model),
        };
    }

    private ExpressionNode BindIdentifier(ExpressionNode node, ISemanticNode context, DatabaseModel model)
    {
        // The only expression a column owns is its DEFAULT, and PostgreSQL rejects a column reference there, so a bare
        // word in it is a keyword (CURRENT_TIMESTAMP, LOCALTIME, ARRAY, ...) and never a missing column (G-56).
        // Function calls in a default are still resolved.
        if (context is ColumnDefinition)
        {
            return node;
        }

        var text = node.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            AddDiagnostic(_diagnostics, $"{context.Kind} '{context.Key}' contains an empty identifier expression.", nameof(ExpressionSemanticBindingPass), location: context.Location);
            return node;
        }

        var parts = text.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            var column = ResolveContextColumn(context, parts[0]) ?? BindColumn(parts[0]);
            if (column is not null)
            {
                return node with { Text = column.Key, Children = [] };
            }

            var table = BindTable(parts[0]);
            if (table is not null)
            {
                return node with { Text = table.Key, Children = [] };
            }

            if (context is IndexDefinition { } indexContext && model.FindMaterializedView(indexContext.SchemaName, indexContext.TableName) is not null)
            {
                // The columns of a materialized view are its query's output columns, which are not modeled (G-171).
                return node;
            }

            AddDiagnostic(
                _diagnostics,
                $"{context.Kind} '{context.Key}' references missing column '{parts[0]}'.",
                nameof(ExpressionSemanticBindingPass),
                SchemaScopeSeverity.ForReference(model, null), location: context.Location);
            return node;
        }

        if (parts.Length == 2)
        {
            var table = ResolveTable(context, model, parts[0]);
            if (table is not null)
            {
                var column = table.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, parts[1], StringComparison.OrdinalIgnoreCase));
                if (column is not null)
                {
                    return node with
                    {
                        Text = column.Key,
                        Children = [ExpressionNode.Identifier(table.Key)],
                    };
                }

                return node with { Text = table.Key, Children = [] };
            }

            var type = ResolveType(context, parts[0], parts[1]);
            if (type is not null)
            {
                return node with { Text = type.Key, Children = [] };
            }

            AddDiagnostic(_diagnostics, $"{context.Kind} '{context.Key}' references unresolved identifier '{text}'.", nameof(ExpressionSemanticBindingPass), location: context.Location);
            return node;
        }

        if (parts.Length == 3)
        {
            var table = ResolveTable(context, model, $"{parts[0]}.{parts[1]}");
            if (table is not null)
            {
                var column = table.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, parts[2], StringComparison.OrdinalIgnoreCase));
                if (column is not null)
                {
                    return node with
                    {
                        Text = column.Key,
                        Children = [ExpressionNode.Identifier(table.Key)],
                    };
                }
            }

            AddDiagnostic(_diagnostics, $"{context.Kind} '{context.Key}' references unresolved identifier '{text}'.", nameof(ExpressionSemanticBindingPass), location: context.Location);
            return node;
        }

        AddDiagnostic(_diagnostics, $"{context.Kind} '{context.Key}' contains malformed identifier '{text}'.", nameof(ExpressionSemanticBindingPass), location: context.Location);
        return node;
    }

    private ExpressionNode BindFunctionCall(ExpressionNode node, ISemanticNode context, DatabaseModel model)
    {
        var functionName = node.Text.Trim();
        var boundChildren = node.Children.Select(child => BindExpression(child, context, model)).ToArray();
        var function = ResolveFunction(context, model, functionName);
        if (function is null)
        {
            if (BuiltInFunctions.IsBuiltIn(functionName) || AggregateReferences.Find(model, context.SchemaName, functionName).Count > 0)
            {
                return node with { Children = boundChildren };
            }

            AddDiagnostic(
                _diagnostics,
                $"{context.Kind} '{context.Key}' references missing function '{functionName}'.",
                nameof(ExpressionSemanticBindingPass),
                SchemaScopeSeverity.Lenient(
                    BuiltInFunctions.MissingFunctionSeverity(model),
                    SchemaScopeSeverity.ForReference(model, SchemaScopeSeverity.SchemaOfObjectReference(functionName))), location: context.Location);
            return node with { Children = boundChildren };
        }

        return node with
        {
            Text = function.Key,
            Children = boundChildren,
        };
    }

    private ExpressionNode BindOperator(ExpressionNode node, ISemanticNode context, DatabaseModel model)
        => node with
        {
            Children = node.Children.Select(child => BindExpression(child, context, model)).ToArray(),
        };

    private static ExpressionNode BindLiteral(ExpressionNode node)
        => node;

    private ExpressionNode BindUnknown(ExpressionNode node, ISemanticNode context, DatabaseModel model)
    {
        AddDiagnostic(_diagnostics, $"{context.Kind} '{context.Key}' contains unknown expression kind '{node.Kind}'.", nameof(ExpressionSemanticBindingPass), location: context.Location);
        return node with
        {
            Children = node.Children.Select(child => BindExpression(child, context, model)).ToArray(),
        };
    }

    private TableDefinition? BindTable(string key)
        => _model.TryGetTable(key, out var table) && table is not null
            ? table
            : _model.Tables.Values.FirstOrDefault(candidate => string.Equals(candidate.Name, key, StringComparison.Ordinal));

    private ColumnDefinition? BindColumn(string key)
        => _symbolTable.TryGetColumn(key, out var column) && column is not null
            ? column
            : _model.Tables.Values.SelectMany(static table => table.Columns)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, key, StringComparison.Ordinal));

    private TypeDefinition? BindType(string key)
        => _model.TryGetType(key, out var type) && type is not null
            ? type
            : _model.Types.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, key, StringComparison.Ordinal)
                || string.Equals(candidate.Key, key, StringComparison.Ordinal));

    private FunctionDefinition? BindFunction(string key)
        => FunctionReferences.Resolve(_model, null, key);

    private TableDefinition? ResolveTable(ISemanticNode context, DatabaseModel model, string key)
    {
        if (_symbolTable.TryGetTable(context, key, out var table) && table is not null)
        {
            return table;
        }

        return model.Tables.Values.FirstOrDefault(candidate => string.Equals(candidate.Name, key, StringComparison.Ordinal));
    }

    private FunctionDefinition? ResolveFunction(ISemanticNode context, DatabaseModel model, string key)
    {
        if (_symbolTable.TryGetFunction(context, key, out var function) && function is not null)
        {
            return function;
        }

        return FunctionReferences.Resolve(model, context.SchemaName, key);
    }

    private TypeDefinition? ResolveType(ISemanticNode context, string left, string right)
    {
        var qualifiedKey = DatabaseModel.CreateKey(left, right);
        if (_model.TryGetType(qualifiedKey, out var qualifiedType) && qualifiedType is not null)
        {
            return qualifiedType;
        }

        if (_symbolTable.TryGetType(context, qualifiedKey, out var scopedType) && scopedType is not null)
        {
            return scopedType;
        }

        return BindType(right);
    }

    private ColumnDefinition? ResolveContextColumn(ISemanticNode context, string columnName)
    {
        var owningTable = ResolveOwningTable(context);
        return owningTable?.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, columnName, StringComparison.Ordinal));
    }

    private TableDefinition? ResolveOwningTable(ISemanticNode context)
        => context switch
        {
            TableDefinition table => table,
            ColumnDefinition column when !string.IsNullOrWhiteSpace(column.TableName)
                => ResolveTable(context, _model, column.TableName),
            IndexDefinition index when !string.IsNullOrWhiteSpace(index.TableName)
                => ResolveTable(context, _model, index.TableName),
            ConstraintDefinition constraint when !string.IsNullOrWhiteSpace(constraint.TableName)
                => ResolveTable(context, _model, constraint.TableName),
            _ => null,
        };

    private static void AddDiagnostic(List<SemanticDiagnostic> diagnostics, string message, string source, SemanticDiagnosticSeverity severity = SemanticDiagnosticSeverity.Error, SemanticLocation? location = null)
        => diagnostics.Add(severity == SemanticDiagnosticSeverity.Warning
            ? SemanticDiagnostics.Warning(message, location, source)
            : SemanticDiagnostics.Error(message, location, source));
}