// Step 5 — ValidationPass
// Implement the ValidationPass for the semantic pipeline.
// This pass performs *shallow, structural validation* of semantic definitions.
// It runs AFTER DependencyPass and BEFORE SemanticBinder.
//
// === Goals ===
// 1. Validate basic structural correctness of semantic definitions:
//      - Tables have unique column names
//      - Columns have required metadata (name, raw type)
//      - Constraints reference existing columns (name-only check)
//      - Indexes reference existing columns (name-only check)
//      - Functions have required metadata (name, language)
//      - Views have required metadata (name)
// 2. Produce SqlDiagnostic entries for structural issues.
// 3. Do NOT:
//      - resolve references (SemanticBinder)
//      - validate types (TypeResolutionPass)
//      - validate expressions (ExpressionNormalizationPass)
//      - validate dependency correctness (DeepDependencyPass)
//      - validate CHECK expressions (ExpressionMappingPass)
// 4. ValidationPass must remain shallow and name‑based.
//
// === Requirements ===
// - Create a ValidationPass class with:
//       DatabaseModel Execute(DatabaseModel model)
// - For each semantic definition:
//       - Validate structural correctness
//       - Emit SqlDiagnostic entries for issues
//       - Return a new DatabaseModel with updated diagnostics
// - Add helper methods:
//       - ValidateTable(TableDefinition table)
//       - ValidateColumn(ColumnDefinition column)
//       - ValidateConstraint(ConstraintDefinition constraint, TableDefinition table)
//       - ValidateIndex(IndexDefinition index, TableDefinition table)
//       - ValidateFunction(FunctionDefinition function)
//       - ValidateView(ViewDefinition view)
// - Add helper for diagnostic creation:
//       - AddDiagnostic(List<SqlDiagnostic> diagnostics, string message, string source)
//
// === Structural Validation Rules ===
// - TableDefinition:
//       * Columns must have unique names
//       * Columns must have non-null RawType
// - ColumnDefinition:
//       * ColumnName must not be null/empty
//       * RawType must not be null/empty
// - ConstraintDefinition:
//       * ConstraintName must not be null/empty
//       * Column references must exist in the owning table (name-only check)
//       * FK referenced columns must be present in the raw dependency list (name-only check)
// - IndexDefinition:
//       * IndexName must not be null/empty
//       * Column references must exist in the owning table (name-only check)
// - FunctionDefinition:
//       * FunctionName must not be null/empty
//       * Language must not be null/empty
// - ViewDefinition:
//       * ViewName must not be null/empty
//
// === TODO for deeper validation (later passes) ===
// - CHECK expression validation (ExpressionMappingPass)
// - FK match types and actions (DeepDependencyPass)
// - Type correctness (TypeResolutionPass)
// - Semantic resolution (SemanticBinder)
// - Expression normalization (ExpressionNormalizationPass)
//
// === Copilot Guidance ===
// Generate scaffolding and initial implementations.
// Focus on shallow structural validation only.
// Emit SqlDiagnostic entries for structural issues.
// Leave TODO comments for deeper validation that belongs in later passes.
// Begin by generating ValidationPass.cs with an Execute() method and stubs for
// ValidateTable(), ValidateColumn(), ValidateConstraint(), ValidateIndex(),
// ValidateFunction(), ValidateView(), and AddDiagnostic().


namespace PgProj.Semantics;

internal sealed class ValidationPass
{
    public DatabaseModel Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var diagnostics = new List<SemanticDiagnostic>(model.Diagnostics);

        foreach (var table in model.Tables.Values)
        {
            ValidateTable(table, diagnostics);

            foreach (var column in table.Columns)
            {
                ValidateColumn(column, diagnostics);
            }
        }

        foreach (var constraint in model.Constraints.Values)
        {
            if (!TryGetOwningTable(model, constraint.SchemaName, constraint.TableName, out var table))
            {
                AddDiagnostic(
                    diagnostics,
                    $"Constraint '{constraint.Key}' references missing owning table '{FormatReference(constraint.SchemaName, constraint.TableName)}'.",
                    nameof(ValidationPass),
                    SchemaScopeSeverity.ForReference(model, constraint.SchemaName), location: constraint.Location);
                continue;
            }

            ValidateConstraint(model, constraint, table, diagnostics);
        }

        foreach (var index in model.Indexes.Values)
        {
            if (!TryGetOwningTable(model, index.SchemaName, index.TableName, out var table))
            {
                if (model.FindMaterializedView(index.SchemaName, index.TableName) is not null)
                {
                    // An index on a materialized view (G-171).
                    continue;
                }

                AddDiagnostic(
                    diagnostics,
                    $"Index '{index.Key}' references missing owning table '{FormatReference(index.SchemaName, index.TableName)}'.",
                    nameof(ValidationPass),
                    SchemaScopeSeverity.ForReference(model, index.SchemaName), location: index.Location);
                continue;
            }

            ValidateIndex(model, index, table, diagnostics);
        }

        foreach (var function in model.Functions.Values)
        {
            ValidateFunction(function, diagnostics);
        }

        foreach (var view in model.Views.Values)
        {
            ValidateView(view, diagnostics);
        }

        return model with
        {
            Diagnostics = diagnostics,
        };
    }

    private static void ValidateTable(TableDefinition table, List<SemanticDiagnostic> diagnostics)
    {
        var duplicateColumns = table.Columns
            .Where(static column => !string.IsNullOrWhiteSpace(column.Name))
            .GroupBy(static column => column.Name, PgProj.Parser.SqlIdentifiers.NameComparer)
            .Where(static grouping => grouping.Count() > 1)
            .Select(static grouping => grouping.Key)
            .ToArray();

        foreach (var duplicateColumn in duplicateColumns)
        {
            AddDiagnostic(diagnostics, $"Table '{table.Key}' contains duplicate column '{duplicateColumn}'.", nameof(ValidationPass), location: table.Location);
        }

        // TODO: Leave type correctness and expression validation to later passes.
    }

    private static void ValidateColumn(ColumnDefinition column, List<SemanticDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(column.Name))
        {
            AddDiagnostic(diagnostics, $"Column '{column.Key}' is missing a name.", nameof(ValidationPass), location: column.Location);
        }

        if (string.IsNullOrWhiteSpace(column.RawType))
        {
            AddDiagnostic(diagnostics, $"Column '{column.Key}' is missing a raw type.", nameof(ValidationPass), location: column.Location);
        }

        // TODO: Leave expression normalization and default-expression validation to later passes.
    }

    private static void ValidateConstraint(DatabaseModel model, ConstraintDefinition constraint, TableDefinition table, List<SemanticDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(constraint.Name))
        {
            AddDiagnostic(diagnostics, $"Constraint '{constraint.Key}' is missing a name.", nameof(ValidationPass), location: constraint.Location);
        }

        foreach (var columnReference in constraint.PrimaryKeyColumns)
        {
            ValidateOwningTableColumn(model, constraint, table, columnReference, "primary key", diagnostics);
        }

        foreach (var columnReference in constraint.UniqueColumns)
        {
            ValidateOwningTableColumn(model, constraint, table, columnReference, "unique", diagnostics);
        }

        foreach (var referencedColumn in constraint.ForeignKeyReferencedColumns)
        {
            if (string.IsNullOrWhiteSpace(referencedColumn))
            {
                AddDiagnostic(diagnostics, $"Constraint '{constraint.Key}' contains a blank foreign key referenced column.", nameof(ValidationPass), location: constraint.Location);
                continue;
            }

            if (!constraint.ReferencedColumns.Any(candidate => MatchesReference(candidate, referencedColumn)))
            {
                AddDiagnostic(diagnostics, $"Constraint '{constraint.Key}' foreign key column '{referencedColumn}' is missing from the raw dependency list.", nameof(ValidationPass), location: constraint.Location);
            }
        }

        // TODO: Leave CHECK expression validation and foreign key type/action validation to later passes.
    }

    private static void ValidateIndex(DatabaseModel model, IndexDefinition index, TableDefinition table, List<SemanticDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(index.Name))
        {
            AddDiagnostic(diagnostics, $"Index '{index.Key}' is missing a name.", nameof(ValidationPass), location: index.Location);
        }

        foreach (var referencedColumn in index.ReferencedColumns)
        {
            ValidateOwningTableColumn(model, index, table, referencedColumn, "index", diagnostics);
        }

        // TODO: Leave index expression, predicate, and function validation to later passes.
    }

    private static void ValidateFunction(FunctionDefinition function, List<SemanticDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(function.Name))
        {
            AddDiagnostic(diagnostics, $"Function '{function.Key}' is missing a name.", nameof(ValidationPass), location: function.Location);
        }

        if (string.IsNullOrWhiteSpace(function.Language))
        {
            AddDiagnostic(diagnostics, $"Function '{function.Key}' is missing a language.", nameof(ValidationPass), location: function.Location);
        }

        // TODO: Leave function body and expression validation to later passes.
    }

    private static void ValidateView(ViewDefinition view, List<SemanticDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(view.Name))
        {
            AddDiagnostic(diagnostics, $"View '{view.Key}' is missing a name.", nameof(ValidationPass), location: view.Location);
        }

        // TODO: Leave view query validation to later passes.
    }

    private static void ValidateOwningTableColumn(
        DatabaseModel model,
        ISemanticNode node,
        TableDefinition table,
        string? reference,
        string referenceKind,
        List<SemanticDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            AddDiagnostic(diagnostics, $"{node.Kind} '{node.Key}' contains a blank {referenceKind} column reference.", nameof(ValidationPass), location: node.Location);
            return;
        }

        if (!HasColumn(table, reference))
        {
            AddDiagnostic(
                diagnostics,
                $"{node.Kind} '{node.Key}' references missing column '{reference}' on table '{table.Key}'.",
                nameof(ValidationPass),
                SchemaScopeSeverity.ForReference(model, table.SchemaName), location: node.Location);
        }
    }

    private static bool TryGetOwningTable(DatabaseModel model, string? schemaName, string? tableName, out TableDefinition table)
    {
        table = null!;

        if (string.IsNullOrWhiteSpace(tableName))
        {
            return false;
        }

        var key = DatabaseModel.CreateKey(schemaName, tableName);
        if (model.Tables.TryGetValue(key, out table!))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            return false;
        }

        table = model.Tables.Values.FirstOrDefault(candidate => string.Equals(candidate.Name, tableName, StringComparison.Ordinal))!;
        return table is not null;
    }

    private static bool HasColumn(TableDefinition table, string reference)
    {
        var columnName = GetSimpleName(reference);
        return table.Columns.Any(column => string.Equals(column.Name, columnName, StringComparison.Ordinal));
    }

    private static bool MatchesReference(string? left, string? right)
        => string.Equals(GetSimpleName(left), GetSimpleName(right), StringComparison.OrdinalIgnoreCase);

    private static string GetSimpleName(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        var trimmed = reference.Trim();
        var separatorIndex = trimmed.LastIndexOf('.');
        return separatorIndex >= 0 ? trimmed[(separatorIndex + 1)..] : trimmed;
    }

    private static string FormatReference(string? schemaName, string? name)
        => string.IsNullOrWhiteSpace(name)
            ? "<unknown>"
            : string.IsNullOrWhiteSpace(schemaName)
                ? name.Trim()
                : DatabaseModel.CreateKey(schemaName, name);

    private static void AddDiagnostic(List<SemanticDiagnostic> diagnostics, string message, string source, SemanticDiagnosticSeverity severity = SemanticDiagnosticSeverity.Error, SemanticLocation? location = null)
        => diagnostics.Add(severity == SemanticDiagnosticSeverity.Warning
            ? SemanticDiagnostics.Warning(message, location, source)
            : SemanticDiagnostics.Error(message, location, source));
}
