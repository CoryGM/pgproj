using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Checks a project with tenants (G-290) before it is built: the template's SQL, read in the stand-in schema (the tenant prefix), may create and change
/// only the tenant's own objects, and the shared project may not reach into a tenant. The template is applied to each tenant schema with that schema as
/// the default and first on the search path, and deployed as written, so a schema written in the template would not follow the tenant:
/// <list type="bullet">
/// <item>An object the template names with a schema other than a shared one (<c>tenant.orders</c>, <c>tenant_acme.orders</c>) is an error: the template
/// writes its own objects without a schema.</item>
/// <item>An object the template creates or changes in a shared schema is an error, and so is anything that belongs to the whole database rather than a
/// schema (an extension, an event trigger, a cast, a publication, a role membership, default privileges without <c>IN SCHEMA</c>).</item>
/// <item>A statement about a schema itself (<c>GRANT ... ON SCHEMA</c>, <c>ALTER DEFAULT PRIVILEGES IN SCHEMA</c>, <c>ALL ... IN SCHEMA</c>, the schema's
/// owner or comment, <c>CREATE SCHEMA</c>) may name only the stand-in schema, which stands for each tenant's.</item>
/// <item>The shared project may not declare anything in, or refer to, the stand-in schema or a tenant schema (one named <c>prefix_...</c>).</item>
/// </list>
/// A template routine whose body PostgreSQL resolves when it runs (anything but a SQL-standard body) and that does not set its own <c>search_path</c> is a
/// warning: it finds names along the caller's path, which need not start with the tenant's schema. Only names the parse tree shows qualified are checked:
/// a name inside a PL/pgSQL or other string body is not read, as elsewhere.
/// </summary>
public static class TenantTemplateCheck
{
    public static IReadOnlyList<SemanticDiagnostic> Check(IReadOnlyList<SqlSyntaxTree> sharedTrees, IReadOnlyList<SqlSyntaxTree> templateTrees, string prefix)
    {
        ArgumentNullException.ThrowIfNull(sharedTrees);
        ArgumentNullException.ThrowIfNull(templateTrees);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var diagnostics = new List<SemanticDiagnostic>();
        foreach (var tree in templateTrees)
        {
            var checker = new Checker(prefix, isTemplate: true, tree.FilePath, diagnostics);
            foreach (var node in tree.Nodes)
            {
                checker.Check(node);
            }
        }

        foreach (var tree in sharedTrees)
        {
            var checker = new Checker(prefix, isTemplate: false, tree.FilePath, diagnostics);
            foreach (var node in tree.Nodes)
            {
                checker.Check(node);
            }
        }

        var unpinned = templateTrees
            .SelectMany(static tree => tree.Nodes.Select(node => (Node: node, tree.FilePath)))
            .Select(static entry => entry.Node switch
            {
                FunctionNode function when RunsWithCallersPath(function.Signature, function.IsSqlStandardBody) => (Name: $"function {function.Name}", entry.FilePath, entry.Node.Range),
                ProcedureNode procedure when RunsWithCallersPath(procedure.Signature, procedure.IsSqlStandardBody) => (Name: $"procedure {procedure.Name}", entry.FilePath, entry.Node.Range),
                _ => (Name: (string?)null, entry.FilePath, entry.Node.Range),
            })
            .Where(static entry => entry.Name is not null)
            .ToArray();
        if (unpinned.Length > 0)
        {
            var named = string.Join(", ", unpinned.Take(3).Select(static entry => entry.Name));
            var more = unpinned.Length > 3 ? $", and {unpinned.Length - 3} more" : string.Empty;
            diagnostics.Add(SemanticDiagnostics.Warning(
                $"Tenant template: {unpinned.Length} routine(s) ({named}{more}) do not set search_path. PostgreSQL resolves the names in such a body when it runs, along "
                + "the caller's search_path, which may not start with the tenant's schema. Add SET search_path FROM CURRENT to each: pgproj creates a tenant's "
                + "routines with that tenant's schema first on the path, so FROM CURRENT keeps it. Or make sure every session sets search_path to the tenant's schema.",
                // The first such routine (G-309e).
                SemanticLocation.Of(unpinned[0].FilePath, unpinned[0].Range) ?? new SemanticLocation(unpinned[0].FilePath, null),
                nameof(TenantTemplateCheck)));
        }

        return diagnostics;
    }

    /// <summary>True for a routine whose body is resolved when it runs (not a SQL-standard body, which is bound when it is created) and that sets no <c>search_path</c>.</summary>
    private static bool RunsWithCallersPath(RoutineSignature? signature, bool isSqlStandardBody)
        => !isSqlStandardBody
            && signature is not null
            && !signature.Settings.Any(static setting => string.Equals(setting.Name, "search_path", StringComparison.OrdinalIgnoreCase));

    private sealed class Checker(string prefix, bool isTemplate, string? filePath, List<SemanticDiagnostic> diagnostics)
    {
        private readonly string _tenantPrefix = prefix + "_";

        /// <summary>The objects already reported for their own schema, by schema and table (or name), so a table's columns and constraints are not reported again.</summary>
        private readonly HashSet<string> _reportedObjects = new(StringComparer.Ordinal);

        public void Check(SqlNode node)
        {
            // What this node breaks is shown at its statement (G-309e).
            _place = SemanticLocation.Of(filePath, node.Range);
            switch (node)
            {
                case ExtensionNode or EventTriggerNode or AlterEventTriggerNode or CastNode or PublicationNode or AlterPublicationNode:
                    if (isTemplate)
                    {
                        Error($"{Describe(node)} belongs to the whole database, not to a schema, so it cannot be in the tenant template, which is applied once per tenant. Put it in the shared project.");
                    }

                    break;
                case SchemaNode schema:
                    CheckSchemaName($"CREATE SCHEMA {schema.Name}", schema.Name);
                    break;
                case GrantNode grant:
                    CheckGrant(grant);
                    break;
                case OwnerNode { ObjectKind: "Schema" } owner:
                    CheckSchemaName($"The owner of schema {owner.ObjectName}", owner.ObjectName);
                    break;
                case ObjectCommentNode { ObjectKind: "Schema" } comment:
                    CheckSchemaName($"The comment on schema {comment.SchemaName}", comment.SchemaName);
                    break;
                default:
                    CheckOwnSchema(node);
                    CheckReferences(node);
                    break;
            }
        }

        private void CheckGrant(GrantNode grant)
        {
            switch (grant.Form)
            {
                case GrantForm.RoleMembership:
                    if (isTemplate)
                    {
                        Error("A role membership (GRANT role TO role) belongs to the whole database, so it cannot be in the tenant template. Put it in the shared project.");
                    }

                    break;
                case GrantForm.DefaultPrivileges when grant.Schemas.Count == 0:
                    if (isTemplate)
                    {
                        Error("ALTER DEFAULT PRIVILEGES without IN SCHEMA applies to every schema, so it cannot be in the tenant template. Write IN SCHEMA " + prefix
                            + " to set them in each tenant's schema, or put it in the shared project.");
                    }

                    break;
                case GrantForm.DefaultPrivileges:
                    foreach (var schema in grant.Schemas)
                    {
                        CheckSchemaName($"ALTER DEFAULT PRIVILEGES IN SCHEMA {schema}", schema);
                    }

                    break;
                case GrantForm.AllInSchema:
                    // The schemas of ALL ... IN SCHEMA are the statement's targets.
                    foreach (var target in grant.Targets)
                    {
                        CheckSchemaName($"GRANT ... ON ALL ... IN SCHEMA {target.Name}", target.Name);
                    }

                    break;
                case GrantForm.Object when string.Equals(grant.ObjectKind, "schema", StringComparison.OrdinalIgnoreCase):
                    foreach (var target in grant.Targets)
                    {
                        CheckSchemaName($"A grant on schema {target.Name}", target.Name);
                    }

                    break;
                default:
                    foreach (var target in grant.Targets)
                    {
                        CheckWrittenSchema($"A grant on {target.Name}", target.IsSchemaDefaulted ? null : target.SchemaName, isOwnObject: true);
                    }

                    break;
            }
        }

        /// <summary>A statement about a schema itself: in the template it may name only the stand-in; in the shared project, not the stand-in or a tenant's.</summary>
        private void CheckSchemaName(string what, string? schema)
        {
            if (string.IsNullOrEmpty(schema))
            {
                return;
            }

            if (isTemplate && !IsStandIn(schema))
            {
                Error($"{what}: the tenant template can name only its own schema, written as {prefix} (the stand-in for each tenant's schema), not '{schema}'. Put a statement about another schema in the shared project.");
            }
            else if (!isTemplate && (IsStandIn(schema) || IsTenantSchema(schema)))
            {
                Error($"{what}: '{schema}' is {(IsStandIn(schema) ? "the tenant template's stand-in schema" : "a tenant's schema")}. A statement about a tenant's schema goes in the tenant template, naming it {prefix}.");
            }
        }

        /// <summary>The schema of the object the statement creates or changes, when it was written.</summary>
        private void CheckOwnSchema(SqlNode node)
        {
            var tableName = node switch
            {
                ColumnNode column => column.TableName,
                ConstraintNode constraint => constraint.TableName,
                IndexNode index => index.TableName,
                TriggerNode trigger => trigger.TableName,
                PolicyNode policy => policy.TableName,
                RuleNode rule => rule.TableName,
                _ => node.Name,
            };
            if (node.SchemaName is not null && !node.IsSchemaDefaulted && _reportedObjects.Add($"{node.SchemaName}|{tableName}"))
            {
                CheckWrittenSchema(Describe(node), node.SchemaName, isOwnObject: true);
            }
        }

        /// <summary>
        /// A schema written in the SQL. In the template: the stand-in or a tenant's schema is an error (write the tenant's objects without a schema), and an
        /// object of its own in a shared schema is an error (a reference to a shared object is fine). In the shared project: the stand-in or a tenant's schema.
        /// </summary>
        private void CheckWrittenSchema(string what, string? schema, bool isOwnObject)
        {
            if (string.IsNullOrEmpty(schema))
            {
                return;
            }

            if (isTemplate)
            {
                if (!isOwnObject && IsTenantSchema(schema))
                {
                    Error($"{what} refers to an object in '{schema}', which is one tenant's schema. The template is the same for every tenant, so it cannot refer to one tenant's objects.");
                }
                else if (!isOwnObject && IsStandIn(schema))
                {
                    Error($"{what} refers to an object written with the schema '{schema}', the stand-in. Refer to the tenant's own objects without a schema: the template is read once "
                        + "for each tenant, with that tenant's schema first on the search path, and deployed as written, so a schema written here would not follow the tenant.");
                }
                else if (IsStandIn(schema) || IsTenantSchema(schema))
                {
                    Error($"{what} is written with the schema '{schema}'. The tenant template writes its own objects without a schema: it is read once for each tenant, "
                        + "with that tenant's schema as the default, and deployed as written, so a schema written here would not follow the tenant.");
                }
                else if (isOwnObject)
                {
                    Error($"{what} is in the shared schema '{schema}'. The tenant template can create and change only the tenant's own objects, since it is applied once "
                        + "per tenant; put objects of a shared schema in the shared project. (The template may refer to a shared object, as in REFERENCES "
                        + $"{schema}.name.)");
                }
            }
            else if (IsStandIn(schema) || IsTenantSchema(schema))
            {
                Error($"{what} {(isOwnObject ? "is in" : "refers to an object in")} the schema '{schema}', which is {(IsStandIn(schema) ? "the tenant template's stand-in schema" : "a tenant's schema")}. "
                    + "The shared project cannot declare or refer to a tenant's objects: they are the template's, and differ by tenant.");
            }
        }

        private void CheckReferences(SqlNode node)
        {
            var what = Describe(node);
            switch (node)
            {
                case TableNode table:
                    CheckReference(what, table.PartitionOfTableName is null || table.IsPartitionOfSchemaDefaulted ? null : table.PartitionOfSchemaName);
                    CheckReference(what, table.OfTypeName is null || table.IsOfTypeSchemaDefaulted ? null : table.OfTypeSchemaName);
                    foreach (var parent in table.InheritsFrom.Where(static parent => !parent.IsSchemaDefaulted))
                    {
                        CheckReference(what, parent.SchemaName);
                    }

                    foreach (var like in table.LikeClauses.Where(static like => !like.IsSchemaDefaulted))
                    {
                        CheckReference(what, like.SchemaName);
                    }

                    foreach (var column in table.Columns)
                    {
                        CheckColumn(what, column);
                    }

                    break;
                case AlterTableAddColumnNode addColumn:
                    CheckColumn(what, addColumn.Column);
                    break;
                case AlterTableAttachPartitionNode attach:
                    CheckReference(what, attach.IsChildSchemaDefaulted ? null : attach.ChildSchemaName);
                    break;
                case ConstraintNode constraint:
                    CheckReference(what, constraint.ForeignKeyReferencedTableName is null || constraint.IsForeignKeyReferencedSchemaDefaulted ? null : constraint.ForeignKeyReferencedSchemaName);
                    CheckExpression(what, constraint.CheckExpressionAst);
                    break;
                case IndexNode index:
                    foreach (var element in index.KeyElements)
                    {
                        CheckExpression(what, element.ExpressionAst);
                    }

                    CheckExpression(what, index.PredicateAst);
                    break;
                case ViewNode view:
                    CheckQuery(what, view.QueryAst);
                    break;
                case FunctionNode function:
                    CheckQuery(what, function.BodyAst);
                    CheckSignature(what, function.Signature);
                    break;
                case ProcedureNode procedure:
                    CheckQuery(what, procedure.BodyAst);
                    CheckSignature(what, procedure.Signature);
                    break;
                case DomainNode domain:
                    CheckName(what, domain.BaseType);
                    foreach (var check in domain.CheckConstraints)
                    {
                        CheckExpression(what, check.ExpressionAst);
                    }

                    break;
                case TypeNode type:
                    foreach (var field in type.CompositeFieldTypes)
                    {
                        CheckName(what, field.BaseType);
                    }

                    CheckName(what, type.RangeSubtype?.BaseType);
                    break;
                case TriggerNode trigger:
                    CheckName(what, trigger.FunctionName);
                    CheckExpression(what, trigger.WhenConditionAst);
                    break;
                case PolicyNode policy:
                    CheckExpression(what, policy.UsingExpressionAst);
                    CheckExpression(what, policy.WithCheckExpressionAst);
                    break;
                case AggregateNode aggregate:
                    foreach (var argument in aggregate.Arguments)
                    {
                        CheckName(what, argument.StartsWith("VARIADIC ", StringComparison.Ordinal) ? argument["VARIADIC ".Length..] : argument);
                    }

                    foreach (var (option, value) in aggregate.Options)
                    {
                        if (AggregateOptions.TypeOptions.Contains(option) || AggregateOptions.FunctionOptions.Contains(option))
                        {
                            CheckName(what, value);
                        }
                    }

                    break;
                case OperatorNode @operator:
                    CheckName(what, @operator.LeftType);
                    CheckName(what, @operator.RightType);
                    CheckName(what, @operator.FunctionName);
                    CheckName(what, @operator.RestrictFunction);
                    CheckName(what, @operator.JoinFunction);
                    break;
                case StatisticsNode statistics:
                    CheckReference(what, statistics.TableSchemaName is { } tableSchema && !string.Equals(tableSchema, statistics.SchemaName, StringComparison.Ordinal) ? tableSchema : null);
                    break;
            }
        }

        private void CheckColumn(string what, ColumnNode column)
        {
            CheckName(what, column.DataType);
            CheckExpression(what, column.DefaultExpressionAst);
            CheckExpression(what, column.GeneratedExpressionAst);
        }

        private void CheckSignature(string what, RoutineSignature? signature)
        {
            if (signature is null)
            {
                return;
            }

            foreach (var parameter in signature.Parameters)
            {
                CheckName(what, parameter.DataType);
            }

            CheckName(what, signature.ReturnType);
        }

        private void CheckQuery(string what, string? queryAst)
        {
            if (queryAst is null)
            {
                return;
            }

            var references = QueryAstReader.Collect(queryAst);
            foreach (var relation in references.Relations)
            {
                CheckReference(what, relation.SchemaName);
            }

            CheckNames(what, references.Functions, references.Types, references.Operators);
        }

        private void CheckExpression(string what, string? expressionAst)
        {
            if (expressionAst is not null)
            {
                var references = ExpressionAstReader.Collect(expressionAst);
                CheckNames(what, references.Functions, references.Types, references.Operators);
            }
        }

        private void CheckNames(string what, IEnumerable<string> functions, IEnumerable<string> types, IEnumerable<string> operators)
        {
            foreach (var name in functions.Concat(types).Concat(operators))
            {
                CheckName(what, name);
            }
        }

        /// <summary>A name as written (<c>schema.name</c>, a type with <c>[]</c>); only its schema, when written, matters.</summary>
        private void CheckName(string what, string? written)
        {
            if (string.IsNullOrWhiteSpace(written))
            {
                return;
            }

            var name = written.Trim();
            while (name.EndsWith("[]", StringComparison.Ordinal))
            {
                name = name[..^2].TrimEnd();
            }

            var parts = SqlIdentifiers.SplitQualifiedName(name);
            if (parts.Count > 1)
            {
                CheckReference(what, parts[^2]);
            }
        }

        /// <summary>A reference to another object, with its schema when written: a shared one is fine from the template, the stand-in or a tenant's never.</summary>
        private void CheckReference(string what, string? schema) => CheckWrittenSchema(what, schema, isOwnObject: false);

        private bool IsStandIn(string schema) => string.Equals(schema, prefix, StringComparison.Ordinal);

        private bool IsTenantSchema(string schema) => schema.StartsWith(_tenantPrefix, StringComparison.Ordinal);

        private SemanticLocation? _place;

        private void Error(string message)
            => diagnostics.Add(SemanticDiagnostics.Error(
                (isTemplate ? "Tenant template: " : string.Empty) + message,
                _place ?? new SemanticLocation(filePath, null),
                nameof(TenantTemplateCheck)));

        private static string Describe(SqlNode node)
        {
            var name = node.Name is null ? string.Empty : " " + DatabaseModel.CreateKey(node.SchemaName, node.Name);
            return node switch
            {
                ExtensionNode => $"The extension{name}",
                EventTriggerNode or AlterEventTriggerNode => $"The event trigger {node.Name}",
                CastNode => $"The cast {node.Name}",
                PublicationNode or AlterPublicationNode => $"The publication {node.Name}",
                _ => $"{node.Kind}{name}",
            };
        }
    }
}
