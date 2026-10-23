using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Binds what a view's query and a SQL-language routine body read, from pg_query's tree for them (<see cref="QueryAstReader"/>), onto
/// the view or routine: the tables, views, and columns it reads, the functions it calls, and the types it casts to (G-90). It replaced
/// <c>DeepCteAndSubqueryPass</c>, which found the same things with regular expressions over the SQL text.
/// <para>
/// A relation resolves to a project table, else a project view (<see cref="ViewDefinition.ReferencedViews"/>, so a view over a view is
/// created after it, G-91), else it is kept as written in <c>ReferencedTables</c> for <see cref="SemanticBinder"/> to report as missing.
/// A column is bound only when its qualifier names a project table; a column of a view, a CTE, or a subquery is not checked. Names
/// resolve as PostgreSQL resolves them: a qualified name in its schema, an unqualified one along the search path (G-93).
/// This pass reports nothing itself: every missing reference it keeps is reported once, by <see cref="SemanticBinder"/>.
/// </para>
/// </summary>
internal sealed class QueryBodyBindingPass
{
    public DatabaseModel Execute(DatabaseModel model, IEnumerable<SqlSyntaxTree> syntaxTrees)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(syntaxTrees);

        var views = new Dictionary<string, ViewDefinition>(model.Views, PgProj.Parser.SqlIdentifiers.NameComparer);
        var functions = new Dictionary<string, FunctionDefinition>(model.Functions, PgProj.Parser.SqlIdentifiers.NameComparer);
        var procedures = new Dictionary<string, ProcedureDefinition>(model.Procedures, PgProj.Parser.SqlIdentifiers.NameComparer);
        var rules = new Dictionary<string, RuleDefinition>(model.Rules, PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var syntaxTree in syntaxTrees)
        {
            foreach (var node in syntaxTree.Nodes)
            {
                switch (node)
                {
                    case ViewNode { QueryAst: { } queryAst } viewNode
                        when views.TryGetValue(DatabaseModel.CreateKey(viewNode.SchemaName, viewNode.Name ?? "unknown_view"), out var view):
                    {
                        var bound = Bind(model, view.SchemaName, QueryAstReader.Collect(queryAst));
                        views[view.Key] = view with
                        {
                            ReferencedTables = Merge(view.ReferencedTables, bound.Tables),
                            ReferencedViews = Merge(view.ReferencedViews, bound.Views.Where(key => !string.Equals(key, view.Key, StringComparison.Ordinal))),
                            ReferencedColumns = Merge(view.ReferencedColumns, bound.Columns),
                            ReferencedTypes = Merge(view.ReferencedTypes, bound.Types),
                            ReferencedFunctions = Merge(view.ReferencedFunctions, bound.Functions),
                            ReferencedOperators = OperatorReferences.Merge(view.ReferencedOperators, bound.Operators),
                        };
                        break;
                    }

                    case FunctionNode { BodyAst: { } bodyAst } functionNode
                        when functions.TryGetValue(
                            DatabaseModel.CreateRoutineKey(functionNode.SchemaName, functionNode.Name ?? "unknown_function", functionNode.Signature?.RenderArgumentTypeSignature()),
                            out var function):
                    {
                        var bound = Bind(model, function.SchemaName, QueryAstReader.Collect(bodyAst));
                        functions[function.Key] = function with
                        {
                            ReferencedTables = Merge(function.ReferencedTables, bound.Tables),
                            ReferencedViews = Merge(function.ReferencedViews, bound.Views),
                            ReferencedColumns = Merge(function.ReferencedColumns, bound.Columns),
                            ReferencedTypes = Merge(function.ReferencedTypes, bound.Types),
                            ReferencedFunctions = Merge(function.ReferencedFunctions, bound.Functions),
                            ReferencedOperators = OperatorReferences.Merge(function.ReferencedOperators, bound.Operators),
                        };
                        break;
                    }

                    case RuleNode { QueryAst: { } ruleAst } ruleNode
                        when rules.TryGetValue(RuleKeys.Create(ruleNode.SchemaName, ruleNode.TableName, ruleNode.Name ?? "unknown_rule"), out var rule):
                    {
                        // A rule is created after the relation it is on (a table or a view) and after everything its condition and actions use.
                        var bound = Bind(model, rule.SchemaName, QueryAstReader.Collect(ruleAst));
                        var target = new QueryAstReader.Relation(rule.SchemaName, rule.TableName ?? string.Empty);
                        var targetTables = new List<string>();
                        var targetViews = new List<string>();
                        if (Resolve(model.Tables.Values, target, rule.SchemaName, model.SearchPathFor(rule.SchemaName)) is { } targetTable)
                        {
                            targetTables.Add(targetTable.Key);
                        }
                        else if (Resolve(model.Views.Values, target, rule.SchemaName, model.SearchPathFor(rule.SchemaName)) is { } targetView)
                        {
                            targetViews.Add(targetView.Key);
                        }
                        else
                        {
                            targetTables.Add(Written(target));
                        }

                        rules[rule.Key] = rule with
                        {
                            ReferencedTables = Merge(rule.ReferencedTables, bound.Tables.Concat(targetTables)),
                            ReferencedViews = Merge(rule.ReferencedViews, bound.Views.Concat(targetViews)),
                            ReferencedColumns = Merge(rule.ReferencedColumns, bound.Columns),
                            ReferencedTypes = Merge(rule.ReferencedTypes, bound.Types),
                            ReferencedFunctions = Merge(rule.ReferencedFunctions, bound.Functions),
                            ReferencedOperators = OperatorReferences.Merge(rule.ReferencedOperators, bound.Operators),
                        };
                        break;
                    }

                    case ProcedureNode { BodyAst: { } bodyAst } procedureNode
                        when procedures.TryGetValue(
                            DatabaseModel.CreateRoutineKey(procedureNode.SchemaName, procedureNode.Name ?? "unknown_procedure", procedureNode.Signature?.RenderArgumentTypeSignature()),
                            out var procedure):
                    {
                        var bound = Bind(model, procedure.SchemaName, QueryAstReader.Collect(bodyAst));
                        procedures[procedure.Key] = procedure with
                        {
                            ReferencedTables = Merge(procedure.ReferencedTables, bound.Tables),
                            ReferencedViews = Merge(procedure.ReferencedViews, bound.Views),
                            ReferencedColumns = Merge(procedure.ReferencedColumns, bound.Columns),
                            ReferencedTypes = Merge(procedure.ReferencedTypes, bound.Types),
                            ReferencedFunctions = Merge(procedure.ReferencedFunctions, bound.Functions),
                            ReferencedOperators = OperatorReferences.Merge(procedure.ReferencedOperators, bound.Operators),
                        };
                        break;
                    }
                }
            }
        }

        return model with
        {
            Views = views,
            Functions = functions,
            Procedures = procedures,
            Rules = rules,
            Aggregates = model.Aggregates.ToDictionary(static pair => pair.Key, pair => pair.Value with { ReferencedOperators = OperatorReferences.Resolve(model, pair.Value.Options.TryGetValue("sortop", out var sortOperator) ? [sortOperator] : [], pair.Value.SchemaName) }, PgProj.Parser.SqlIdentifiers.NameComparer),
        };
    }

    private sealed record Bound(
        IReadOnlyList<string> Tables,
        IReadOnlyList<string> Views,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> Functions,
        IReadOnlyList<string> Types,
        IReadOnlyList<string> Operators);

    private static Bound Bind(DatabaseModel model, string? ownerSchema, QueryAstReader.References references)
    {
        var tables = new List<string>();
        var views = new List<string>();
        foreach (var relation in references.Relations)
        {
            if (IsBuiltIn(relation))
            {
                continue;
            }

            if (Resolve(model.Tables.Values, relation, ownerSchema, model.SearchPathFor(ownerSchema)) is { } table)
            {
                tables.Add(table.Key);
            }
            else if (Resolve(model.Views.Values, relation, ownerSchema, model.SearchPathFor(ownerSchema)) is { } view)
            {
                views.Add(view.Key);
            }
            else
            {
                tables.Add(Written(relation));
            }
        }

        var columns = new List<string>();
        foreach (var column in references.Columns)
        {
            if (IsBuiltIn(column.Relation) || Resolve(model.Tables.Values, column.Relation, ownerSchema, model.SearchPathFor(ownerSchema)) is not { } table)
            {
                // A view's, a CTE's, or a subquery's column; or a relation that is itself missing, which is reported as such.
                continue;
            }

            var match = table.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, column.Name, StringComparison.Ordinal));
            columns.Add(match?.Key ?? $"{column.Qualifier}.{column.Name}");
        }

        return new Bound(tables, views, columns, references.Functions, references.Types, OperatorReferences.Resolve(model, references.Operators, ownerSchema));
    }

    internal static TNode? Resolve<TNode>(IEnumerable<TNode> candidates, QueryAstReader.Relation relation, string? ownerSchema, IReadOnlyList<string> searchPath)
        where TNode : class, ISemanticNode
    {
        var list = candidates as IReadOnlyCollection<TNode> ?? candidates.ToArray();
        if (relation.SchemaName is not null)
        {
            return list.FirstOrDefault(node => Named(node, relation.SchemaName, relation.Name));
        }

        // The first schema of the search path that has it, as PostgreSQL finds it (G-93); then, for a reference SearchPathValidationPass
        // reports as off the path, the owner's schema and then any, so it still binds.
        return searchPath.Select(schema => list.FirstOrDefault(node => Named(node, schema, relation.Name))).FirstOrDefault(static node => node is not null)
            ?? list.FirstOrDefault(node => Named(node, ownerSchema, relation.Name))
            ?? list.FirstOrDefault(node => string.Equals(node.Name, relation.Name, StringComparison.Ordinal));
    }

    private static bool Named(ISemanticNode node, string? schemaName, string name)
        => string.Equals(node.SchemaName, schemaName, StringComparison.Ordinal)
            && string.Equals(node.Name, name, StringComparison.Ordinal);

    internal static bool IsBuiltIn(QueryAstReader.Relation relation)
        => PostgreSqlSystemReferences.IsBuiltInSchema(relation.SchemaName ?? string.Empty)
            || PostgreSqlSystemReferences.IsBuiltInRelation(Written(relation));

    internal static string Written(QueryAstReader.Relation relation)
        => relation.SchemaName is null ? relation.Name : $"{relation.SchemaName}.{relation.Name}";

    private static IReadOnlyList<string> Merge(IEnumerable<string> current, IEnumerable<string> additional)
        => current
            .Concat(additional)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
