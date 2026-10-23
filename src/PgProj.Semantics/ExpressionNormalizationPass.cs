// Step 11 — ExpressionNormalizationPass
// Implement the ExpressionNormalizationPass for the semantic pipeline.
// This pass performs *expression normalization* — rewriting ExpressionNode trees
// into canonical, comparable forms. It runs AFTER SemanticBinder (Deep Binding)
// and BEFORE FunctionBodySemanticPass.
//
// === Goals ===
// 1. Normalize ExpressionNode trees into canonical form:
//      - normalize identifier casing (lowercase for PostgreSQL)
//      - normalize table-qualified and schema-qualified identifiers
//      - normalize operator shapes (flatten nested binary ops)
//      - normalize function call shapes (lowercase names, canonical argument order)
//      - normalize literal representations (trim quotes, unify numeric formats)
//      - normalize composite expressions (flatten nested lists)
// 2. Prepare expressions for:
//      - semantic binding (Step 12)
//      - diffing (Step 14)
//      - CHECK expression validation (Step 12)
//      - function body semantic mapping (Step 12)
// 3. Do NOT:
//      - resolve types (TypeResolutionPass)
//      - bind semantic references (SemanticBinder)
//      - extract dependencies (DeepDependencyPass)
//      - validate expressions (ValidationPass)
// 4. ExpressionNormalizationPass must remain syntactic-only.
//
// === Requirements ===
// - Create an ExpressionNormalizationPass class with:
//       DatabaseModel Execute(DatabaseModel model)
// - For each semantic definition:
//       - Normalize ExpressionNode trees:
//             * ColumnDefinition.DefaultExpressionNode
//             * ConstraintDefinition.CheckExpressionNode
//             * ViewDefinition.SelectExpressionNode
//             * IndexDefinition.PredicateExpressionNode
//             * FunctionDefinition.BodyExpressionNode
// - Add helper methods:
//       - NormalizeExpression(ExpressionNode node) → ExpressionNode
//       - NormalizeIdentifier(ExpressionNode node)
//       - NormalizeFunctionCall(ExpressionNode node)
//       - NormalizeOperator(ExpressionNode node)
//       - NormalizeLiteral(ExpressionNode node)
//       - NormalizeComposite(ExpressionNode node)
//       - AddDiagnostic(List<SemanticDiagnostic> diagnostics, string message, string source)
//
// === Normalization Rules ===
// - Identifiers:
//       * lowercase all identifiers (PostgreSQL canonical form)
//       * convert "schema.table.column" → ["schema", "table", "column"] components
//       * trim whitespace
// - Function calls:
//       * lowercase function names
//       * normalize argument lists (remove empty args, flatten nested lists)
// - Operators:
//       * flatten nested binary operators (e.g., ((a + b) + c) → a + b + c)
//       * normalize operator names (e.g., "AND", "and" → "and")
// - Literals:
//       * trim quotes for strings
//       * normalize numeric formats (e.g., "01" → "1")
//       * normalize boolean literals ("TRUE" → "true")
// - Composite expressions:
//       * flatten nested composite nodes
//       * remove empty children
//
// === Diagnostics ===
// - Emit warnings for:
//       * malformed ExpressionNode trees
//       * unknown expression kinds
//       * normalization failures
//
// === TODO for deeper semantic handling (later passes) ===
// - CHECK expression semantic binding (Step 12)
// - Function body semantic mapping (Step 12)
// - View expression semantic mapping (Step 13)
// - DiffEngine semantic comparison (Step 14)
//
// === Copilot Guidance ===
// Generate scaffolding and initial implementations.
// Focus on syntactic normalization only.
// Emit diagnostics for malformed expressions.
// Leave TODO comments for deeper semantic handling.
// Begin by generating ExpressionNormalizationPass.cs with an Execute() method and stubs for
// NormalizeExpression(), NormalizeIdentifier(), NormalizeFunctionCall(),
// NormalizeOperator(), NormalizeLiteral(), NormalizeComposite(), and AddDiagnostic().

using System.Globalization;

namespace PgProj.Semantics;

internal sealed class ExpressionNormalizationPass
{
    internal static string NormalizeExpression(string expr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expr);

        var normalized = string.Join(' ', expr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Trim();
    }

    public DatabaseModel Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var diagnostics = model.Diagnostics.ToList();
        var tables = model.Tables.Values
            .Select(table => NormalizeTable(table, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var views = model.Views.Values
            .Select(view => NormalizeView(view, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var functions = model.Functions.Values
            .Select(function => NormalizeFunction(function, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var procedures = model.Procedures.Values
            .Select(procedure => NormalizeProcedure(procedure, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var indexes = model.Indexes.Values
            .Select(index => NormalizeIndex(index, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var constraints = model.Constraints.Values
            .Select(constraint => NormalizeConstraint(constraint, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);

        return model with
        {
            Tables = tables,
            Views = views,
            Functions = functions,
            Procedures = procedures,
            Indexes = indexes,
            Constraints = constraints,
            Diagnostics = diagnostics,
        };
    }

    private TableDefinition NormalizeTable(TableDefinition table, List<SemanticDiagnostic> diagnostics)
        => table with
        {
            Columns = table.Columns.Select(column => NormalizeColumn(column, diagnostics)).ToArray(),
        };

    private ColumnDefinition NormalizeColumn(ColumnDefinition column, List<SemanticDiagnostic> diagnostics)
    {
        var normalizedNode = column.DefaultExpressionNode is null
            ? null
            : Placed(diagnostics, column.Location, () => NormalizeExpression(column.DefaultExpressionNode, diagnostics, column.Kind, column.Key));

        // A default is compared through DefaultExpressionCanonicalizer, not the expression tree: the tree drops the
        // quotes of string literals (so '0' and 0 render alike) and cannot see a cast. The raw text stays alongside
        // for deployment (G-56).
        return column with
        {
            DefaultExpressionNode = normalizedNode,
            NormalizedDefaultExpression = column.GetComparableDefaultExpression()
                ?? (normalizedNode is null ? column.NormalizedDefaultExpression : RenderExpression(normalizedNode)),
        };
    }

    private ViewDefinition NormalizeView(ViewDefinition view, List<SemanticDiagnostic> diagnostics)
    {
        var normalizedText = string.IsNullOrWhiteSpace(view.RawBodyExpression)
            ? null
            : NormalizeSqlStatement(view.RawBodyExpression);
        var normalizedNode = normalizedText is null
            ? null
            : ExpressionNode.Simple(normalizedText, []);

        return view with
        {
            SelectExpressionNode = normalizedNode,
            NormalizedExpressions = normalizedText is null ? [] : [normalizedText],
        };
    }

    internal static string NormalizeSqlStatement(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var result = new System.Text.StringBuilder(sql.Length);
        var pendingWhitespace = false;

        for (var index = 0; index < sql.Length;)
        {
            if (char.IsWhiteSpace(sql[index]))
            {
                pendingWhitespace = result.Length > 0;
                index++;
                continue;
            }

            if (sql[index] == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not '\r' and not '\n')
                {
                    index++;
                }

                pendingWhitespace = result.Length > 0;
                continue;
            }

            if (sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index += 2;
                var depth = 1;
                while (index < sql.Length && depth > 0)
                {
                    if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
                    {
                        depth++;
                        index += 2;
                    }
                    else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
                    {
                        depth--;
                        index += 2;
                    }
                    else
                    {
                        index++;
                    }
                }

                pendingWhitespace = result.Length > 0;
                continue;
            }

            if (sql[index] is '\'' or '"')
            {
                AppendPendingWhitespace(result, ref pendingWhitespace);
                var quote = sql[index++];
                result.Append(quote);
                while (index < sql.Length)
                {
                    var current = sql[index++];
                    result.Append(current);
                    if (current != quote)
                    {
                        continue;
                    }

                    if (index < sql.Length && sql[index] == quote)
                    {
                        result.Append(sql[index++]);
                        continue;
                    }

                    break;
                }

                continue;
            }

            if (sql[index] == '$')
            {
                var delimiterEnd = index + 1;
                while (delimiterEnd < sql.Length && (char.IsLetterOrDigit(sql[delimiterEnd]) || sql[delimiterEnd] == '_'))
                {
                    delimiterEnd++;
                }

                if (delimiterEnd < sql.Length && sql[delimiterEnd] == '$')
                {
                    AppendPendingWhitespace(result, ref pendingWhitespace);
                    var delimiter = sql[index..(delimiterEnd + 1)];
                    var closingDelimiter = sql.IndexOf(delimiter, delimiterEnd + 1, StringComparison.Ordinal);
                    var tokenEnd = closingDelimiter < 0 ? sql.Length : closingDelimiter + delimiter.Length;
                    result.Append(sql, index, tokenEnd - index);
                    index = tokenEnd;
                    continue;
                }
            }

            if (char.IsLetter(sql[index]) || sql[index] == '_')
            {
                AppendPendingWhitespace(result, ref pendingWhitespace);
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
                {
                    result.Append(char.ToLowerInvariant(sql[index++]));
                }

                continue;
            }

            if (sql[index] is ',' or '(' or ')' or ';' or '.' or '+' or '-' or '*' or '/' or '%' or '=' or '<' or '>' or '!')
            {
                while (result.Length > 0 && result[^1] == ' ')
                {
                    result.Length--;
                }

                result.Append(sql[index++]);
                pendingWhitespace = false;
                continue;
            }

            AppendPendingWhitespace(result, ref pendingWhitespace);
            result.Append(sql[index++]);
        }

        return result.ToString().Trim().TrimEnd(';');
    }

    private static void AppendPendingWhitespace(System.Text.StringBuilder result, ref bool pendingWhitespace)
    {
        if (pendingWhitespace
            && result.Length > 0
            && result[^1] is not '(' and not ',' and not '.'
            and not '+' and not '-' and not '*' and not '/' and not '%'
            and not '=' and not '<' and not '>' and not '!')
        {
            result.Append(' ');
        }

        pendingWhitespace = false;
    }

    private FunctionDefinition NormalizeFunction(FunctionDefinition function, List<SemanticDiagnostic> diagnostics)
    {
        var normalizedNode = function.BodyExpressionNode is null
            ? null
            : Placed(diagnostics, function.Location, () => NormalizeExpression(function.BodyExpressionNode, diagnostics, function.Kind, function.Key));

        return function with
        {
            BodyExpressionNode = normalizedNode,
            NormalizedExpressions = normalizedNode is null ? [] : [RenderExpression(normalizedNode)],
        };
    }

    private ProcedureDefinition NormalizeProcedure(ProcedureDefinition procedure, List<SemanticDiagnostic> diagnostics)
    {
        var normalizedNode = procedure.BodyExpressionNode is null
            ? null
            : Placed(diagnostics, procedure.Location, () => NormalizeExpression(procedure.BodyExpressionNode, diagnostics, procedure.Kind, procedure.Key));

        return procedure with
        {
            BodyExpressionNode = normalizedNode,
            NormalizedExpressions = normalizedNode is null ? [] : [RenderExpression(normalizedNode)],
        };
    }

    private IndexDefinition NormalizeIndex(IndexDefinition index, List<SemanticDiagnostic> diagnostics)
    {
        var normalizedNode = index.PredicateExpressionNode is null
            ? null
            : Placed(diagnostics, index.Location, () => NormalizeExpression(index.PredicateExpressionNode, diagnostics, index.Kind, index.Key));

        return index with
        {
            PredicateExpressionNode = normalizedNode,
            NormalizedExpressions = normalizedNode is null ? [] : [RenderExpression(normalizedNode)],
        };
    }

    private ConstraintDefinition NormalizeConstraint(ConstraintDefinition constraint, List<SemanticDiagnostic> diagnostics)
    {
        var normalizedNode = constraint.CheckExpressionNode is null
            ? null
            : Placed(diagnostics, constraint.Location, () => NormalizeExpression(constraint.CheckExpressionNode, diagnostics, constraint.Kind, constraint.Key));

        return constraint with
        {
            CheckExpressionNode = normalizedNode,
            NormalizedExpressions = normalizedNode is null ? [] : [RenderExpression(normalizedNode)],
        };
    }

    private ExpressionNode NormalizeExpression(
        ExpressionNode node,
        List<SemanticDiagnostic> diagnostics,
        string ownerKind,
        string ownerKey)
    {
        if (string.IsNullOrWhiteSpace(node.Kind))
        {
            AddDiagnostic(diagnostics, $"{ownerKind} '{ownerKey}' contains a malformed expression node with no kind.", nameof(ExpressionNormalizationPass));
            return node;
        }

        return node.Kind switch
        {
            "Identifier" => NormalizeIdentifier(node),
            "FunctionCall" => NormalizeFunctionCall(node, diagnostics, ownerKind, ownerKey),
            "Operator" => NormalizeOperator(node, diagnostics, ownerKind, ownerKey),
            "Literal" => NormalizeLiteral(node),
            "SimpleExpression" => NormalizeComposite(node, diagnostics, ownerKind, ownerKey),
            _ => NormalizeUnknown(node, diagnostics, ownerKind, ownerKey),
        };
    }

    private ExpressionNode NormalizeIdentifier(ExpressionNode node)
    {
        var parts = node.Text
            .Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.ToLowerInvariant())
            .ToArray();

        var normalizedText = parts.Length == 0
            ? node.Text.Trim().ToLowerInvariant()
            : string.Join('.', parts);

        return node with
        {
            Text = normalizedText,
            Children = [],
        };
    }

    private ExpressionNode NormalizeFunctionCall(
        ExpressionNode node,
        List<SemanticDiagnostic> diagnostics,
        string ownerKind,
        string ownerKey)
    {
        var normalizedChildren = node.Children
            .Select(child => NormalizeExpression(child, diagnostics, ownerKind, ownerKey))
            .Where(static child => !IsEmptyNode(child))
            .SelectMany(static child => child.Kind == "SimpleExpression" && child.Children.Count > 0 ? child.Children : [child])
            .ToArray();

        return node with
        {
            Text = NormalizeIdentifier(node with { Kind = "Identifier" }).Text,
            Children = normalizedChildren,
        };
    }

    private ExpressionNode NormalizeOperator(
        ExpressionNode node,
        List<SemanticDiagnostic> diagnostics,
        string ownerKind,
        string ownerKey)
    {
        var normalizedOperator = node.Text.Trim().ToLowerInvariant();
        var normalizedChildren = node.Children
            .Select(child => NormalizeExpression(child, diagnostics, ownerKind, ownerKey))
            .Where(static child => !IsEmptyNode(child))
            .ToArray();

        var flattenedChildren = new List<ExpressionNode>();
        foreach (var child in normalizedChildren)
        {
            if (child.Kind == "Operator" && string.Equals(child.Text, normalizedOperator, StringComparison.OrdinalIgnoreCase))
            {
                flattenedChildren.AddRange(child.Children);
            }
            else
            {
                flattenedChildren.Add(child);
            }
        }

        return node with
        {
            Text = normalizedOperator,
            Children = flattenedChildren.ToArray(),
        };
    }

    private ExpressionNode NormalizeLiteral(ExpressionNode node)
    {
        var trimmed = node.Text.Trim();

        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            trimmed = trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        else if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
              || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.ToLowerInvariant();
        }
        else if (decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue))
        {
            trimmed = decimalValue.ToString(CultureInfo.InvariantCulture);
        }

        return node with
        {
            Text = trimmed,
            Children = [],
        };
    }

    private ExpressionNode NormalizeComposite(
        ExpressionNode node,
        List<SemanticDiagnostic> diagnostics,
        string ownerKind,
        string ownerKey)
    {
        var normalizedChildren = node.Children
            .Select(child => NormalizeExpression(child, diagnostics, ownerKind, ownerKey))
            .Where(static child => !IsEmptyNode(child))
            .ToArray();

        var flattenedChildren = new List<ExpressionNode>();
        foreach (var child in normalizedChildren)
        {
            if (child.Kind == node.Kind)
            {
                flattenedChildren.AddRange(child.Children);
            }
            else
            {
                flattenedChildren.Add(child);
            }
        }

        return node with
        {
            Text = NormalizeCompositeText(node.Text),
            Children = flattenedChildren.ToArray(),
        };
    }

    private ExpressionNode NormalizeUnknown(
        ExpressionNode node,
        List<SemanticDiagnostic> diagnostics,
        string ownerKind,
        string ownerKey)
    {
        AddDiagnostic(diagnostics, $"{ownerKind} '{ownerKey}' contains unknown expression kind '{node.Kind}'.", nameof(ExpressionNormalizationPass));
        return NormalizeComposite(node, diagnostics, ownerKind, ownerKey);
    }

    internal static string RenderExpression(ExpressionNode node)
        => node.Kind switch
        {
            "FunctionCall" => $"{node.Text}({string.Join(", ", node.Children.Select(RenderExpression))})",
            "Operator" => string.Join($" {node.Text} ", node.Children.Select(RenderExpression)),
            "SimpleExpression" when node.Children.Count > 0 => string.Join(", ", node.Children.Select(RenderExpression)),
            _ => node.Text,
        };

    private static string NormalizeCompositeText(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsEmptyNode(ExpressionNode node)
        => string.IsNullOrWhiteSpace(node.Text) && node.Children.Count == 0;

    /// <summary>Runs <paramref name="normalize"/>, giving what it reports without a place the place of the object it normalizes (G-309e).</summary>
    private static T Placed<T>(List<SemanticDiagnostic> diagnostics, SemanticLocation? location, Func<T> normalize)
    {
        var start = diagnostics.Count;
        var result = normalize();
        SemanticDiagnostics.PlaceFrom(diagnostics, start, location);
        return result;
    }

    private static void AddDiagnostic(List<SemanticDiagnostic> diagnostics, string message, string source, SemanticLocation? location = null)
        => diagnostics.Add(SemanticDiagnostics.Warning(message, location, source));
}