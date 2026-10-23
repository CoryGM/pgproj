using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Reports an unqualified reference that PostgreSQL would not find (G-93). PostgreSQL resolves a name written without a schema through
/// <c>search_path</c> only: <c>pg_catalog</c>, then each schema of the path in order. It does this when it creates a view, a
/// <c>LANGUAGE sql</c> routine, a column (its type and <c>DEFAULT</c>), a <c>CHECK</c>, an index, a domain, a type, a trigger, or a policy,
/// and rejects the statement when the name isn't there. <c>build</c> used to accept any project object of that name in any schema, so it
/// passed SQL that failed at <c>publish</c> (<c>42883</c>, <c>42P01</c>, <c>42704</c>). Now a reference that names a project object only
/// outside the path is an error, which says where the object is and how to reach it. A name that is not a project object at all is left to
/// the existing "references missing" diagnostics (it may be an extension's), and a PL/pgSQL body is not read: PostgreSQL does not resolve
/// it until it runs.
/// </summary>
internal sealed class SearchPathValidationPass
{
    private static readonly SymbolTable BuiltIns = new(DatabaseModel.Empty);

    public DatabaseModel Execute(DatabaseModel model, IEnumerable<SqlSyntaxTree> syntaxTrees)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(syntaxTrees);

        var checker = new Checker(model);
        foreach (var tree in syntaxTrees)
        {
            foreach (var node in tree.Nodes)
            {
                checker.Check(node, tree.FilePath);
            }
        }

        return checker.Diagnostics.Count == 0
            ? model
            : model with { Diagnostics = model.Diagnostics.Concat(checker.Diagnostics).ToList(), SearchPathFindings = checker.Findings };
    }

    private enum Kind
    {
        Relation,
        Function,
        Type,
    }

    private sealed class Checker(DatabaseModel model)
    {
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

        private IReadOnlyList<string> _path = model.SearchPath;

        // The file of the node being checked, and the place a finding about it names (G-309e): the node's statement, or a column's own name.
        private string? _file;
        private SemanticLocation? _place;

        public List<SemanticDiagnostic> Diagnostics { get; } = [];

        public List<IReadOnlyList<string>> Findings { get; } = [];

        public void Check(SqlNode node, string? filePath)
        {
            _file = filePath;
            _place = SemanticLocation.Of(filePath, node.Range);
            // A tenant template's object resolves along its own schema's path (G-290); everything else along the project's.
            _path = model.SearchPathFor(node.SchemaName);
            switch (node)
            {
                case ViewNode view:
                    CheckQuery($"View '{Key(view)}'", view.QueryAst);
                    break;
                case FunctionNode function:
                    var functionOwner = $"Function '{Key(function)}'";
                    CheckQuery(functionOwner, function.BodyAst);
                    CheckSignature(functionOwner, function.Signature);
                    break;
                case ProcedureNode procedure:
                    var procedureOwner = $"Procedure '{Key(procedure)}'";
                    CheckQuery(procedureOwner, procedure.BodyAst);
                    CheckSignature(procedureOwner, procedure.Signature);
                    break;
                case TableNode table:
                    foreach (var column in table.Columns)
                    {
                        CheckColumn(column);
                    }

                    break;
                case AlterTableAddColumnNode addColumn:
                    CheckColumn(addColumn.Column);
                    break;
                case ConstraintNode constraint:
                    CheckExpression($"Constraint '{Key(constraint)}'", constraint.CheckExpressionAst);
                    break;
                case IndexNode index:
                    var indexOwner = $"Index '{Key(index)}'";
                    foreach (var element in index.KeyElements)
                    {
                        CheckExpression(indexOwner, element.ExpressionAst);
                    }

                    CheckExpression(indexOwner, index.PredicateAst);
                    break;
                case DomainNode domain:
                    var domainOwner = $"Domain '{Key(domain)}'";
                    CheckName(domainOwner, domain.BaseType, Kind.Type);
                    foreach (var check in domain.CheckConstraints)
                    {
                        CheckExpression(domainOwner, check.ExpressionAst);
                    }

                    break;
                case TypeNode type:
                    var typeOwner = $"Type '{Key(type)}'";
                    foreach (var field in type.CompositeFieldTypes)
                    {
                        CheckName(typeOwner, field.BaseType, Kind.Type);
                    }

                    CheckName(typeOwner, type.RangeSubtype?.BaseType, Kind.Type);
                    break;
                case TriggerNode trigger:
                    var triggerOwner = $"Trigger '{trigger.Name}' on '{DatabaseModel.CreateKey(trigger.SchemaName, trigger.TableName ?? "?")}'";
                    CheckName(triggerOwner, trigger.FunctionName, Kind.Function);
                    CheckExpression(triggerOwner, trigger.WhenConditionAst);
                    break;
                case CastNode cast:
                    var castOwner = $"Cast '{cast.Name}'";
                    CheckName(castOwner, cast.SourceType, Kind.Type);
                    CheckName(castOwner, cast.TargetType, Kind.Type);
                    CheckName(castOwner, cast.FunctionName, Kind.Function);
                    foreach (var argument in cast.FunctionArguments)
                    {
                        CheckName(castOwner, argument, Kind.Type);
                    }

                    break;
                case AggregateNode aggregate:
                    var aggregateOwner = $"Aggregate '{aggregate.Name}'";
                    foreach (var argument in aggregate.Arguments)
                    {
                        CheckName(aggregateOwner, argument.StartsWith("VARIADIC ", StringComparison.Ordinal) ? argument["VARIADIC ".Length..] : argument, Kind.Type);
                    }

                    foreach (var (option, value) in aggregate.Options)
                    {
                        if (AggregateOptions.TypeOptions.Contains(option))
                        {
                            CheckName(aggregateOwner, value, Kind.Type);
                        }
                        else if (AggregateOptions.FunctionOptions.Contains(option))
                        {
                            CheckName(aggregateOwner, value, Kind.Function);
                        }
                    }

                    break;
                case OperatorNode @operator:
                    var operatorOwner = $"Operator '{@operator.Name}'";
                    CheckName(operatorOwner, @operator.LeftType, Kind.Type);
                    CheckName(operatorOwner, @operator.RightType, Kind.Type);
                    CheckName(operatorOwner, @operator.FunctionName, Kind.Function);
                    CheckName(operatorOwner, @operator.RestrictFunction, Kind.Function);
                    CheckName(operatorOwner, @operator.JoinFunction, Kind.Function);
                    break;
                case EventTriggerNode eventTrigger:
                    CheckName($"Event trigger '{eventTrigger.Name}'", eventTrigger.FunctionName, Kind.Function);
                    break;
                case PolicyNode policy:
                    var policyOwner = $"Policy '{policy.Name}' on '{DatabaseModel.CreateKey(policy.SchemaName, policy.TableName ?? "?")}'";
                    CheckExpression(policyOwner, policy.UsingExpressionAst);
                    CheckExpression(policyOwner, policy.WithCheckExpressionAst);
                    break;
            }
        }

        private void CheckColumn(ColumnNode column)
        {
            var owner = $"Column '{DatabaseModel.CreateKey(column.SchemaName, $"{column.TableName}.{column.Name}")}'";
            var statement = _place;
            _place = SemanticLocation.Of(_file, column.TypeRange) ?? SemanticLocation.Of(_file, column.Range) ?? statement;
            CheckName(owner, column.DataType, Kind.Type);
            _place = SemanticLocation.Of(_file, column.Range) ?? statement;
            CheckExpression(owner, column.DefaultExpressionAst);
            CheckExpression(owner, column.GeneratedExpressionAst);
            _place = statement;
        }

        private void CheckSignature(string owner, RoutineSignature? signature)
        {
            if (signature is null)
            {
                return;
            }

            foreach (var parameter in signature.Parameters)
            {
                CheckName(owner, parameter.DataType, Kind.Type);
            }

            CheckName(owner, signature.ReturnType, Kind.Type);
        }

        private void CheckQuery(string owner, string? queryAst)
        {
            if (queryAst is null)
            {
                return;
            }

            var references = QueryAstReader.Collect(queryAst);
            foreach (var relation in references.Relations.Where(static relation => relation.SchemaName is null))
            {
                CheckName(owner, relation.Name, Kind.Relation);
            }

            CheckNames(owner, references.Functions, references.Types);
        }

        private void CheckExpression(string owner, string? expressionAst)
        {
            if (expressionAst is not null)
            {
                var references = ExpressionAstReader.Collect(expressionAst);
                CheckNames(owner, references.Functions, references.Types);
            }
        }

        private void CheckNames(string owner, IEnumerable<string> functions, IEnumerable<string> types)
        {
            foreach (var function in functions)
            {
                CheckName(owner, function, Kind.Function);
            }

            foreach (var type in types)
            {
                CheckName(owner, type, Kind.Type);
            }
        }

        private void CheckName(string owner, string? written, Kind kind)
        {
            if (string.IsNullOrWhiteSpace(written))
            {
                return;
            }

            var name = written.Trim();
            while (kind == Kind.Type && name.EndsWith("[]", StringComparison.Ordinal))
            {
                name = name[..^2].TrimEnd();
            }

            if (name.Contains('.', StringComparison.Ordinal) || IsBuiltIn(name, kind))
            {
                return;
            }

            var schemas = SchemasDeclaring(name, kind);
            if (schemas.Count == 0 || _path.Any(schemas.Contains))
            {
                // Not a project object (an extension's, or genuinely missing: reported elsewhere), or found on the path.
                return;
            }

            if (!_reported.Add($"{owner}|{kind}|{name}"))
            {
                return;
            }

            var path = string.Join(", ", _path);
            var found = string.Join(", ", schemas.Order(StringComparer.Ordinal));
            var example = schemas.Order(StringComparer.Ordinal).First();
            Findings.Add([.. schemas.Order(StringComparer.Ordinal)]);
            Diagnostics.Add(SemanticDiagnostics.Error(
                $"{owner} references {Describe(kind)} '{name}' without a schema, and it is not on the search path ({path}): it is in schema '{found}'. "
                + $"PostgreSQL will not find it. Write it as {example}.{name}, or add {example} to \"searchPath\" in pgproj.json.",
                _place,
                nameof(SearchPathValidationPass)));
        }

        private HashSet<string> SchemasDeclaring(string name, Kind kind)
        {
            IEnumerable<ISemanticNode> candidates = kind switch
            {
                Kind.Relation => model.Tables.Values.Cast<ISemanticNode>().Concat(model.Views.Values),
                Kind.Function => model.Functions.Values.Cast<ISemanticNode>().Concat(model.Procedures.Values).Concat(model.Aggregates.Values),
                _ => model.Types.Values.Cast<ISemanticNode>().Concat(model.Tables.Values).Concat(model.Views.Values),
            };

            return candidates
                .Where(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal) && candidate.SchemaName is not null)
                .Select(static candidate => candidate.SchemaName!)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static bool IsBuiltIn(string name, Kind kind)
            => kind switch
            {
                Kind.Function => BuiltInFunctions.IsBuiltIn(name),
                Kind.Relation => PostgreSqlSystemReferences.IsBuiltInRelation(name),
                _ => BuiltIns.IsKnownBuiltInType(name),
            };

        private static string Describe(Kind kind)
            => kind switch
            {
                Kind.Relation => "the relation",
                Kind.Function => "the function",
                _ => "the type",
            };

        private static string Key(SqlNode node) => DatabaseModel.CreateKey(node.SchemaName, node.Name ?? "?");
    }
}
