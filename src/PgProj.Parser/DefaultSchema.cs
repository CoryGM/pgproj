namespace PgProj.Parser;

/// <summary>
/// PostgreSQL creates an object named without a schema in the first schema on <c>search_path</c>, which by default is
/// <c>public</c>, and a live database reads every object back schema-qualified. So an unqualified name in project SQL
/// (<c>CREATE TABLE t</c>, <c>CREATE INDEX i ON t</c>, <c>REFERENCES parent</c>, <c>GRANT ... ON t</c>) means the object in
/// <c>public</c> (G-114). Before this, such an object was keyed without a schema (<c>t</c>) and never matched its live counterpart
/// (<c>public.t</c>), so a project written without qualifiers never converged. Applied to every parsed node once, so every later
/// stage (keys, references, generated SQL) sees the same qualified name. The default is <c>public</c>, PostgreSQL's default
/// <c>search_path</c>, or the first schema of a project's <c>searchPath</c> (G-93).
/// </summary>
public static class DefaultSchema
{
    public const string Name = "public";

    public static IReadOnlyList<SqlNode> Apply(IReadOnlyList<SqlNode> nodes)
        => Apply(nodes, Name);

    /// <summary>Gives every unqualified schema-scoped name <paramref name="schemaName"/>, a project's first <c>searchPath</c> schema (G-93).</summary>
    public static IReadOnlyList<SqlNode> Apply(IReadOnlyList<SqlNode> nodes, string schemaName)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);

        var applier = new Applier(schemaName);
        return nodes.Select(applier.Apply).ToArray();
    }

    /// <summary>Gives each node's unqualified names one default schema.</summary>
    private sealed class Applier(string schemaName)
    {
        public SqlNode Apply(SqlNode node)
            => Mark(node) switch
            {
                TableNode table => table with
                {
                    SchemaName = Or(table.SchemaName),
                    Columns = table.Columns.Select(ApplyColumn).ToArray(),
                    PartitionOfSchemaName = table.PartitionOfTableName is null ? table.PartitionOfSchemaName : Or(table.PartitionOfSchemaName),
                    IsPartitionOfSchemaDefaulted = table.PartitionOfTableName is not null && Unwritten(table.PartitionOfSchemaName),
                    OfTypeSchemaName = table.OfTypeName is null ? table.OfTypeSchemaName : Or(table.OfTypeSchemaName),
                    IsOfTypeSchemaDefaulted = table.OfTypeName is not null && Unwritten(table.OfTypeSchemaName),
                    InheritsFrom = table.InheritsFrom.Select(parent => parent with { SchemaName = Or(parent.SchemaName), IsSchemaDefaulted = Unwritten(parent.SchemaName) }).ToArray(),
                    LikeClauses = table.LikeClauses.Select(like => like with { SchemaName = Or(like.SchemaName), IsSchemaDefaulted = Unwritten(like.SchemaName) }).ToArray(),
                },
                ColumnNode column => ApplyColumn(column),
                ConstraintNode constraint => constraint with
                {
                    SchemaName = Or(constraint.SchemaName),
                    ForeignKeyReferencedSchemaName = constraint.ForeignKeyReferencedTableName is null
                        ? constraint.ForeignKeyReferencedSchemaName
                        : Or(constraint.ForeignKeyReferencedSchemaName),
                    IsForeignKeyReferencedSchemaDefaulted = constraint.ForeignKeyReferencedTableName is not null && Unwritten(constraint.ForeignKeyReferencedSchemaName),
                },
                AlterTableAddColumnNode addColumn => addColumn with { SchemaName = Or(addColumn.SchemaName), Column = ApplyColumn(addColumn.Column) },
                AlterTableRowSecurityNode rowSecurity => rowSecurity with { SchemaName = Or(rowSecurity.SchemaName) },
                UnsupportedAlterTableNode unsupported => unsupported with { SchemaName = Or(unsupported.SchemaName) },
                NotNullConstraintNode notNull => notNull with { SchemaName = Or(notNull.SchemaName) },
                ViewNode view => view with { SchemaName = Or(view.SchemaName) },
                FunctionNode function => function with { SchemaName = Or(function.SchemaName) },
                ProcedureNode procedure => procedure with { SchemaName = Or(procedure.SchemaName) },
                SequenceNode sequence => sequence with { SchemaName = Or(sequence.SchemaName) },
                TypeNode type => type with { SchemaName = Or(type.SchemaName) },
                BaseTypeNode baseType => baseType with { SchemaName = Or(baseType.SchemaName) },
                OperatorNode @operator => @operator with { SchemaName = Or(@operator.SchemaName) },
                AggregateNode aggregate => aggregate with { SchemaName = Or(aggregate.SchemaName) },
                CollationNode collation => collation with { SchemaName = Or(collation.SchemaName) },
                TextSearchDictionaryNode dictionary => dictionary with { SchemaName = Or(dictionary.SchemaName) },
                TextSearchDictionaryAlterNode dictionaryAlter => dictionaryAlter with { SchemaName = Or(dictionaryAlter.SchemaName) },
                TextSearchConfigurationNode configuration => configuration with { SchemaName = Or(configuration.SchemaName) },
                TextSearchMappingNode mapping => mapping with { SchemaName = Or(mapping.SchemaName) },
                StatisticsNode statistics => statistics with { SchemaName = Or(statistics.SchemaName), TableSchemaName = Or(statistics.TableSchemaName) },
                PublicationNode publication => publication with
                {
                    Tables = publication.Tables.Select(table => table with { SchemaName = Or(table.SchemaName) }).ToArray(),
                    Schemas = publication.Schemas.Select(schema => string.Equals(schema, "CURRENT_SCHEMA", StringComparison.Ordinal) ? schemaName : schema).ToArray(),
                },
                AlterPublicationNode alterPublication => alterPublication with
                {
                    Tables = alterPublication.Tables.Select(table => table with { SchemaName = Or(table.SchemaName) }).ToArray(),
                    Schemas = alterPublication.Schemas.Select(schema => string.Equals(schema, "CURRENT_SCHEMA", StringComparison.Ordinal) ? schemaName : schema).ToArray(),
                },
                DomainNode domain => domain with { SchemaName = Or(domain.SchemaName) },
                AlterDomainCheckNode alterDomain => alterDomain with { SchemaName = Or(alterDomain.SchemaName) },
                AlterTableColumnDefaultNode columnDefault => columnDefault with { SchemaName = Or(columnDefault.SchemaName) },
                AlterTableAddIdentityNode addIdentity => addIdentity with { SchemaName = Or(addIdentity.SchemaName) },
                AlterTableColumnStorageNode columnStorage => columnStorage with { SchemaName = Or(columnStorage.SchemaName) },
                AlterTableAttachPartitionNode attach => attach with
                {
                    SchemaName = Or(attach.SchemaName),
                    ChildSchemaName = Or(attach.ChildSchemaName),
                    IsChildSchemaDefaulted = Unwritten(attach.ChildSchemaName),
                },
                AlterSequenceOwnedByNode ownedBy => ownedBy with { SchemaName = Or(ownedBy.SchemaName) },
                AlterTableObjectStateNode objectState => objectState with { SchemaName = Or(objectState.SchemaName) },
                AlterTableClusterOnNode clusterOn => clusterOn with { SchemaName = Or(clusterOn.SchemaName) },
                AlterTableReplicaIdentityNode replicaIdentity => replicaIdentity with { SchemaName = Or(replicaIdentity.SchemaName) },
                AlterIndexAttachPartitionNode attachIndex => attachIndex with
                {
                    SchemaName = Or(attachIndex.SchemaName),
                    ChildSchemaName = Or(attachIndex.ChildSchemaName),
                    IsChildSchemaDefaulted = Unwritten(attachIndex.ChildSchemaName),
                },
                IndexNode index => index with { SchemaName = Or(index.SchemaName) },
                TriggerNode trigger => trigger with { SchemaName = Or(trigger.SchemaName) },
                PolicyNode policy => policy with { SchemaName = Or(policy.SchemaName) },
                RuleNode rule => rule with { SchemaName = Or(rule.SchemaName) },
                ObjectCommentNode comment => comment with { SchemaName = Or(comment.SchemaName) },
                OwnerNode owner => owner with { SchemaName = SchemaScopedOwnerKinds.Contains(owner.ObjectKind) ? Or(owner.SchemaName) : owner.SchemaName },
                GrantNode grant => grant with { Targets = grant.Targets.Select(ApplyTarget(grant)).ToArray() },
                _ => node,
            };

        private ColumnNode ApplyColumn(ColumnNode column) => column with { SchemaName = Or(column.SchemaName) };

        /// <summary>The kinds of object an owner can be stated for that live in a schema (G-146); a schema, an event trigger, and a publication do not.</summary>
        private static readonly HashSet<string> SchemaScopedOwnerKinds = new(StringComparer.Ordinal)
        {
            "Table", "View", "MaterializedView", "Sequence", "Function", "Procedure", "Aggregate", "Operator", "Type", "Domain", "Collation", "Statistics", "TextSearchDictionary", "TextSearchConfiguration",
        };

        /// <summary>The grant object kinds whose targets live in a schema; a schema itself, a database, a role, and so on do not.</summary>
        private static readonly HashSet<string> SchemaScopedGrantKinds = new(StringComparer.OrdinalIgnoreCase) { "table", "sequence", "function", "procedure", "routine" };

        /// <summary>A named object's grant target takes the default; a schema-level target (the schema itself, or <c>ALL ... IN SCHEMA</c>) and a role do not.</summary>
        private Func<GrantTarget, GrantTarget> ApplyTarget(GrantNode grant)
            => target => grant.Form == GrantForm.Object && SchemaScopedGrantKinds.Contains(grant.ObjectKind)
                ? target with { SchemaName = Or(target.SchemaName), IsSchemaDefaulted = Unwritten(target.SchemaName) }
                : target;

        private string Or(string? name) => Unwritten(name) ? schemaName : name!;

        private static bool Unwritten(string? name) => string.IsNullOrWhiteSpace(name);

        /// <summary>Records that the node's own schema is about to be defaulted (G-93), before the switch below gives it one.</summary>
        private static SqlNode Mark(SqlNode node) => Unwritten(node.SchemaName) ? node with { IsSchemaDefaulted = true } : node;
    }
}
