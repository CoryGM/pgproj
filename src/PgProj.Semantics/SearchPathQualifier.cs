using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Gives an unqualified reference the schema PostgreSQL finds it in through the project's <c>searchPath</c> (G-93), before anything is
/// declared or keyed. A declaration's own unqualified name is created in the first schema of the path (the parser gave it that), but a
/// name that refers to an existing object is looked up along the path: <c>REFERENCES region</c>, <c>CREATE INDEX ... ON region</c>,
/// <c>ALTER TABLE region</c>, a trigger's or policy's table, a comment's or grant's target, a partition's parent. So is a type name, and a
/// trigger's function. PostgreSQL resolves these once, when the object is created, and the
/// live database reads them back qualified with that schema unless it is <c>public</c> (the path the live read runs under). So with
/// <c>searchPath: ["shop", "public"]</c>, <c>f(m mood)</c> is <c>f(m shop.mood)</c>, and a routine's key, a column's type, and a trigger's
/// function match what the database reads back. A name found in <c>public</c> first, one not found on the path, and a built-in type are
/// left as written; with the default path (<c>public</c> alone) nothing changes. A name found only off the path is reported by
/// <see cref="SearchPathValidationPass"/>.
/// </summary>
internal static class SearchPathQualifier
{
    private static readonly SymbolTable BuiltIns = new(DatabaseModel.Empty);

    /// <summary>
    /// <paramref name="schemaSearchPaths"/> gives a schema whose objects resolve along a path of their own (G-290: a tenant template in its stand-in schema): a node is
    /// qualified along the path of the schema it is in, else along <paramref name="searchPath"/>.
    /// </summary>
    public static IReadOnlyList<SqlSyntaxTree> Apply(
        IReadOnlyList<SqlSyntaxTree> trees,
        IReadOnlyList<string> searchPath,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? schemaSearchPaths = null)
    {
        ArgumentNullException.ThrowIfNull(trees);
        ArgumentNullException.ThrowIfNull(searchPath);

        var paths = schemaSearchPaths ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (paths.Values.Prepend(searchPath).All(static path => path.All(static schema => string.Equals(schema, "public", StringComparison.Ordinal) || string.Equals(schema, "pg_catalog", StringComparison.Ordinal))))
        {
            return trees;
        }

        var qualifier = new Qualifier(trees, searchPath, paths);
        return trees.Select(tree => tree with { Nodes = tree.Nodes.Select(qualifier.Apply).ToArray() }).ToArray();
    }

    private sealed class Qualifier
    {
        private readonly IReadOnlyList<string> _projectSearchPath;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _schemaSearchPaths;
        private IReadOnlyList<string> _searchPath;
        private readonly Dictionary<string, HashSet<string>> _typeSchemas = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _functionSchemas = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _relationSchemas = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _sequenceSchemas = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _procedureSchemas = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _indexSchemas = new(StringComparer.Ordinal);

        public Qualifier(IReadOnlyList<SqlSyntaxTree> trees, IReadOnlyList<string> searchPath, IReadOnlyDictionary<string, IReadOnlyList<string>> schemaSearchPaths)
        {
            _projectSearchPath = searchPath;
            _schemaSearchPaths = schemaSearchPaths;
            _searchPath = searchPath;
            foreach (var node in trees.SelectMany(static tree => tree.Nodes))
            {
                switch (node)
                {
                    // A table's or view's row type is a type too.
                    case TableNode or ViewNode when node.Name is not null && node.SchemaName is not null:
                        Add(_typeSchemas, node.Name, node.SchemaName);
                        Add(_relationSchemas, node.Name, node.SchemaName);
                        break;
                    case TypeNode or DomainNode when node.Name is not null && node.SchemaName is not null:
                        Add(_typeSchemas, node.Name, node.SchemaName);
                        break;
                    case SequenceNode when node.Name is not null && node.SchemaName is not null:
                        Add(_sequenceSchemas, node.Name, node.SchemaName);
                        break;
                    case ProcedureNode when node.Name is not null && node.SchemaName is not null:
                        Add(_procedureSchemas, node.Name, node.SchemaName);
                        break;
                    case AggregateNode when node.Name is not null && node.SchemaName is not null:
                        Add(_functionSchemas, node.Name, node.SchemaName);
                        break;
                    case FunctionNode when node.Name is not null && node.SchemaName is not null:
                        Add(_functionSchemas, node.Name, node.SchemaName);
                        break;
                }
            }

            // An index is in its table's schema, so an unqualified index is where its table is found.
            foreach (var index in trees.SelectMany(static tree => tree.Nodes).OfType<IndexNode>().Where(static index => index.Name is not null))
            {
                _searchPath = PathFor(index.SchemaName);
                if (ResolveReference(index.SchemaName, index.IsSchemaDefaulted, index.TableName, _relationSchemas) is { } schema)
                {
                    Add(_indexSchemas, index.Name!, schema);
                }
            }
        }

        public SqlNode Apply(SqlNode node)
        {
            _searchPath = PathFor(node.SchemaName);
            return Qualify(node);
        }

        private IReadOnlyList<string> PathFor(string? schemaName)
            => schemaName is not null && _schemaSearchPaths.TryGetValue(schemaName, out var path) ? path : _projectSearchPath;

        private SqlNode Qualify(SqlNode node)
            => node switch
            {
                TableNode table => table with
                {
                    Columns = table.Columns.Select(QualifyColumn).ToArray(),
                    PartitionOfSchemaName = ResolveReference(table.PartitionOfSchemaName, table.IsPartitionOfSchemaDefaulted, table.PartitionOfTableName, _relationSchemas),
                    InheritsFrom = table.InheritsFrom
                        .Select(parent => parent with { SchemaName = ResolveReference(parent.SchemaName, parent.IsSchemaDefaulted, parent.Name, _relationSchemas) })
                        .ToArray(),
                    // A LIKE source is a table, or a composite type (G-221).
                    LikeClauses = table.LikeClauses
                        .Select(like => like with
                        {
                            SchemaName = ResolveReference(like.SchemaName, like.IsSchemaDefaulted, like.Name, _relationSchemas.ContainsKey(like.Name) ? _relationSchemas : _typeSchemas),
                        })
                        .ToArray(),
                },
                ColumnNode column => QualifyColumn(column) with { SchemaName = OnTable(column, column.TableName) },
                AlterTableAddColumnNode addColumn => addColumn with
                {
                    SchemaName = OnTable(addColumn, addColumn.TableName),
                    Column = QualifyColumn(addColumn.Column) with { SchemaName = OnTable(addColumn, addColumn.TableName) },
                },
                AlterTableRowSecurityNode rowSecurity => rowSecurity with { SchemaName = OnTable(rowSecurity, rowSecurity.TableName) },
                AlterTableColumnDefaultNode columnDefault => columnDefault with { SchemaName = OnTable(columnDefault, columnDefault.TableName) },
                AlterTableAddIdentityNode addIdentity => addIdentity with { SchemaName = OnTable(addIdentity, addIdentity.TableName) },
                AlterTableColumnStorageNode columnStorage => columnStorage with { SchemaName = OnTable(columnStorage, columnStorage.TableName) },
                AlterTableAttachPartitionNode attach => attach with
                {
                    SchemaName = OnTable(attach, attach.TableName),
                    ChildSchemaName = ResolveReference(attach.ChildSchemaName, attach.IsChildSchemaDefaulted, attach.ChildName, _relationSchemas),
                },
                AlterIndexAttachPartitionNode attachIndex => attachIndex with
                {
                    SchemaName = ResolveReference(attachIndex.SchemaName, attachIndex.IsSchemaDefaulted, attachIndex.IndexName, _indexSchemas),
                    ChildSchemaName = ResolveReference(attachIndex.ChildSchemaName, attachIndex.IsChildSchemaDefaulted, attachIndex.ChildIndexName, _indexSchemas),
                },
                AlterTableObjectStateNode objectState => objectState with { SchemaName = OnTable(objectState, objectState.TableName) },
                AlterTableClusterOnNode clusterOn => clusterOn with { SchemaName = OnTable(clusterOn, clusterOn.TableName) },
                AlterTableReplicaIdentityNode replicaIdentity => replicaIdentity with { SchemaName = OnTable(replicaIdentity, replicaIdentity.TableName) },
                AlterSequenceOwnedByNode ownedBy => ownedBy with { SchemaName = ResolveReference(ownedBy.SchemaName, ownedBy.IsSchemaDefaulted, ownedBy.Name, _sequenceSchemas) },
                UnsupportedAlterTableNode unsupported => unsupported with { SchemaName = OnTable(unsupported, unsupported.TableName) },
                NotNullConstraintNode notNull => notNull with { SchemaName = OnTable(notNull, notNull.TableName) },
                ConstraintNode constraint => constraint with
                {
                    SchemaName = OnTable(constraint, constraint.TableName),
                    ForeignKeyReferencedSchemaName = ResolveReference(
                        constraint.ForeignKeyReferencedSchemaName,
                        constraint.IsForeignKeyReferencedSchemaDefaulted,
                        constraint.ForeignKeyReferencedTableName,
                        _relationSchemas),
                },
                IndexNode index => index with { SchemaName = OnTable(index, index.TableName) },
                PolicyNode policy => policy with { SchemaName = OnTable(policy, policy.TableName) },
                RuleNode rule => rule with { SchemaName = OnTable(rule, rule.TableName) },
                ObjectCommentNode comment => comment with { SchemaName = ResolveReference(comment.SchemaName, comment.IsSchemaDefaulted, comment.TableName, CommentTargets(comment.ObjectKind)) },
                OwnerNode owner => owner with { SchemaName = ResolveReference(owner.SchemaName, owner.IsSchemaDefaulted, owner.ObjectName, OwnerTargets(owner.ObjectKind)) },
                GrantNode grant => grant with
                {
                    Targets = grant.Targets
                        .Select(target => target with { SchemaName = ResolveReference(target.SchemaName, target.IsSchemaDefaulted, target.Name, GrantTargets(grant.ObjectKind)) })
                        .ToArray(),
                },
                FunctionNode function => function with { Signature = QualifySignature(function.Signature) },
                ProcedureNode procedure => procedure with { Signature = QualifySignature(procedure.Signature) },
                DomainNode domain => domain with { BaseType = QualifyType(domain.BaseType) },
                AlterDomainCheckNode alterDomain => alterDomain with { SchemaName = ResolveReference(alterDomain.SchemaName, alterDomain.IsSchemaDefaulted, alterDomain.DomainName, _typeSchemas) },
                TypeNode type => type with
                {
                    CompositeFieldTypes = type.CompositeFieldTypes.Select(field => field with { BaseType = QualifyType(field.BaseType) }).ToArray(),
                    RangeSubtype = type.RangeSubtype is { } subtype ? subtype with { BaseType = QualifyType(subtype.BaseType) } : null,
                },
                TriggerNode trigger => trigger with
                {
                    SchemaName = OnTable(trigger, trigger.TableName),
                    FunctionName = Qualify(trigger.FunctionName, _functionSchemas, isType: false),
                },
                EventTriggerNode eventTrigger => eventTrigger with { FunctionName = Qualify(eventTrigger.FunctionName, _functionSchemas, isType: false) },
                AggregateNode aggregate => aggregate with
                {
                    Arguments = aggregate.Arguments.Select(QualifyArgument).ToArray(),
                    Options = aggregate.Options.ToDictionary(
                        static option => option.Key,
                        option => AggregateOptions.FunctionOptions.Contains(option.Key)
                            ? Qualify(option.Value, _functionSchemas, isType: false) ?? option.Value
                            : AggregateOptions.TypeOptions.Contains(option.Key)
                                ? QualifyType(option.Value) ?? option.Value
                                : option.Value,
                        StringComparer.Ordinal),
                },
                OperatorNode @operator => @operator with
                {
                    LeftType = QualifyType(@operator.LeftType),
                    RightType = QualifyType(@operator.RightType),
                    FunctionName = Qualify(@operator.FunctionName, _functionSchemas, isType: false),
                    RestrictFunction = Qualify(@operator.RestrictFunction, _functionSchemas, isType: false),
                    JoinFunction = Qualify(@operator.JoinFunction, _functionSchemas, isType: false),
                },
                CastNode cast => cast with
                {
                    SourceType = QualifyType(cast.SourceType),
                    TargetType = QualifyType(cast.TargetType),
                    FunctionName = Qualify(cast.FunctionName, _functionSchemas, isType: false),
                    FunctionArguments = cast.FunctionArguments.Select(argument => QualifyType(argument) ?? argument).ToArray(),
                },
                _ => node,
            };

        private ColumnNode QualifyColumn(ColumnNode column) => column with { DataType = QualifyType(column.DataType), RawType = QualifyType(column.RawType) };

        /// <summary>The schema of the table a node names, found along the path when it was not written.</summary>
        private string? OnTable(SqlNode node, string? tableName) => ResolveReference(node.SchemaName, node.IsSchemaDefaulted, tableName, _relationSchemas);

        /// <summary>
        /// For a reference whose schema was not written, the first schema of the path that has an object of that name; otherwise, or when no
        /// schema on the path has one, the schema it already has.
        /// </summary>
        private string? ResolveReference(string? schema, bool defaulted, string? name, Dictionary<string, HashSet<string>>? declared)
            => defaulted && name is not null && declared is not null && declared.TryGetValue(name, out var schemas)
                ? _searchPath.FirstOrDefault(schemas.Contains) ?? schema
                : schema;

        private Dictionary<string, HashSet<string>>? CommentTargets(string objectKind)
            => objectKind.ToUpperInvariant() switch
            {
                "TABLE" or "COLUMN" or "VIEW" or "MATERIALIZEDVIEW" or "MATERIALIZED VIEW" => _relationSchemas,
                "FUNCTION" => _functionSchemas,
                "PROCEDURE" => _procedureSchemas,
                "SEQUENCE" => _sequenceSchemas,
                "TYPE" or "DOMAIN" => _typeSchemas,
                "INDEX" => _indexSchemas,
                _ => null,
            };

        private Dictionary<string, HashSet<string>>? OwnerTargets(string objectKind)
            => objectKind switch
            {
                "Table" or "View" or "MaterializedView" => _relationSchemas,
                "Sequence" => _sequenceSchemas,
                "Function" or "Aggregate" => _functionSchemas,
                "Procedure" => _procedureSchemas,
                "Type" or "Domain" => _typeSchemas,
                _ => null,
            };

        private Dictionary<string, HashSet<string>>? GrantTargets(string objectKind)
            => objectKind.ToLowerInvariant() switch
            {
                "table" => Merge(_relationSchemas, _sequenceSchemas),
                "sequence" => _sequenceSchemas,
                "function" => _functionSchemas,
                "procedure" => _procedureSchemas,
                "routine" => Merge(_functionSchemas, _procedureSchemas),
                _ => null,
            };

        private static Dictionary<string, HashSet<string>> Merge(Dictionary<string, HashSet<string>> first, Dictionary<string, HashSet<string>> second)
        {
            var merged = first.ToDictionary(static pair => pair.Key, static pair => new HashSet<string>(pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var (name, schemas) in second)
            {
                foreach (var schema in schemas)
                {
                    Add(merged, name, schema);
                }
            }

            return merged;
        }

        private RoutineSignature? QualifySignature(RoutineSignature? signature)
            => signature is null
                ? null
                : signature with
                {
                    Parameters = signature.Parameters.Select(parameter => parameter with { DataType = QualifyType(parameter.DataType) }).ToArray(),
                    ReturnType = QualifyType(signature.ReturnType),
                };

        private string? QualifyType(string? typeName) => Qualify(typeName, _typeSchemas, isType: true);

        /// <summary>An aggregate's argument type, keeping a <c>VARIADIC </c> marker in front (G-130).</summary>
        private string QualifyArgument(string argument)
            => argument.StartsWith("VARIADIC ", StringComparison.Ordinal)
                ? "VARIADIC " + (QualifyType(argument["VARIADIC ".Length..]) ?? argument["VARIADIC ".Length..])
                : QualifyType(argument) ?? argument;

        /// <summary>The name with the schema the path finds it in, when that is not <c>public</c>; otherwise the name as written.</summary>
        private string? Qualify(string? written, Dictionary<string, HashSet<string>> declared, bool isType)
        {
            if (string.IsNullOrWhiteSpace(written))
            {
                return written;
            }

            var name = written.Trim();
            var suffix = string.Empty;
            while (isType && name.EndsWith("[]", StringComparison.Ordinal))
            {
                suffix += "[]";
                name = name[..^2].TrimEnd();
            }

            if (name.Contains('.', StringComparison.Ordinal) || name.Contains('"', StringComparison.Ordinal)
                || (isType && BuiltIns.IsKnownBuiltInType(name))
                || !declared.TryGetValue(name, out var schemas))
            {
                return written;
            }

            var schema = _searchPath.FirstOrDefault(schemas.Contains);
            return schema is null || string.Equals(schema, "public", StringComparison.Ordinal)
                ? written
                : $"{schema}.{name}{suffix}";
        }

        private static void Add(Dictionary<string, HashSet<string>> declared, string name, string schema)
        {
            if (!declared.TryGetValue(name, out var schemas))
            {
                declared[name] = schemas = new HashSet<string>(StringComparer.Ordinal);
            }

            schemas.Add(schema);
        }
    }
}
