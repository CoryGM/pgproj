namespace PgProj.Semantics;

internal sealed class ViewSemanticPass
{
    private DatabaseModel _model = DatabaseModel.Empty;
    private SymbolTable _symbolTable = new(DatabaseModel.Empty);
    private List<SemanticDiagnostic> _diagnostics = [];

    public DatabaseModel Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model;
        _symbolTable = new SymbolTable(model);
        _diagnostics = [];

        var views = model.Views.Values
            .Select(view => BindView(view, model))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);

        return model with
        {
            Views = views,
            Diagnostics = model.Diagnostics.Concat(_diagnostics).ToList(),
        };
    }

    private ViewDefinition BindView(ViewDefinition view, DatabaseModel model)
    {
        if (view.SelectExpressionNode is null)
        {
            return view;
        }

        if (view.SelectExpressionNode.Kind == "SimpleExpression"
            && view.SelectExpressionNode.Children.Count == 0
            && !string.IsNullOrWhiteSpace(view.RawBodyExpression))
        {
            return view;
        }

        var boundSelect = BindSelectExpression(view.SelectExpressionNode, view, model);
        var targets = GetSelectTargets(boundSelect).ToList();
        var outputNames = new List<string>(targets.Count);
        var outputTypes = new List<string>(targets.Count);

        for (var index = 0; index < targets.Count; index++)
        {
            var target = BindSelectTarget(targets[index], view, model);
            targets[index] = target;

            var outputName = InferOutputColumnName(target, index + 1);
            outputNames.Add(outputName);

            var outputType = InferOutputColumnType(target);
            if (string.IsNullOrWhiteSpace(outputType))
            {
                AddDiagnostic(_diagnostics, $"View '{view.Key}' could not infer an output type for SELECT target '{outputName}'.", nameof(ViewSemanticPass), location: view.Location);
            }
            else
            {
                outputTypes.Add(outputType);
            }
        }

        foreach (var duplicateName in outputNames
                     .GroupBy(static name => name, PgProj.Parser.SqlIdentifiers.NameComparer)
                     .Where(static group => group.Count() > 1)
                     .Select(static group => group.Key))
        {
            AddDiagnostic(_diagnostics, $"View '{view.Key}' produces duplicate output column '{duplicateName}'.", nameof(ViewSemanticPass), location: view.Location);
        }

        return view with
        {
            SelectExpressionNode = RebuildSelectExpression(boundSelect, targets),
            OutputColumnNames = outputNames.ToArray(),
            OutputColumnTypes = outputTypes.ToArray(),
        };
    }

    private ExpressionNode BindSelectExpression(ExpressionNode node, ViewDefinition view, DatabaseModel model)
        => BindExpression(node, view, model);

    private ExpressionNode BindSelectTarget(ExpressionNode node, ViewDefinition view, DatabaseModel model)
        => BindExpression(node, view, model);

    private ExpressionNode BindExpression(ExpressionNode node, ViewDefinition view, DatabaseModel model)
    {
        if (string.IsNullOrWhiteSpace(node.Kind))
        {
            AddDiagnostic(_diagnostics, $"View '{view.Key}' contains a malformed expression node with no kind.", nameof(ViewSemanticPass), location: view.Location);
            return node;
        }

        return node.Kind switch
        {
            "Identifier" => BindIdentifier(node, view, model),
            "FunctionCall" => BindFunctionCall(node, view, model),
            "Operator" => BindOperator(node, view, model),
            "Literal" => node,
            _ => node with
            {
                Children = node.Children.Select(child => BindExpression(child, view, model)).ToArray(),
            },
        };
    }

    private ExpressionNode BindIdentifier(ExpressionNode node, ViewDefinition view, DatabaseModel model)
    {
        var text = node.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            AddDiagnostic(_diagnostics, $"View '{view.Key}' contains an empty identifier target.", nameof(ViewSemanticPass), location: view.Location);
            return node;
        }

        var parts = text.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            var column = ResolveViewColumn(view, parts[0]);
            if (column is not null)
            {
                return node with { Text = column.Key, Children = [] };
            }

            AddDiagnostic(
                _diagnostics,
                $"View '{view.Key}' references missing column '{parts[0]}'.",
                nameof(ViewSemanticPass),
                SchemaScopeSeverity.ForReference(model, null), location: view.Location);
            return node;
        }

        if (parts.Length == 2 || parts.Length == 3)
        {
            var qualifier = parts.Length == 2 ? parts[0] : $"{parts[0]}.{parts[1]}";
            var columnName = parts[^1];
            var table = ResolveViewTable(view, qualifier, model);
            if (table is null)
            {
                if (PostgreSqlSystemReferences.IsBuiltInRelation(qualifier))
                {
                    return node;
                }

                var type = ResolveViewType(qualifier, columnName);
                if (type is not null)
                {
                    return node with { Text = type.Key, Children = [] };
                }

                AddDiagnostic(
                    _diagnostics,
                    $"View '{view.Key}' references missing table '{qualifier}'.",
                    nameof(ViewSemanticPass),
                    SchemaScopeSeverity.ForReference(model, SchemaScopeSeverity.SchemaOfObjectReference(qualifier)), location: view.Location);
                return node;
            }

            var column = table.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, columnName, StringComparison.Ordinal));
            if (column is null)
            {
                AddDiagnostic(
                    _diagnostics,
                    $"View '{view.Key}' references missing column '{columnName}' on table '{table.Key}'.",
                    nameof(ViewSemanticPass),
                    SchemaScopeSeverity.ForReference(model, table.SchemaName), location: view.Location);
                return node;
            }

            return node with
            {
                Text = column.Key,
                Children = [ExpressionNode.Identifier(table.Key)],
            };
        }

        AddDiagnostic(_diagnostics, $"View '{view.Key}' contains malformed identifier '{text}'.", nameof(ViewSemanticPass), location: view.Location);
        return node;
    }

    private ExpressionNode BindFunctionCall(ExpressionNode node, ViewDefinition view, DatabaseModel model)
    {
        var functionName = node.Text.Trim();
        var boundChildren = node.Children.Select(child => BindExpression(child, view, model)).ToArray();
        if (_symbolTable.TryGetFunction(view, functionName, out var function) && function is not null)
        {
            return node with
            {
                Text = function.Key,
                Children = boundChildren,
            };
        }

        var fallbackFunction = FunctionReferences.Resolve(model, view.SchemaName, functionName);
        if (fallbackFunction is not null)
        {
            return node with
            {
                Text = fallbackFunction.Key,
                Children = boundChildren,
            };
        }

        if (!BuiltInFunctions.IsBuiltIn(functionName) && AggregateReferences.Find(model, view.SchemaName, functionName).Count == 0)
        {
            AddDiagnostic(
                _diagnostics,
                $"View '{view.Key}' references missing function '{functionName}'.",
                nameof(ViewSemanticPass),
                SchemaScopeSeverity.Lenient(
                    BuiltInFunctions.MissingFunctionSeverity(model),
                    SchemaScopeSeverity.ForReference(model, SchemaScopeSeverity.SchemaOfObjectReference(functionName))), location: view.Location);
        }

        return node with { Children = boundChildren };
    }

    private ExpressionNode BindOperator(ExpressionNode node, ViewDefinition view, DatabaseModel model)
        => node with
        {
            Children = node.Children.Select(child => BindExpression(child, view, model)).ToArray(),
        };

    private string InferOutputColumnName(ExpressionNode node, int ordinal)
    {
        if (node.Kind == "Identifier")
        {
            return GetTrailingIdentifier(node.Text, $"column_{ordinal}");
        }

        if (node.Kind == "FunctionCall")
        {
            return GetTrailingIdentifier(node.Text, $"column_{ordinal}");
        }

        return $"column_{ordinal}";
    }

    private string? InferOutputColumnType(ExpressionNode node)
    {
        if (node.Kind == "Identifier")
        {
            var column = ResolveColumnByKey(node.Text);
            return column?.ResolvedTypes.FirstOrDefault()?.Key
                ?? column?.ResolvedTypes.FirstOrDefault()?.Name
                ?? column?.RawType
                ?? column?.DataType;
        }

        if (node.Kind == "FunctionCall")
        {
            var function = _model.Functions.TryGetValue(node.Text, out var resolvedFunction) ? resolvedFunction : null;
            return function?.ResolvedTypes.FirstOrDefault()?.Key
                ?? function?.ResolvedTypes.FirstOrDefault()?.Name
                ?? function?.ReferencedTypes.FirstOrDefault();
        }

        if (node.Kind == "Literal")
        {
            if (string.Equals(node.Text, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(node.Text, "false", StringComparison.OrdinalIgnoreCase))
            {
                return "boolean";
            }

            if (decimal.TryParse(node.Text, out _))
            {
                return node.Text.Contains('.', StringComparison.Ordinal) ? "numeric" : "integer";
            }

            if (node.Text.Length >= 2 && node.Text[0] == '\'' && node.Text[^1] == '\'')
            {
                return "text";
            }
        }

        if (node.Kind == "Operator")
        {
            return node.Text switch
            {
                "=" or "<>" or "!=" or ">" or "<" or ">=" or "<=" or "and" or "or" => "boolean",
                _ => node.Children.Select(InferOutputColumnType).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
            };
        }

        return node.Children.Select(InferOutputColumnType).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static IReadOnlyList<ExpressionNode> GetSelectTargets(ExpressionNode node)
        => node.Kind == "SimpleExpression" && node.Children.Count > 0
            ? node.Children.ToArray()
            : [node];

    private static ExpressionNode RebuildSelectExpression(ExpressionNode root, IReadOnlyList<ExpressionNode> targets)
        => root.Kind == "SimpleExpression" && root.Children.Count > 0
            ? root with { Children = targets.ToArray() }
            : root;

    private TableDefinition? ResolveViewTable(ViewDefinition view, string reference, DatabaseModel model)
    {
        if (_symbolTable.TryGetTable(view, reference, out var table) && table is not null)
        {
            return table;
        }

        return model.Tables.Values.FirstOrDefault(candidate => string.Equals(candidate.Name, reference, StringComparison.Ordinal));
    }

    private ColumnDefinition? ResolveViewColumn(ViewDefinition view, string columnName)
    {
        foreach (var table in view.ResolvedTables)
        {
            var column = table.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, columnName, StringComparison.Ordinal));
            if (column is not null)
            {
                return column;
            }
        }

        return _model.Tables.Values
            .SelectMany(static table => table.Columns)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, columnName, StringComparison.Ordinal));
    }

    private TypeDefinition? ResolveViewType(string schemaName, string typeName)
    {
        var key = DatabaseModel.CreateKey(schemaName, typeName);
        if (_model.TryGetType(key, out var type) && type is not null)
        {
            return type;
        }

        return _model.Types.Values.FirstOrDefault(candidate => string.Equals(candidate.Name, typeName, StringComparison.Ordinal));
    }

    private ColumnDefinition? ResolveColumnByKey(string key)
    {
        if (_symbolTable.TryGetColumn(key, out var column) && column is not null)
        {
            return column;
        }

        return _model.Tables.Values
            .SelectMany(static table => table.Columns)
            .FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
    }

    private static string GetTrailingIdentifier(string text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var parts = text.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? fallback : parts[^1];
    }

    private static void AddDiagnostic(List<SemanticDiagnostic> diagnostics, string message, string source, SemanticDiagnosticSeverity severity = SemanticDiagnosticSeverity.Error, SemanticLocation? location = null)
        => diagnostics.Add(severity == SemanticDiagnosticSeverity.Warning
            ? SemanticDiagnostics.Warning(message, location, source)
            : SemanticDiagnostics.Error(message, location, source));
}
