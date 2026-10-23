using PgProj.Parser;

namespace PgProj.Semantics;

internal sealed class DeepDependencyPass
{
    private DatabaseModel _model = DatabaseModel.Empty;

    public DatabaseModel Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model;

        var diagnostics = model.Diagnostics.ToList();
        var tables = model.Tables.Values
            .Select(table => MapTable(table, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var views = model.Views.Values
            .Select(view => MapView(view, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var functions = model.Functions.Values
            .Select(function => MapFunction(function, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var indexes = model.Indexes.Values
            .Select(index => MapIndex(index, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var constraints = model.Constraints.Values
            .Select(constraint => MapConstraint(constraint, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);

        return model with
        {
            Tables = tables,
            Views = views,
            Functions = functions,
            Indexes = indexes,
            Constraints = constraints,
            Diagnostics = diagnostics,
        };
    }

    private TableDefinition MapTable(TableDefinition table, List<SemanticDiagnostic> diagnostics)
    {
        var aggregate = new DependencyInfo();
        var columns = table.Columns
            .Select(column => MapColumn(column, aggregate, diagnostics))
            .ToArray();

        return ((TableDefinition)MergeDependencies(table, aggregate)) with
        {
            Columns = columns,
        };
    }

    private ColumnDefinition MapColumn(ColumnDefinition column, DependencyInfo aggregate, List<SemanticDiagnostic> diagnostics)
    {
        if (column.DefaultExpressionNode is null)
        {
            return column;
        }

        var extracted = new DependencyInfo();
        var placedFrom = diagnostics.Count;
        ExtractFromExpression(column.DefaultExpressionNode, extracted, diagnostics, column.Kind, column.Key);
        SemanticDiagnostics.PlaceFrom(diagnostics, placedFrom, column.Location);

        // PostgreSQL rejects a column or table reference in a DEFAULT, so a bare word found in one is a keyword
        // (CURRENT_TIMESTAMP, LOCALTIME, the ARRAY of ARRAY[1, 2]) and not a reference. Only the functions and types
        // it uses are dependencies (G-56).
        var info = new DependencyInfo();
        info.AddTypes(extracted.ReferencedTypes);
        info.AddFunctions(extracted.ReferencedFunctions);
        aggregate.AddTypes(info.ReferencedTypes);
        aggregate.AddFunctions(info.ReferencedFunctions);
        return (ColumnDefinition)MergeDependencies(column, info);
    }

    private ViewDefinition MapView(ViewDefinition view, List<SemanticDiagnostic> diagnostics)
    {
        _ = diagnostics;

        // View bodies are complete SQL queries, not scalar expressions. They are bound from their tree by QueryBodyBindingPass (G-90).
        return view;
    }

    private FunctionDefinition MapFunction(FunctionDefinition function, List<SemanticDiagnostic> diagnostics)
    {
        _ = diagnostics;

        // A SQL-language body is bound from its tree by QueryBodyBindingPass (G-90); PL/pgSQL bodies are not read. They are not scalar expressions.
        return function;
    }

    private IndexDefinition MapIndex(IndexDefinition index, List<SemanticDiagnostic> diagnostics)
    {
        if (index.PredicateExpressionNode is null)
        {
            return index;
        }

        var info = new DependencyInfo();
        var placedFrom = diagnostics.Count;
        ExtractFromExpression(index.PredicateExpressionNode, info, diagnostics, index.Kind, index.Key);
        SemanticDiagnostics.PlaceFrom(diagnostics, placedFrom, index.Location);

        // TODO: Extend deep extraction to partial-index predicates and index-key expression trees.
        return (IndexDefinition)MergeDependencies(index, info);
    }

    private ConstraintDefinition MapConstraint(ConstraintDefinition constraint, List<SemanticDiagnostic> diagnostics)
    {
        if (constraint.CheckExpressionNode is null)
        {
            return constraint;
        }

        var info = new DependencyInfo();
        var placedFrom = diagnostics.Count;
        ExtractFromExpression(constraint.CheckExpressionNode, info, diagnostics, constraint.Kind, constraint.Key);
        SemanticDiagnostics.PlaceFrom(diagnostics, placedFrom, constraint.Location);

        // TODO: Extend deep extraction to exclusion elements and richer CHECK predicate semantics.
        var merged = (ConstraintDefinition)MergeDependencies(constraint, info);
        return merged with
        {
            ReferencedOperators = OperatorReferences.Merge(merged.ReferencedOperators, OperatorReferences.Resolve(_model, ExpressionAstReader.Collect(constraint.DeclarationNode?.CheckExpressionAst).Operators, constraint.SchemaName)),
        };
    }

    private void ExtractFromExpression(
        ExpressionNode node,
        DependencyInfo info,
        List<SemanticDiagnostic> diagnostics,
        string ownerKind,
        string ownerKey)
    {
        if (string.IsNullOrWhiteSpace(node.Kind))
        {
            AddDiagnostic(diagnostics, $"{ownerKind} '{ownerKey}' contains a malformed expression node with no kind.", nameof(DeepDependencyPass));
            return;
        }

        switch (node.Kind)
        {
            case "Identifier":
                ExtractIdentifier(node, info, diagnostics, ownerKind, ownerKey);
                break;
            case "Literal":
                break;
            case "FunctionCall":
                if (string.IsNullOrWhiteSpace(node.Text))
                {
                    AddDiagnostic(diagnostics, $"{ownerKind} '{ownerKey}' contains a function-call node with no name.", nameof(DeepDependencyPass));
                }
                else
                {
                    info.AddFunction(node.Text);
                }

                foreach (var child in node.Children)
                {
                    ExtractFromExpression(child, info, diagnostics, ownerKind, ownerKey);
                }
                break;
            case "Operator":
            case "SimpleExpression":
                if (node.Kind == "Operator" && node.Children.Count < 2)
                {
                    AddDiagnostic(diagnostics, $"{ownerKind} '{ownerKey}' contains an operator node with insufficient operands.", nameof(DeepDependencyPass));
                }

                // A childless expression is an unparsed leaf: every expression is read from pg_query's tree, and one whose tree names no column
                // or function (ARRAY[]::int[]) has nothing to read; its text is not guessed at (G-90 part 4).

                foreach (var child in node.Children)
                {
                    ExtractFromExpression(child, info, diagnostics, ownerKind, ownerKey);
                }
                break;
            default:
                AddDiagnostic(diagnostics, $"{ownerKind} '{ownerKey}' contains an unknown expression node kind '{node.Kind}'.", nameof(DeepDependencyPass));
                foreach (var child in node.Children)
                {
                    ExtractFromExpression(child, info, diagnostics, ownerKind, ownerKey);
                }
                break;
        }
    }

    private void ExtractIdentifier(
        ExpressionNode node,
        DependencyInfo info,
        List<SemanticDiagnostic> diagnostics,
        string ownerKind,
        string ownerKey)
    {
        if (string.IsNullOrWhiteSpace(node.Text))
        {
            AddDiagnostic(diagnostics, $"{ownerKind} '{ownerKey}' contains an identifier node with no text.", nameof(DeepDependencyPass));
            return;
        }

        // Read as PostgreSQL reads it (G-70): "Amount" is the column Amount, not a column whose name contains quotes.
        var parts = SqlIdentifiers.SplitQualifiedName(node.Text.Trim()).ToArray();
        var identifier = string.Join('.', parts);
        if (parts.Length == 0)
        {
            AddDiagnostic(diagnostics, $"{ownerKind} '{ownerKey}' contains an unparsable identifier fragment '{identifier}'.", nameof(DeepDependencyPass));
            return;
        }

        if (parts.Length == 1)
        {
            info.AddColumn(parts[0]);
            return;
        }

        if (parts.Length == 2)
        {
            if (LooksLikeTypeReference(identifier))
            {
                info.AddType(identifier);
                return;
            }

            if (LooksLikeTableReference(identifier))
            {
                info.AddTable(identifier);
                return;
            }

            info.AddTable(parts[0]);
            info.AddColumn(identifier);
            return;
        }

        if (parts.Length == 3)
        {
            var tableReference = $"{parts[0]}.{parts[1]}";
            info.AddTable(tableReference);
            info.AddColumn(identifier);
            return;
        }

        AddDiagnostic(diagnostics, $"{ownerKind} '{ownerKey}' contains an identifier with unknown shape '{identifier}'.", nameof(DeepDependencyPass));
    }

    private ISemanticNode MergeDependencies(ISemanticNode node, DependencyInfo info)
    {
        var referencedTables = Merge(node.ReferencedTables, info.ReferencedTables);
        var referencedColumns = Merge(node.ReferencedColumns, info.ReferencedColumns);
        var referencedTypes = Merge(node.ReferencedTypes, info.ReferencedTypes);
        var referencedFunctions = Merge(node.ReferencedFunctions, info.ReferencedFunctions);

        return node switch
        {
            TableDefinition table => table with
            {
                ReferencedTables = referencedTables,
                ReferencedColumns = referencedColumns,
                ReferencedTypes = referencedTypes,
                ReferencedFunctions = referencedFunctions,
            },
            ColumnDefinition column => column with
            {
                ReferencedTables = referencedTables,
                ReferencedColumns = referencedColumns,
                ReferencedTypes = referencedTypes,
                ReferencedFunctions = referencedFunctions,
            },
            ViewDefinition view => view with
            {
                ReferencedTables = referencedTables,
                ReferencedColumns = referencedColumns,
                ReferencedTypes = referencedTypes,
                ReferencedFunctions = referencedFunctions,
            },
            FunctionDefinition function => function with
            {
                ReferencedTables = referencedTables,
                ReferencedColumns = referencedColumns,
                ReferencedTypes = referencedTypes,
                ReferencedFunctions = referencedFunctions,
            },
            IndexDefinition index => index with
            {
                ReferencedTables = referencedTables,
                ReferencedColumns = referencedColumns,
                ReferencedTypes = referencedTypes,
                ReferencedFunctions = referencedFunctions,
            },
            ConstraintDefinition constraint => constraint with
            {
                ReferencedTables = referencedTables,
                ReferencedColumns = referencedColumns,
                ReferencedTypes = referencedTypes,
                ReferencedFunctions = referencedFunctions,
            },
            _ => node,
        };
    }

    private bool LooksLikeTableReference(string identifier)
        => _model.Tables.ContainsKey(identifier)
            || _model.Views.ContainsKey(identifier)
            || _model.Tables.Values.Any(table => string.Equals(table.Name, identifier, StringComparison.OrdinalIgnoreCase));

    private bool LooksLikeTypeReference(string identifier)
        => _model.Types.ContainsKey(identifier)
            || _model.Types.Values.Any(type => string.Equals(type.Name, identifier, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> Merge(IEnumerable<string> current, IEnumerable<string> additional)
        => current
            .Concat(additional)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void AddDiagnostic(List<SemanticDiagnostic> diagnostics, string message, string source, SemanticLocation? location = null)
        => diagnostics.Add(SemanticDiagnostics.Warning(message, location, source));
}
