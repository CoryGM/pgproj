// Step 4 — DependencyPass
// Implement the DependencyPass for the semantic pipeline.
// This pass extracts *raw, unresolved* dependency names from AST nodes and
// attaches them to semantic definitions. It runs AFTER ConstraintPass and
// BEFORE ValidationPass.
//
// === Goals ===
// 1. Move shallow dependency extraction out of SemanticBuilder and into this pass.
// 2. For each SqlNode (TableNode, ViewNode, FunctionNode, IndexNode, ConstraintNode):
//      - Collect raw referenced object names (tables, columns, types, functions)
//      - Attach these raw names to the corresponding semantic definition
// 3. Do NOT:
//      - resolve references (SemanticBinder + TypeResolutionPass)
//      - normalize expressions (ExpressionNormalizationPass)
//      - extract deep dependencies from expressions (ExpressionMappingPass + deep passes)
//      - validate dependency correctness (ValidationPass)
// 4. DependencyPass must remain shallow and name‑based.
//
// === Requirements ===
// - Create a DependencyPass class with:
//       DatabaseModel Execute(DatabaseModel model, IEnumerable<SqlSyntaxTree> syntaxTrees)
// - For each syntax tree:
//       - For each SqlNode:
//             - Use a shallow visitor (SqlDependencyVisitor) to collect raw names
//             - Attach raw names to the semantic definition via:
//                   ReferencedTables
//                   ReferencedColumns
//                   ReferencedTypes
//                   ReferencedFunctions
// - Add helper methods:
//       - CollectDependencyInfo(SqlNode node)
//       - AttachDependencies(ISemanticNode node, DependencyInfo info)
//       - ResolveTableKey / ResolveColumnKey / ResolveTypeKey / ResolveFunctionKey
//         (BUT only for name‑scoping, NOT semantic resolution)
// - Leave TODO comments for deeper dependency handling:
//       - expression‑level dependency extraction (ExpressionMappingPass)
//       - view/function/index expression mapping
//       - CHECK constraint expression mapping
//
// === Copilot Guidance ===
// Generate scaffolding and initial implementations.
// Focus on shallow name extraction only.
// Leave TODO comments for deeper dependency extraction.
// Begin by aligning DependencyPass.cs with this prompt and removing any logic
// that performs semantic resolution or deep dependency analysis.

using PgProj.Parser;

namespace PgProj.Semantics;

internal sealed class DependencyPass
{
    public DatabaseModel Execute(DatabaseModel model, IEnumerable<SqlSyntaxTree> syntaxTrees)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(syntaxTrees);

        var tables = new Dictionary<string, TableDefinition>(model.Tables, PgProj.Parser.SqlIdentifiers.NameComparer);
        var views = new Dictionary<string, ViewDefinition>(model.Views, PgProj.Parser.SqlIdentifiers.NameComparer);
        var functions = new Dictionary<string, FunctionDefinition>(model.Functions, PgProj.Parser.SqlIdentifiers.NameComparer);
        var procedures = new Dictionary<string, ProcedureDefinition>(model.Procedures, PgProj.Parser.SqlIdentifiers.NameComparer);
        var indexes = new Dictionary<string, IndexDefinition>(model.Indexes, PgProj.Parser.SqlIdentifiers.NameComparer);
        var constraints = new Dictionary<string, ConstraintDefinition>(model.Constraints, PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var syntaxTree in syntaxTrees)
        {
            foreach (var node in syntaxTree.Nodes)
            {
                var info = CollectDependencyInfo(node);

                switch (node)
                {
                    case TableNode tableNode:
                    {
                        var key = ResolveTableDefinitionKey(tableNode);
                        if (tables.TryGetValue(key, out var table))
                        {
                            tables[key] = (TableDefinition)AttachDependencies(table, info);
                        }

                        break;
                    }
                    case ViewNode viewNode:
                    {
                        // TODO: Move expression-level dependency extraction for view bodies into ExpressionMappingPass.
                        var key = ResolveViewDefinitionKey(viewNode);
                        if (views.TryGetValue(key, out var view))
                        {
                            views[key] = (ViewDefinition)AttachDependencies(view, info);
                        }

                        break;
                    }
                    case FunctionNode functionNode:
                    {
                        // TODO: Move function body expression mapping into ExpressionMappingPass.
                        var key = ResolveFunctionDefinitionKey(functionNode);
                        if (functions.TryGetValue(key, out var function))
                        {
                            functions[key] = (FunctionDefinition)AttachDependencies(function, info);
                        }

                        break;
                    }
                    case ProcedureNode procedureNode:
                    {
                        var key = DatabaseModel.CreateRoutineKey(procedureNode.SchemaName, procedureNode.Name ?? "unknown_procedure", procedureNode.Signature?.RenderArgumentTypeSignature());
                        if (procedures.TryGetValue(key, out var procedure))
                        {
                            procedures[key] = (ProcedureDefinition)AttachDependencies(procedure, info);
                        }

                        break;
                    }
                    case IndexNode indexNode:
                    {
                        // TODO: Move index expression and predicate mapping into ExpressionMappingPass.
                        var key = IndexKeys.Find(indexes, indexNode);
                        if (indexes.TryGetValue(key, out var index))
                        {
                            indexes[key] = (IndexDefinition)AttachDependencies(index, info);
                        }

                        break;
                    }
                    case ConstraintNode constraintNode:
                    {
                        // TODO: Move CHECK constraint expression mapping into ExpressionMappingPass.
                        var key = ResolveConstraintDefinitionKey(constraintNode);
                        if (constraints.TryGetValue(key, out var constraint))
                        {
                            constraints[key] = (ConstraintDefinition)AttachDependencies(constraint, info);
                        }

                        break;
                    }
                }
            }
        }

        return model with
        {
            Tables = tables,
            Views = views,
            Functions = functions,
            Procedures = procedures,
            Indexes = indexes,
            Constraints = constraints,
        };
    }

    private ISemanticNode AttachDependencies(ISemanticNode node, DependencyInfo info)
    {
        var referencedTables = Merge(node.ReferencedTables, info.ReferencedTables.Select(name => ResolveTableKey(node.SchemaName, name)));
        var referencedColumns = Merge(node.ReferencedColumns, info.ReferencedColumns.Select(name => ResolveColumnKey(node.SchemaName, name)));
        var referencedTypes = Merge(node.ReferencedTypes, info.ReferencedTypes.Select(name => ResolveTypeKey(node.SchemaName, name)));
        var referencedFunctions = Merge(node.ReferencedFunctions, info.ReferencedFunctions.Select(name => ResolveFunctionKey(node.SchemaName, name)));

        return node switch
        {
            TableDefinition table => table with
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
            ProcedureDefinition procedure => procedure with
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

    private string ResolveTableKey(string? schemaName, string? name)
        => ScopeReference(schemaName, name);

    private string ResolveFunctionKey(string? schemaName, string? name)
        => ScopeReference(schemaName, name);

    private string ResolveTypeKey(string? schemaName, string? name)
        => ScopeReference(schemaName, name);

    private string ResolveColumnKey(string? schemaName, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var reference = name.Trim();

        return ScopeReference(schemaName, reference);
    }

    private static IReadOnlyList<string> Merge(IEnumerable<string> current, IEnumerable<string> additional)
        => current
            .Concat(additional)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static DependencyInfo CollectDependencyInfo(SqlNode node)
    {
        var visitor = new SqlDependencyVisitor();
        visitor.Visit(node);
        return visitor.Info;
    }

    private static string ScopeReference(string? schemaName, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        var trimmedReference = reference.Trim();

        if (string.IsNullOrWhiteSpace(schemaName) || trimmedReference.Contains('.', StringComparison.Ordinal))
        {
            return trimmedReference;
        }

        return DatabaseModel.CreateKey(schemaName, trimmedReference);
    }

    private static string ResolveTableDefinitionKey(TableNode node)
        => DatabaseModel.CreateKey(node.SchemaName, node.Name ?? "unknown_table");

    private static string ResolveViewDefinitionKey(ViewNode node)
        => DatabaseModel.CreateKey(node.SchemaName, node.Name ?? "unknown_view");

    private static string ResolveFunctionDefinitionKey(FunctionNode node)
        => DatabaseModel.CreateRoutineKey(node.SchemaName, node.Name ?? "unknown_function", node.Signature?.RenderArgumentTypeSignature());

    private static string ResolveConstraintDefinitionKey(ConstraintNode node)
        => DatabaseModel.CreateConstraintKey(node.SchemaName, node.TableName, node.Name);
}
