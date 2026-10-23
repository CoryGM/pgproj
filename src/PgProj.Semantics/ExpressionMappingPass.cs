// Step 7 — ExpressionMappingPass
// Implement the ExpressionMappingPass for the semantic pipeline.
// This pass performs *shallow expression mapping* — converting raw SQL expression
// text into structured expression fragments attached to semantic definitions.
// It runs AFTER SemanticBinder and BEFORE DeepDependencyPass.
//
// === Goals ===
// 1. Map raw SQL expressions into structured ExpressionNode fragments:
//      - Column default expressions
//      - CHECK constraint expressions
//      - View SELECT expressions
//      - Index predicate expressions
//      - Function body expressions (shallow only)
// 2. Preserve AST node references for later deep passes.
// 3. Do NOT:
//      - resolve types (TypeResolutionPass)
//      - normalize expressions (ExpressionNormalizationPass)
//      - extract deep dependencies (DeepDependencyPass)
//      - validate expressions (ValidationPass)
//      - bind semantic references (SemanticBinder)
// 4. ExpressionMappingPass must remain shallow and structural.
//
// === Requirements ===
// - Create an ExpressionMappingPass class with:
//       DatabaseModel Execute(DatabaseModel model)
// - For each semantic definition:
//       - Map raw expressions into ExpressionNode trees
//       - Attach ExpressionNode trees to:
//             * ColumnDefinition.DefaultExpressionNode
//             * ConstraintDefinition.CheckExpressionNode
//             * ViewDefinition.SelectExpressionNode
//             * IndexDefinition.PredicateExpressionNode
//             * FunctionDefinition.BodyExpressionNode
// - Add helper methods:
//       - MapColumnDefaultExpression(ColumnDefinition column)
//       - MapCheckExpression(ConstraintDefinition constraint)
//       - MapViewExpression(ViewDefinition view)
//       - MapIndexPredicate(IndexDefinition index)
//       - MapFunctionBody(FunctionDefinition function)
//       - (ParseExpression, the text fallback, was removed by G-90: expressions come from pg_query's tree)
//
// === Expression Mapping Rules ===
// - Use pg_query JSON fragments when available (ColumnNode.RawDefaultExpression, etc.)
// - Expressions are read from pg_query's tree; with no tree (only a hand-built node), one unparsed leaf (G-90 removed the text parser)
// - ExpressionNode should be a shallow AST:
//       * identifiers
//       * literals
//       * operators
//       * function calls
//       * simple expressions
// - Do NOT:
//       * normalize identifiers
//       * resolve types
//       * bind references
//       * extract dependencies
//
// === Diagnostics ===
// - Emit warnings for:
//       * unparsable expressions
//       * empty expressions
//       * malformed fragments
//
// === TODO for deeper expression handling (later passes) ===
// - Deep dependency extraction (Step 8 — DeepDependencyPass)
// - Type resolution (Step 9 — TypeResolutionPass)
// - Semantic binding (Step 10 — SemanticBinder)
// - Expression normalization (Step 11 — ExpressionNormalizationPass)
// - Function body semantic mapping (Step 12)
// - View expression semantic mapping (Step 13)
//
// === Copilot Guidance ===
// Generate scaffolding and initial implementations.
// Focus on shallow expression mapping only.
// Emit diagnostics for malformed expressions.
// Leave TODO comments for deeper expression handling.
// Begin by generating ExpressionMappingPass.cs with an Execute() method and stubs for
// MapColumnDefaultExpression(), MapCheckExpression(), MapViewExpression(),
// MapIndexPredicate(), and MapFunctionBody().

using PgProj.Parser;

namespace PgProj.Semantics;

internal sealed class ExpressionMappingPass
{
    public DatabaseModel Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var diagnostics = model.Diagnostics.ToList();
        var tables = model.Tables.Values
            .Select(table => MapTableExpressions(table, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var views = model.Views.Values
            .Select(view => MapViewExpression(view, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var functions = model.Functions.Values
            .Select(function => MapFunctionBody(function, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var procedures = model.Procedures.Values
            .Select(procedure => MapProcedureBody(procedure, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var indexes = model.Indexes.Values
            .Select(index => MapIndexPredicate(index, diagnostics))
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var constraints = model.Constraints.Values
            .Select(constraint => MapCheckExpression(constraint, diagnostics))
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

    private TableDefinition MapTableExpressions(TableDefinition table, List<SemanticDiagnostic> diagnostics)
        => table with
        {
            Columns = table.Columns.Select(column => MapColumnDefaultExpression(column, diagnostics)).ToArray(),
        };

    private ColumnDefinition MapColumnDefaultExpression(ColumnDefinition column, List<SemanticDiagnostic> diagnostics)
    {
        // A generated column has no default: its expression is what binding and dependency extraction must see, so a function it calls is
        // created first and a column or function that does not exist is reported (G-71).
        var rawExpression = column.IsGenerated && !string.IsNullOrWhiteSpace(column.GeneratedExpression)
            ? column.GeneratedExpression
            : column.RawDefaultExpression;
        if (rawExpression is null || column.IsSerial)
        {
            // SERIAL/BIGSERIAL/SMALLSERIAL columns carry a synthesized `nextval(...)` default that was
            // not present in the original SQL text (see PgQueryParser.BuildSerialDefaultExpression), so
            // there is no real expression to parse or extract dependencies from.
            return column;
        }

        // The tree says what is a column, a function, and a cast (G-102). Every parsed column has one; a node built without one (only a
        // hand-built test node can be) is kept as one unparsed leaf, since the text parser that used to stand in for it is gone (G-90).
        // A tree that refers to no column or function (ARRAY[]::int[]) has no reference nodes, but it did parse, so it is a leaf.
        var ast = column.IsGenerated ? column.DeclarationNode?.GeneratedExpressionAst : column.DeclarationNode?.DefaultExpressionAst;
        var expression = ast is not null
            ? ExpressionAstReader.ToExpressionNode(ast) ?? ExpressionNode.Simple(rawExpression.Trim(), [])
            : ExpressionNode.Simple(rawExpression.Trim(), []);
        return column with
        {
            DefaultExpressionNode = expression,
        };
    }

    private ConstraintDefinition MapCheckExpression(ConstraintDefinition constraint, List<SemanticDiagnostic> diagnostics)
    {
        if (constraint.RawCheckExpression is null)
        {
            return constraint;
        }

        // The tree says what is a column and what is a function (G-72); with no tree (only a hand-built node), one unparsed leaf (G-90).
        var expression = ExpressionAstReader.ToExpressionNode(constraint.DeclarationNode?.CheckExpressionAst)
            ?? ExpressionNode.Simple(constraint.RawCheckExpression.Trim(), []);

        // TODO: Preserve richer CHECK predicate structure once PgProj.Parser exposes dedicated expression nodes.
        return constraint with
        {
            CheckExpressionNode = expression,
        };
    }

    private ViewDefinition MapViewExpression(ViewDefinition view, List<SemanticDiagnostic> diagnostics)
    {
        var placedFrom = diagnostics.Count;
        var rawExpression = ExtractRawExpression(view.DeclarationNode, diagnostics, view.Kind, view.Key, "SELECT");
        SemanticDiagnostics.PlaceFrom(diagnostics, placedFrom, view.Location);
        var expression = rawExpression is null
            ? null
            : ExpressionNode.Simple(rawExpression.Trim(), []);

        return view with
        {
            SelectExpressionNode = expression,
        };
    }

    private IndexDefinition MapIndexPredicate(IndexDefinition index, List<SemanticDiagnostic> diagnostics)
    {
        var placedFrom = diagnostics.Count;
        var rawExpression = ExtractRawExpression(index.DeclarationNode, diagnostics, index.Kind, index.Key, "predicate");
        SemanticDiagnostics.PlaceFrom(diagnostics, placedFrom, index.Location);
        var expression = rawExpression is null
            ? null
            : ExpressionAstReader.ToExpressionNode(index.DeclarationNode?.PredicateAst)
                ?? ExpressionNode.Simple(rawExpression.Trim(), []);

        // INCLUDE lists and index-key expressions are captured structurally on IndexDefinition.KeyElements/IncludeColumns
        // by DeclarationPass; only the partial-index predicate flows through the raw-expression pipeline here.
        return index with
        {
            PredicateExpressionNode = expression,
        };
    }

    private FunctionDefinition MapFunctionBody(FunctionDefinition function, List<SemanticDiagnostic> diagnostics)
    {
        var placedFrom = diagnostics.Count;
        var rawExpression = ExtractRawExpression(function.DeclarationNode, diagnostics, function.Kind, function.Key, "body");
        SemanticDiagnostics.PlaceFrom(diagnostics, placedFrom, function.Location);
        var expression = rawExpression is null
            ? null
            : ExpressionNode.Simple(rawExpression.Trim(), []);

        // TODO: Map function statements, RETURN clauses, nested SQL, and procedural blocks when PgProj.Parser preserves function-body expression fragments.
        return function with
        {
            BodyExpressionNode = expression,
        };
    }

    private ProcedureDefinition MapProcedureBody(ProcedureDefinition procedure, List<SemanticDiagnostic> diagnostics)
    {
        var placedFrom = diagnostics.Count;
        var rawExpression = ExtractRawExpression(procedure.DeclarationNode, diagnostics, procedure.Kind, procedure.Key, "body");
        SemanticDiagnostics.PlaceFrom(diagnostics, placedFrom, procedure.Location);
        var expression = rawExpression is null
            ? null
            : ExpressionNode.Simple(rawExpression.Trim(), []);

        // Mapped like a function body (G-66): without it a change to a procedure's body was invisible to the diff.
        return procedure with
        {
            BodyExpressionNode = expression,
        };
    }

    private static string? ExtractRawExpression(
        object? declarationNode,
        List<SemanticDiagnostic> diagnostics,
        string ownerKind,
        string ownerKey,
        string expressionKind)
    {
        if (declarationNode is null)
        {
            return null;
        }

        if (declarationNode is FunctionNode functionNode && string.Equals(expressionKind, "body", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(functionNode.RawBodyExpression)
                ? null
                : functionNode.RawBodyExpression;
        }

        if (declarationNode is ProcedureNode procedureNode && string.Equals(expressionKind, "body", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(procedureNode.RawBodyExpression)
                ? null
                : procedureNode.RawBodyExpression;
        }

        if (declarationNode is ViewNode viewNode && string.Equals(expressionKind, "SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(viewNode.RawBodyExpression)
                ? null
                : viewNode.RawBodyExpression;
        }

        if (declarationNode is IndexNode indexNode && string.Equals(expressionKind, "predicate", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(indexNode.RawPredicateExpression)
                ? null
                : indexNode.RawPredicateExpression;
        }

        AddDiagnostic(diagnostics, $"{ownerKind} '{ownerKey}' does not expose a raw {expressionKind} expression fragment yet.", nameof(ExpressionMappingPass));
        return null;
    }

    private static void AddDiagnostic(List<SemanticDiagnostic> diagnostics, string message, string source, SemanticLocation? location = null)
        => diagnostics.Add(SemanticDiagnostics.Warning(message, location, source));
}
