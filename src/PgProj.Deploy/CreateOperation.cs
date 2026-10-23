using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy;

public sealed record class CreateOperation : SqlOperation
{
    public required ISemanticNode Node { get; init; }

    /// <summary>
    /// The comment the replaced object carried before this operation, when this <c>CREATE OR REPLACE</c> stands in for a
    /// modification (a view or a routine with an unchanged signature) rather than a genuinely new object (G-34). <c>CREATE OR
    /// REPLACE</c> does not touch an existing comment it is not setting, so a comment that was removed needs its own
    /// <c>COMMENT ON ... IS NULL</c> or it silently survives live and the diff never converges. Null for a new object, where
    /// there is nothing to clear.
    /// </summary>
    public string? PreviousComment { get; init; }

    /// <summary>The column comments a view replaced in place had (G-216), so those that changed are set and those removed cleared; null for a new view.</summary>
    public IReadOnlyDictionary<string, string>? PreviousColumnComments { get; init; }

    /// <summary>For a view replaced in place: the defaults its columns had (G-291), which <c>CREATE OR REPLACE VIEW</c> keeps, so only a changed or removed one is written. Null for a new or recreated view.</summary>
    public IReadOnlyDictionary<string, string>? PreviousColumnDefaults { get; init; }

    /// <summary>
    /// For a view replaced in place, the output columns to rename first (<see cref="Semantics.ViewColumnRenames"/>, G-276), each name as <c>quote_ident</c> wrote it:
    /// <c>CREATE OR REPLACE VIEW</c> cannot rename one.
    /// </summary>
    public IReadOnlyList<(string From, string To)> ViewColumnRenames { get; init; } = [];

    /// <summary>
    /// For an index on a partitioned table under <c>publish --concurrently</c> (G-271): created <c>ON ONLY</c> the parent, so it is made at once and stays invalid until
    /// each partition's index, built concurrently after the commit, is attached to it.
    /// </summary>
    public bool OnlyParent { get; init; }

    public override string GenerateSql()
        => Node switch
        {
            TableDefinition table => GenerateCreateTable(table),
            ViewDefinition view => GenerateCreateView(view, PreviousComment, PreviousColumnComments, ViewColumnRenames, PreviousColumnDefaults),
            FunctionDefinition function => GenerateCreateFunction(function, PreviousComment),
            ProcedureDefinition procedure => GenerateCreateProcedure(procedure, PreviousComment),
            TriggerDefinition trigger => GenerateCreateTrigger(trigger) + (trigger.EnabledState is null or "ENABLE" ? string.Empty : "\n" + CommentSqlHelper.GenerateTriggerState(trigger.SchemaName, trigger.TableName ?? "<table>", trigger.Name, trigger.EnabledState))
                + string.Concat(CommentSqlHelper.GeneratePartitionTriggerStates(trigger.Name, trigger.PartitionStates).Select(static statement => "\n" + statement)),
            TypeDefinition type => GenerateCreateType(type),
            IndexDefinition index => GenerateCreateIndex(index, OnlyParent) + IndexFlagStatements(index.SchemaName, index.TableName, index.Name, index.IsClustered, index.IsReplicaIdentity),
            ConstraintDefinition constraint => GenerateCreateConstraint(constraint) + IndexFlagStatements(constraint.SchemaName, constraint.TableName, constraint.Name, constraint.IsClustered, constraint.IsReplicaIdentity),
            SequenceDefinition sequence => GenerateCreateSequence(sequence),
            PolicyDefinition policy => GenerateCreatePolicy(policy),
            EventTriggerDefinition eventTrigger => GenerateCreateEventTrigger(eventTrigger),
            CastDefinition cast => GenerateCreateCast(cast),
            OperatorDefinition definition => GenerateCreateOperator(definition),
            RuleDefinition rule => GenerateCreateRule(rule) + (rule.EnabledState is null or "ENABLE" ? string.Empty : "\n" + CommentSqlHelper.GenerateRuleState(rule.SchemaName, rule.TableName ?? "<table>", rule.Name, rule.EnabledState)),
            AggregateDefinition aggregate => GenerateCreateAggregate(aggregate),
            CollationDefinition collation => GenerateCreateCollation(collation),
            StatisticsDefinition statistics => GenerateCreateStatistics(statistics),
            TextSearchDictionaryDefinition dictionary => TextSearchSqlBuilder.CreateDictionary(dictionary),
            TextSearchConfigurationDefinition configuration => TextSearchSqlBuilder.CreateConfiguration(configuration),
            PublicationDefinition publication => GenerateCreatePublication(publication),
            GrantDefinition grant => GenerateGrant(grant),
            OwnerDefinition owner => OwnerSqlBuilder.Alter(owner),
            SchemaDefinition schema => CommentSqlHelper.GenerateSchemaComment(schema.Name, schema.Comment),
            _ => $"-- TODO: Generate CREATE SQL for {NodeKind} '{NodeId}'.",
        };

    private static string GenerateCreateTable(TableDefinition table)
    {
        var unlogged = table.IsUnlogged ? "UNLOGGED " : string.Empty;
        var withClause = table.StorageOptions.Count == 0
            ? string.Empty
            : $"\nWITH ({string.Join(", ", table.StorageOptions.Select(static option => option.RawText))})";
        var tablespaceClause = string.IsNullOrWhiteSpace(table.TablespaceName)
            ? string.Empty
            : $"\nTABLESPACE {QuoteIdentifier(table.TablespaceName)}";

        // The access method as written (G-223).
        var usingClause = table.AccessMethod is null ? string.Empty : $"\nUSING {QuoteIdentifier(table.AccessMethod)}";

        // A parent's own key; a sub-partitioned child (itself further partitioned into partitions) can have one too (G-24c).
        var partitionByClause = table.PartitionStrategy is null
            ? string.Empty
            : $"\nPARTITION BY {table.PartitionStrategy} ({string.Join(", ", table.PartitionKeyElements.Select(static element => element.ToSql(QuoteDottedName)))})";

        string sql;
        if (table.PartitionKeepsOwnColumns && !string.IsNullOrWhiteSpace(table.PartitionOfTableName))
        {
            // A NOT NULL constraint named otherwise than the parent's: PARTITION OF would give it the parent's name, so the partition is made with its columns and attached (G-264).
            // A partition's column is never an identity of its own; PostgreSQL refuses to attach a table that has one.
            var columns = string.Join(",\n", table.Columns.Select(static column => $"    {BuildColumnDefinitionSql(column with { IdentityGeneration = null })}"));
            var parentName = FormatQualifiedName(table.PartitionOfSchemaName, table.PartitionOfTableName);
            sql = $"CREATE {unlogged}TABLE {FormatQualifiedName(table.SchemaName, table.Name)} (\n{columns}\n){partitionByClause}{usingClause}{withClause}{tablespaceClause};\n"
                + $"ALTER TABLE {parentName} ATTACH PARTITION {FormatQualifiedName(table.SchemaName, table.Name)} {table.PartitionBoundRaw};\n"
                + "-- NOTE: Constraints and indexes deploy as separate ALTER TABLE/CREATE INDEX statements.";
        }
        else if (!string.IsNullOrWhiteSpace(table.PartitionOfTableName))
        {
            // A partition's own DDL never lists columns (PostgreSQL rejects one here) — they are inherited from its parent,
            // which is already carried on the model (Phase 2) for everything else that needs them (G-24c).
            // Its element list of column options and constraints, when it has one, is deployed as written (G-266).
            var parentName = FormatQualifiedName(table.PartitionOfSchemaName, table.PartitionOfTableName);
            var elementsClause = table.PartitionElementsText is null ? string.Empty : $" {table.PartitionElementsText}";
            sql = $"CREATE {unlogged}TABLE {FormatQualifiedName(table.SchemaName, table.Name)} PARTITION OF {parentName}{elementsClause} {table.PartitionBoundRaw}{partitionByClause}{usingClause}{withClause}{tablespaceClause};\n"
                + "-- NOTE: Constraints and indexes deploy as separate ALTER TABLE/CREATE INDEX statements.";
        }
        else if (table.IsTypedTable)
        {
            // A typed table lists only the columns it gives a default or NOT NULL, each as WITH OPTIONS; the others are its type's fields as they are (G-136).
            var options = table.Columns.Select(BuildTypedColumnOptionsSql).OfType<string>().ToList();
            var elements = options.Count == 0 ? string.Empty : $" (\n{string.Join(",\n", options.Select(static option => $"    {option}"))}\n)";
            sql = $"CREATE {unlogged}TABLE {FormatQualifiedName(table.SchemaName, table.Name)} OF {FormatQualifiedName(table.OfTypeSchemaName, table.OfTypeName!)}{elements}{partitionByClause}{usingClause}{withClause}{tablespaceClause};\n"
                + "-- NOTE: Constraints and indexes deploy as separate ALTER TABLE/CREATE INDEX statements.";
        }
        else
        {
            // A column a child inherits is its parent's, not written here; INHERITS gives it (G-194). A column a LIKE clause copies is not written either:
            // the clause is, as written, where it was written among the table's own columns (G-221).
            var ownColumns = table.Columns.Where(static column => column.InheritedFromTable is null && column.CopiedByLike is null).ToArray();
            var elements = new List<string>();
            for (var index = 0; index <= ownColumns.Length; index++)
            {
                elements.AddRange(table.LikeClauses.Where(like => like.OwnColumnsBefore == index || (index == ownColumns.Length && like.OwnColumnsBefore > index)).Select(static like => like.RawText));
                if (index < ownColumns.Length)
                {
                    elements.Add(BuildColumnDefinitionSql(ownColumns[index]));
                }
            }

            // A child's own NOT NULL constraint on a column it only inherits is a constraint among its elements (PostgreSQL 18; G-253).
            elements.AddRange(table.Columns
                .Where(static column => column.OwnsInheritedNotNull)
                .Select(column => $"{(string.IsNullOrWhiteSpace(column.NotNullName) ? string.Empty : $"CONSTRAINT {QuoteIdentifier(column.NotNullName)} ")}NOT NULL {QuoteIdentifier(column.Name)}{(column.NotNullNoInherit ? " NO INHERIT" : string.Empty)}"));

            var columns = elements.Count == 0
                ? (table.InheritsFrom.Count == 0 ? "    -- NOTE: This table declares no columns." : string.Empty)
                : string.Join(",\n", elements.Select(static element => $"    {element}"));
            var inheritsClause = table.InheritsFrom.Count == 0 ? string.Empty : $"\nINHERITS ({string.Join(", ", table.InheritsFrom.Select(FormatTableKey))})";
            sql = $"CREATE {unlogged}TABLE {FormatQualifiedName(table.SchemaName, table.Name)} (\n{columns}\n){inheritsClause}{partitionByClause}{usingClause}{withClause}{tablespaceClause};\n"
                + "-- NOTE: Constraints and indexes deploy as separate ALTER TABLE/CREATE INDEX statements.";
        }

        // PostgreSQL has no way to enable row security from CREATE TABLE itself, so a table declared with it already on is a
        // trailing ALTER TABLE, exactly like a live-loaded one (LiveDatabaseModelLoader.BuildCreateTableStatement) (G-76).
        var trailingStatements = new List<string>();
        var qualifiedName = FormatQualifiedName(table.SchemaName, table.Name);

        // A storage the project set with ALTER TABLE ... SET STORAGE, as written (G-269): every supported version accepts this form.
        trailingStatements.AddRange(table.Columns
            .Where(static column => column is { IsStorageSetByAlter: true, Storage: not null, InheritedFromTable: null, CopiedByLike: null })
            .Select(column => BuildSetStorageSql(qualifiedName, column)));
        if (table.IsRowSecurityEnabled)
        {
            trailingStatements.Add($"ALTER TABLE {qualifiedName} ENABLE ROW LEVEL SECURITY;");
        }

        if (table.IsRowSecurityForced)
        {
            trailingStatements.Add($"ALTER TABLE {qualifiedName} FORCE ROW LEVEL SECURITY;");
        }

        // A child's own default for a column it inherits (G-237): the parent's default is cloned, so the child's is a statement after the table.
        // One written in a partition's element list is already in its CREATE TABLE (G-266).
        foreach (var column in table.Columns.Where(column => column.OverridesInheritedDefault
            && !table.InheritedColumnDefaults.Any(listed => listed.IsInPartitionOfList && string.Equals(listed.ColumnName, column.Name, StringComparison.Ordinal))))
        {
            trailingStatements.Add(string.IsNullOrWhiteSpace(column.RawDefaultExpression)
                ? $"ALTER TABLE ONLY {qualifiedName} ALTER COLUMN {QuoteIdentifier(column.Name)} DROP DEFAULT;"
                : $"ALTER TABLE ONLY {qualifiedName} ALTER COLUMN {QuoteIdentifier(column.Name)} SET DEFAULT {column.RawDefaultExpression};");
        }

        // A NOT VALID NOT NULL is a separate ALTER TABLE (G-121): PostgreSQL 18 has no way to write it inside CREATE TABLE.
        if (string.IsNullOrWhiteSpace(table.PartitionOfTableName))
        {
            trailingStatements.AddRange(table.Columns
                .Where(static column => column.NotNullIsNotValid && column.IsNullable == false && column.InheritedFromTable is null)
                .Select(column => BuildNotValidNotNullSql(qualifiedName, column)));
        }

        // FULL and NOTHING are the table's own; USING INDEX follows the index (G-255).
        if (table.ReplicaIdentity is "FULL" or "NOTHING")
        {
            trailingStatements.Add(CommentSqlHelper.GenerateReplicaIdentity(table.SchemaName, table.Name, table.ReplicaIdentity));
        }

        var tableComment = CommentSqlHelper.GenerateTableComment(table.SchemaName, table.Name, table.Comment);
        if (tableComment is not null)
        {
            trailingStatements.Add(tableComment);
        }

        trailingStatements.AddRange(table.Columns
            .Where(static column => column.Comment is not null)
            .Select(column => CommentSqlHelper.GenerateColumnComment(table.SchemaName, table.Name, column.Name, column.Comment)));

        // A comment on a column's NOT NULL constraint (G-235).
        trailingStatements.AddRange(table.Columns
            .Where(static column => column.IsNullable == false && column.NotNullComment is not null && column.NotNullName is not null && column.InheritedFromTable is null)
            .Select(column => CommentSqlHelper.GenerateNotNullConstraintComment(table.SchemaName, table.Name, column.NotNullName!, column.NotNullComment)));

        return trailingStatements.Count == 0
            ? sql
            : $"{sql}\n{string.Join("\n", trailingStatements)}";
    }

    /// <summary>
    /// Maps pg_query's internal, <c>pg_catalog</c>-qualified name for a SQL-standard keyword type spelling
    /// (<c>integer</c>, <c>bigint</c>, ...) back to a friendly name (G-88). Without this, a column declared
    /// <c>integer</c> deployed as <c>pg_catalog.int4</c>: valid SQL, but not what the project wrote. Only entries
    /// whose modifier (if any) can still go at the very end are listed here - the same position
    /// <see cref="BuildColumnTypeSql"/> already appends it at, matching PostgreSQL's own <c>format_type()</c>
    /// output for these (confirmed against a real server): <c>character varying(20)</c>, <c>bit varying(3)</c>.
    /// <c>timestamp</c>/<c>timestamptz</c>/<c>time</c>/<c>timetz</c> are deliberately *not* mapped to their
    /// SQL-standard multi-word spelling (<c>timestamp with time zone</c>): PostgreSQL requires the modifier
    /// *between* the two halves there (<c>timestamp(3) with time zone</c>, never
    /// <c>timestamp with time zone(3)</c>), which this method has nowhere to insert - so those, plus every other
    /// type this table doesn't know (an ordinary catalog name like <c>uuid</c>/<c>jsonb</c>, a custom type, or a
    /// future PostgreSQL keyword type), fall through to the safe default below: strip only the
    /// <c>pg_catalog.</c> prefix, keeping the short catalog spelling (<c>timestamptz</c>) that already puts a
    /// modifier in the right place.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FriendlyTypeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["int2"] = "smallint",
        ["int4"] = "integer",
        ["int8"] = "bigint",
        ["float4"] = "real",
        ["float8"] = "double precision",
        ["bool"] = "boolean",
        ["bpchar"] = "character",
        ["varchar"] = "character varying",
        ["varbit"] = "bit varying",
    };

    internal static string? FormatFriendlyTypeName(string? dataType)
    {
        const string prefix = "pg_catalog.";
        if (string.IsNullOrWhiteSpace(dataType) || !dataType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return dataType;
        }

        var unqualified = dataType[prefix.Length..];
        return FriendlyTypeNames.TryGetValue(unqualified, out var friendly) ? friendly : unqualified;
    }

    /// <summary>
    /// Renders a column's complete type: the base type, its modifiers as declared (<c>numeric(10,2)</c>,
    /// <c>varchar(20)</c>, <c>timestamptz(3)</c>), an <c>interval</c>'s field restriction and precision, and array
    /// brackets. Emitting only the base type silently dropped precision, scale, length, and array-ness (G-45).
    /// </summary>
    internal static string BuildColumnTypeSql(ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);

        // What is deployed is what the project wrote (G-207); the spelling built from the model is the fallback.
        return column.WrittenType ?? BuildTypeSql(column.DataType, column.TypeModifiers, column.ArrayDimensions, column.IntervalFields);
    }

    /// <summary>
    /// Renders a full declared type: the base type, its modifiers as declared (<c>numeric(10,2)</c>,
    /// <c>varchar(20)</c>, <c>timestamptz(3)</c>), an <c>interval</c>'s field restriction and precision, and array
    /// brackets. Shared by a column's type (<see cref="BuildColumnTypeSql"/>) and a domain's base type (G-77) -
    /// emitting only the base type silently dropped precision, scale, length, and array-ness (G-45).
    /// </summary>
    internal static string BuildTypeSql(string? dataType, IReadOnlyList<string> typeModifiers, int arrayDimensions, string? intervalFields)
    {
        var baseType = FormatFriendlyTypeName(dataType) ?? "text";
        var builder = new System.Text.StringBuilder();
        var isInterval = baseType.EndsWith("interval", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrWhiteSpace(intervalFields) || typeModifiers.Count > 0);

        if (isInterval)
        {
            // interval takes its restriction as keywords (interval day to second(2)), not as a generic modifier list.
            builder.Append("interval");
            if (!string.IsNullOrWhiteSpace(intervalFields))
            {
                builder.Append(' ').Append(intervalFields);
            }

            if (typeModifiers.Count > 0)
            {
                builder.Append('(').Append(typeModifiers[0]).Append(')');
            }
        }
        else
        {
            builder.Append(SqlIdentifiers.QuoteTypeName(baseType));
            if (typeModifiers.Count > 0)
            {
                builder.Append('(').Append(string.Join(",", typeModifiers)).Append(')');
            }
        }

        for (var dimension = 0; dimension < arrayDimensions; dimension++)
        {
            builder.Append("[]");
        }

        return builder.ToString();
    }

    /// <summary>
    /// A column's <c>NOT NULL</c>, with <c>CONSTRAINT name</c> when the project named it (G-235). Every server accepts the name; PostgreSQL 18 keeps it and an older one
    /// forgets it. A column that inherits its NOT NULL is not written with its parent's name.
    /// </summary>
    internal static string NotNullClause(ColumnDefinition column)
    {
        var inherited = column.InheritedFromTable is not null;
        var noInherit = column.NotNullNoInherit && !inherited ? " NO INHERIT" : string.Empty;
        return (!string.IsNullOrWhiteSpace(column.NotNullName) && !inherited ? $"CONSTRAINT {QuoteIdentifier(column.NotNullName)} NOT NULL" : "NOT NULL") + noInherit;
    }

    /// <summary>
    /// <c>ALTER TABLE ... ADD [CONSTRAINT name] NOT NULL column NOT VALID</c> (PostgreSQL 18; G-121): the NOT NULL that is enforced for new
    /// rows without checking the existing ones. Its name is written when the project or the live database gave one.
    /// </summary>
    internal static string BuildNotValidNotNullSql(string qualifiedTable, ColumnDefinition column)
    {
        var name = string.IsNullOrWhiteSpace(column.NotNullConstraintName) ? string.Empty : $"CONSTRAINT {QuoteIdentifier(column.NotNullConstraintName)} ";
        return $"ALTER TABLE {qualifiedTable} ADD {name}NOT NULL {QuoteIdentifier(column.Name)} NOT VALID;";
    }

    /// <summary>
    /// A typed table's column as <c>WITH OPTIONS</c> writes it (G-136): the column's name and what the table gives it (<c>NOT NULL</c>, a <c>DEFAULT</c> or generated expression,
    /// identity), its type being the field's; null when the table gives it nothing, so the column is not listed.
    /// </summary>
    internal static string? BuildTypedColumnOptionsSql(ColumnDefinition column)
    {
        var options = new List<string>();
        if (column.IsNullable == false && !column.NotNullIsNotValid)
        {
            options.Add(NotNullClause(column));
        }

        if (column.IsGenerated)
        {
            if (!string.IsNullOrWhiteSpace(column.GeneratedExpression))
            {
                options.Add($"GENERATED ALWAYS AS ({NormalizeParenthesizedExpression(column.GeneratedExpression!)}){(column.IsVirtual ? " VIRTUAL" : " STORED")}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(column.RawDefaultExpression))
        {
            options.Add("DEFAULT " + column.RawDefaultExpression);
        }

        if (!string.IsNullOrWhiteSpace(column.IdentityGeneration))
        {
            options.Add($"GENERATED {column.IdentityGeneration} AS IDENTITY{FormatIdentityOptions(column)}");
        }

        return options.Count == 0 ? null : $"{QuoteIdentifier(column.Name)} WITH OPTIONS {string.Join(" ", options)}";
    }

    /// <summary><c>ALTER TABLE ... ALTER COLUMN ... SET STORAGE</c> for a storage the project set that way (G-269).</summary>
    internal static string BuildSetStorageSql(string qualifiedTableName, ColumnDefinition column)
        => $"ALTER TABLE {qualifiedTableName} ALTER COLUMN {QuoteIdentifier(column.Name)} SET STORAGE {column.Storage};";

    internal static string BuildColumnDefinitionSql(ColumnDefinition column)
    {
        var builder = new System.Text.StringBuilder()
            .Append(QuoteIdentifier(column.Name))
            .Append(' ')
            .Append(BuildColumnTypeSql(column));

        // As written, right after the type, where the grammar puts them (G-222); a storage the project set with ALTER TABLE is deployed that way (G-269), after the table.
        if (column.Storage is not null && !column.IsStorageSetByAlter)
        {
            builder.Append(" STORAGE ").Append(column.Storage);
        }

        if (column.Compression is not null)
        {
            builder.Append(" COMPRESSION ").Append(column.Compression);
        }

        if (!string.IsNullOrWhiteSpace(column.CollationName))
        {
            builder.Append(" COLLATE ").Append(QuoteDottedName(column.CollationName));
        }

        // A NOT VALID NOT NULL (PostgreSQL 18) is added after the column exists, by BuildNotValidNotNullSql; written on the column it would be valid.
        if (column.IsNullable == false && !column.NotNullIsNotValid && !column.NotNullIsInherited)
        {
            builder.Append(' ').Append(NotNullClause(column));
        }

        if (column.IsGenerated)
        {
            if (!string.IsNullOrWhiteSpace(column.GeneratedExpression) && !column.GenerationIsInherited)
            {
                builder.Append(" GENERATED ALWAYS AS (").Append(NormalizeParenthesizedExpression(column.GeneratedExpression!)).Append(column.IsVirtual ? ") VIRTUAL" : ") STORED");
            }
        }
        else if (!string.IsNullOrWhiteSpace(column.RawDefaultExpression))
        {
            builder.Append(" DEFAULT ").Append(column.RawDefaultExpression);
        }

        if (!string.IsNullOrWhiteSpace(column.IdentityGeneration))
        {
            builder.Append(" GENERATED ").Append(column.IdentityGeneration).Append(" AS IDENTITY").Append(FormatIdentityOptions(column));
        }

        return builder.ToString();
    }

    /// <summary>An identity's options as written, in parentheses after <c>AS IDENTITY</c> (G-214); empty when it states none.</summary>
    internal static string FormatIdentityOptions(ColumnDefinition column)
        => string.IsNullOrWhiteSpace(column.IdentityOptionsText) ? string.Empty : $" ({column.IdentityOptionsText})";

    private static string GenerateCreateView(ViewDefinition view, string? previousComment, IReadOnlyDictionary<string, string>? previousColumnComments, IReadOnlyList<(string From, string To)> columnRenames, IReadOnlyDictionary<string, string>? previousColumnDefaults = null)
    {
        if (string.IsNullOrWhiteSpace(view.RawBodyExpression))
        {
            return $"-- TODO: No original body was captured for view '{view.Key}'; cannot emit CREATE VIEW.";
        }

        var keyword = view.IsMaterialized ? "MATERIALIZED VIEW" : "VIEW";
        var orReplace = view.IsMaterialized ? string.Empty : "OR REPLACE ";

        // G-180: CREATE OR REPLACE VIEW replaces the options too (an absent WITH clause clears them), so stating the full set here
        // changes one in place. The check option is not part of the header: it is the body's own WITH CHECK OPTION.
        // A materialized view's storage parameters and WITH NO DATA are deployed as written (G-215).
        // A view's options are deployed as written too (G-217), an option set to its default included; a view made without the text (a model built in code)
        // gets its canonical options.
        var headerOptions = view.IsMaterialized
            ? view.StorageOptions.Select(static option => option.RawText.Trim()).ToArray()
            : view.WrittenOptions.Count > 0 ? view.WrittenOptions.Select(static option => option.RawText.Trim()).ToArray() : ViewOptions.HeaderOptions(view.Options).ToArray();
        var withClause = headerOptions.Length == 0 ? string.Empty : $" WITH ({string.Join(", ", headerOptions)})";
        var aliasList = view.ColumnAliases.Count == 0 ? string.Empty : $" ({string.Join(", ", view.ColumnAliases.Select(QuoteIdentifier))})";
        var noData = view.IsMaterialized && view.WithNoData ? "\nWITH NO DATA" : string.Empty;
        // A materialized view's USING and TABLESPACE, as written, where the grammar puts them (G-219).
        var usingClause = view.IsMaterialized && view.AccessMethod is not null ? $" USING {QuoteIdentifier(view.AccessMethod)}" : string.Empty;
        var tablespaceClause = view.IsMaterialized && view.TablespaceName is not null ? $" TABLESPACE {QuoteIdentifier(view.TablespaceName)}" : string.Empty;
        var sql = $"CREATE {orReplace}{keyword} {FormatQualifiedName(view.SchemaName, view.Name)}{aliasList}{usingClause}{withClause}{tablespaceClause} AS\n{view.RawBodyExpression}{noData};";

        var viewComment = CommentSqlHelper.GenerateViewComment(view.SchemaName, view.Name, view.IsMaterialized, view.Comment)
            ?? (view.Comment is null && previousComment is not null ? CommentSqlHelper.GenerateViewCommentClear(view.SchemaName, view.Name, view.IsMaterialized) : null);

        // Its columns' comments (G-216): every one on a new view; on one replaced in place, those that changed, since CREATE OR REPLACE keeps the rest.
        var columnComments = CommentSqlHelper.GenerateColumnCommentChanges(view.SchemaName, view.Name, previousColumnComments ?? ColumnCommentMap.Empty, view.ColumnComments);
        var renames = columnRenames.Select(rename => $"ALTER VIEW {FormatQualifiedName(view.SchemaName, view.Name)} RENAME COLUMN {rename.From} TO {rename.To};");
        // The defaults of its columns (G-291): every one on a new view; on one replaced in place, those that changed or went, since CREATE OR REPLACE keeps the rest.
        var columnDefaults = ColumnCommentMap.Changes(previousColumnDefaults ?? ColumnCommentMap.Empty, view.ColumnDefaults).Select(change =>
            $"ALTER VIEW {FormatQualifiedName(view.SchemaName, view.Name)} ALTER COLUMN {QuoteIdentifier(change.Column)} " + (change.Comment is null ? "DROP DEFAULT;" : $"SET DEFAULT {change.Comment};"));
        return string.Join("\n", renames.Concat(new[] { sql, viewComment }.OfType<string>()).Concat(columnComments).Concat(columnDefaults));
    }

    private static string GenerateCreateFunction(FunctionDefinition function, string? previousComment)
    {
        // A blank body is a body (a stub or placeholder, G-243); only a body that was not read at all cannot be written.
        if (function.RawBody is null)
        {
            return $"-- TODO: No original body was captured for function '{function.Key}'; cannot emit CREATE FUNCTION.";
        }

        var signature = function.Signature;
        var builder = new System.Text.StringBuilder()
            .Append("CREATE OR REPLACE FUNCTION ")
            .Append(FormatQualifiedName(function.SchemaName, function.Name))
            .Append('(')
            .Append(signature?.RenderParameterList() ?? string.Empty)
            .Append(')')
            .Append('\n');

        if (signature?.RenderReturnsClause() is { } returns)
        {
            builder.Append("RETURNS ").Append(returns).Append('\n');
        }

        AppendRoutineTail(builder, function.Language, signature, function.RawBody!, function.BodyDelimiter, function.IsSqlStandardBody);

        var functionComment = CommentSqlHelper.GenerateFunctionComment(function.SchemaName, function.Name, signature?.RenderArgumentTypeSignatureSql(), function.Comment)
            ?? (function.Comment is null && previousComment is not null
                ? CommentSqlHelper.GenerateFunctionCommentClear(function.SchemaName, function.Name, signature?.RenderArgumentTypeSignatureSql())
                : null);
        if (functionComment is not null)
        {
            builder.Append('\n').Append(functionComment);
        }

        return builder.ToString();
    }

    private static string GenerateCreateProcedure(ProcedureDefinition procedure, string? previousComment)
    {
        // A blank body is a body (a stub or placeholder, G-243); only a body that was not read at all cannot be written.
        if (procedure.RawBody is null)
        {
            return $"-- TODO: No original body was captured for procedure '{procedure.Key}'; cannot emit CREATE PROCEDURE.";
        }

        var builder = new System.Text.StringBuilder()
            .Append("CREATE OR REPLACE PROCEDURE ")
            .Append(FormatQualifiedName(procedure.SchemaName, procedure.Name))
            .Append('(')
            .Append(procedure.Signature?.RenderParameterList() ?? string.Empty)
            .Append(')')
            .Append('\n');

        AppendRoutineTail(builder, procedure.Language, procedure.Signature, procedure.RawBody!, procedure.BodyDelimiter, procedure.IsSqlStandardBody);

        var procedureComment = CommentSqlHelper.GenerateProcedureComment(procedure.SchemaName, procedure.Name, procedure.ArgumentIdentitySql, procedure.Comment)
            ?? (procedure.Comment is null && previousComment is not null
                ? CommentSqlHelper.GenerateProcedureCommentClear(procedure.SchemaName, procedure.Name, procedure.ArgumentIdentitySql)
                : null);
        if (procedureComment is not null)
        {
            builder.Append('\n').Append(procedureComment);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends the language and attribute clauses, then the user's body verbatim inside a
    /// delimiter that is guaranteed not to collide with the body's own content.
    /// </summary>
    private static void AppendRoutineTail(
        System.Text.StringBuilder builder,
        string? language,
        RoutineSignature? signature,
        string rawBody,
        string? bodyDelimiter,
        bool isSqlStandardBody = false)
    {
        builder.Append("LANGUAGE ").Append(language ?? "sql").Append('\n');

        if (!string.IsNullOrWhiteSpace(signature?.Volatility))
        {
            builder.Append(signature!.Volatility!.ToUpperInvariant()).Append('\n');
        }

        if (signature?.IsStrict == true)
        {
            builder.Append("STRICT\n");
        }

        if (signature?.IsSecurityDefiner == true)
        {
            builder.Append("SECURITY DEFINER\n");
        }

        // G-197: the rest of the routine's options, as written; CREATE OR REPLACE sets every one of them, so one left out is reset.
        if (signature?.IsWindow == true)
        {
            builder.Append("WINDOW\n");
        }

        if (signature?.IsLeakproof == true)
        {
            builder.Append("LEAKPROOF\n");
        }

        if (!string.IsNullOrWhiteSpace(signature?.Parallel))
        {
            builder.Append("PARALLEL ").Append(signature!.Parallel!.ToUpperInvariant()).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(signature?.Cost))
        {
            builder.Append("COST ").Append(signature!.Cost).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(signature?.Rows))
        {
            builder.Append("ROWS ").Append(signature!.Rows).Append('\n');
        }

        // G-205: SUPPORT and TRANSFORM, as written; CREATE OR REPLACE sets both, so one left out is removed.
        if (signature?.RenderSupportClause() is { } support)
        {
            builder.Append(support).Append('\n');
        }

        if (signature?.RenderTransformClause() is { } transform)
        {
            builder.Append(transform).Append('\n');
        }

        foreach (var setting in signature?.Settings ?? [])
        {
            builder.Append(setting.Render()).Append('\n');
        }

        // A SQL-standard body (BEGIN ATOMIC ... END, or RETURN expression) is written as it is: it is SQL, not a string (G-95).
        if (isSqlStandardBody)
        {
            builder.Append(rawBody).Append(';');
            return;
        }

        var delimiter = ResolveBodyDelimiter(rawBody, bodyDelimiter);
        builder
            .Append("AS ")
            .Append(delimiter)
            .Append(rawBody)
            .Append(delimiter);

        // A C function's link symbol follows its library (G-211): AS 'library', 'symbol'.
        if (signature?.LinkSymbol is { } linkSymbol)
        {
            builder.Append(", ").Append(QuoteLiteral(linkSymbol));
        }

        builder.Append(';');
    }

    /// <summary>
    /// Preserves the user's original dollar-quote tag when it is still safe, and otherwise picks a
    /// non-colliding tag so the verbatim body is never corrupted.
    /// </summary>
    private static string ResolveBodyDelimiter(string rawBody, string? bodyDelimiter)
    {
        if (!string.IsNullOrWhiteSpace(bodyDelimiter) && !rawBody.Contains(bodyDelimiter, StringComparison.Ordinal))
        {
            return bodyDelimiter!;
        }

        if (!rawBody.Contains("$$", StringComparison.Ordinal))
        {
            return "$$";
        }

        for (var suffix = 1; ; suffix++)
        {
            var candidate = $"$body{suffix}$";
            if (!rawBody.Contains(candidate, StringComparison.Ordinal))
            {
                return candidate;
            }
        }
    }

    private static string GenerateCreateTrigger(TriggerDefinition trigger)
    {
        // A trigger has no separable body, so the original statement is replayed verbatim.
        if (!string.IsNullOrWhiteSpace(trigger.RawStatement))
        {
            var statement = trigger.RawStatement!.TrimEnd();
            return statement.EndsWith(';') ? statement : statement + ";";
        }

        if (string.IsNullOrWhiteSpace(trigger.FunctionName) || string.IsNullOrWhiteSpace(trigger.TableName))
        {
            return $"-- TODO: Insufficient metadata to emit CREATE TRIGGER for '{trigger.Key}'.";
        }

        var events = trigger.Events.Count == 0 ? "INSERT" : string.Join(" OR ", trigger.Events);
        return $"CREATE TRIGGER {QuoteIdentifier(trigger.Name)}\n{trigger.Timing ?? "AFTER"} {events} ON {FormatQualifiedName(trigger.SchemaName, trigger.TableName!)}\nFOR EACH {(trigger.IsRowLevel ? "ROW" : "STATEMENT")}\nEXECUTE FUNCTION {trigger.FunctionName}();";
    }

    /// <summary>
    /// A clause is emitted only when it differs from what omitting it means: <c>AS PERMISSIVE</c> (the default), <c>FOR ALL</c> (the
    /// default), and <c>TO PUBLIC</c> (the default when <c>TO</c> is omitted) are all left out, matching how the live database
    /// loader reconstructs the same statement from <c>pg_policy</c> (G-76).
    /// </summary>
    private static string GenerateCreatePolicy(PolicyDefinition policy)
    {
        var asClause = policy.IsPermissive ? string.Empty : " AS RESTRICTIVE";
        var forClause = string.Equals(policy.Command, "all", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" FOR {policy.Command.ToUpperInvariant()}";
        var toClause = policy.Roles.Count == 1 && string.Equals(policy.Roles[0], "PUBLIC", StringComparison.Ordinal)
            ? string.Empty
            : $" TO {string.Join(", ", policy.Roles.Select(FormatPolicyRole))}";
        var usingClause = string.IsNullOrWhiteSpace(policy.RawUsingExpression)
            ? string.Empty
            : $" USING ({NormalizeParenthesizedExpression(policy.RawUsingExpression!)})";
        var withCheckClause = string.IsNullOrWhiteSpace(policy.RawWithCheckExpression)
            ? string.Empty
            : $" WITH CHECK ({NormalizeParenthesizedExpression(policy.RawWithCheckExpression!)})";

        return $"CREATE POLICY {QuoteIdentifier(policy.Name)} ON {FormatQualifiedName(policy.SchemaName, policy.TableName ?? "<table>")}{asClause}{forClause}{toClause}{usingClause}{withCheckClause};";
    }

    /// <summary>
    /// <c>CREATE EVENT TRIGGER name ON event [WHEN TAG IN ('...')] EXECUTE FUNCTION f();</c> (G-127), followed by an <c>ALTER EVENT TRIGGER</c> when it
    /// is not enabled in the default way (<c>CREATE</c> has no clause for that). The tags are written as the project gave them: PostgreSQL
    /// upper-cases them itself.
    /// </summary>
    private static string GenerateCreateEventTrigger(EventTriggerDefinition eventTrigger)
    {
        if (string.IsNullOrWhiteSpace(eventTrigger.EventName) || string.IsNullOrWhiteSpace(eventTrigger.FunctionName))
        {
            return $"-- TODO: Cannot emit CREATE EVENT TRIGGER for '{eventTrigger.Key}' without its event and function.";
        }

        var tagClause = eventTrigger.Tags.Count == 0
            ? string.Empty
            : $"\n    WHEN TAG IN ({string.Join(", ", eventTrigger.Tags.Select(QuoteStringLiteral))})";
        var function = string.Join('.', eventTrigger.FunctionName.Split('.').Select(QuoteIdentifier));
        var sql = $"CREATE EVENT TRIGGER {QuoteIdentifier(eventTrigger.Name)}\n    ON {eventTrigger.EventName}{tagClause}\n    EXECUTE FUNCTION {function}();";

        return eventTrigger.EnabledMode.ToUpperInvariant() switch
        {
            "D" => $"{sql}\nALTER EVENT TRIGGER {QuoteIdentifier(eventTrigger.Name)} DISABLE;",
            "R" => $"{sql}\nALTER EVENT TRIGGER {QuoteIdentifier(eventTrigger.Name)} ENABLE REPLICA;",
            "A" => $"{sql}\nALTER EVENT TRIGGER {QuoteIdentifier(eventTrigger.Name)} ENABLE ALWAYS;",
            _ => sql,
        };
    }

    /// <summary>
    /// <c>CREATE RULE</c> (G-128): the rule's text as written, since its body is any SQL. <c>OR REPLACE</c> is dropped because a changed rule is dropped and created
    /// again, and a rule that already exists is not this operation's business.
    /// </summary>
    private static string GenerateCreateRule(RuleDefinition rule)
    {
        if (string.IsNullOrWhiteSpace(rule.RawDefinition))
        {
            return $"-- TODO: Cannot emit CREATE RULE for '{rule.Key}' without its definition.";
        }

        var text = System.Text.RegularExpressions.Regex.Replace(rule.RawDefinition.Trim().TrimEnd(';'), @"^\s*CREATE\s+OR\s+REPLACE\s+RULE\b", "CREATE RULE", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text + ";";
    }

    /// <summary>The order the options of an aggregate are written in (G-130); it is the order PostgreSQL's documentation lists them in.</summary>
    private static readonly string[] AggregateOptionOrder =
    [
        "sfunc", "stype", "sspace", "finalfunc", "finalfunc_extra", "finalfunc_modify", "combinefunc", "serialfunc", "deserialfunc", "initcond",
        "msfunc", "minvfunc", "mstype", "msspace", "mfinalfunc", "mfinalfunc_extra", "mfinalfunc_modify", "minitcond", "sortop", "parallel", "hypothetical",
    ];

    /// <summary>
    /// <c>CREATE AGGREGATE schema.name (arguments) (SFUNC = ..., STYPE = ..., ...);</c> (G-130). The argument and state types are written the way a column's type is
    /// (<see cref="FormatCastType"/>), the functions quoted, the initial values as string literals, and the sort operator as <c>OPERATOR(schema.name)</c>.
    /// </summary>
    private static string GenerateCreateAggregate(AggregateDefinition aggregate)
    {
        if (!aggregate.Options.ContainsKey("sfunc") || !aggregate.Options.ContainsKey("stype"))
        {
            return $"-- TODO: Cannot emit CREATE AGGREGATE for '{aggregate.Key}' without its state function and state type.";
        }

        var options = new List<string>();
        foreach (var name in AggregateOptionOrder.Where(aggregate.Options.ContainsKey))
        {
            var value = aggregate.Options[name];
            options.Add(name.ToUpperInvariant() + name switch
            {
                _ when AggregateOptions.FlagOptions.Contains(name) => string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ? "\0" : string.Empty,
                _ when AggregateOptions.FunctionOptions.Contains(name) => " = " + QuoteDottedName(value),
                _ when AggregateOptions.TypeOptions.Contains(name) => " = " + FormatCastType(value, aggregate.WrittenTypes),
                "initcond" or "minitcond" => " = " + QuoteStringLiteral(value),
                "sortop" => " = OPERATOR(" + FormatOperatorReference(value) + ")",
                _ => " = " + value.ToUpperInvariant(),
            });
        }

        options.RemoveAll(static option => option.EndsWith('\0'));
        return $"CREATE AGGREGATE {FormatAggregateName(aggregate)} ({FormatAggregateArguments(aggregate, withNames: true)}) (\n    {string.Join(",\n    ", options)}\n);";
    }

    /// <summary>
    /// <c>CREATE COLLATION</c> (G-148): the provider, then the locale (an ICU or builtin collation's) or <c>LC_COLLATE</c> and <c>LC_CTYPE</c> (a libc collation's), then
    /// <c>DETERMINISTIC = false</c> and the rules when they are not the defaults.
    /// </summary>
    private static string GenerateCreateCollation(CollationDefinition collation)
    {
        var options = new List<string> { $"PROVIDER = {collation.Provider}" };
        // An ICU collation's locale may be empty (the root locale, how pg_dump writes it before PostgreSQL 16): PostgreSQL 14 and 15 refuse one that states no locale at all (G-298).
        if (!string.IsNullOrWhiteSpace(collation.Locale) || (collation.Locale is not null && !string.Equals(collation.Provider, "libc", StringComparison.OrdinalIgnoreCase)))
        {
            options.Add($"LOCALE = {QuoteLiteral(collation.Locale)}");
        }

        if (!string.IsNullOrWhiteSpace(collation.LcCollate))
        {
            options.Add($"LC_COLLATE = {QuoteLiteral(collation.LcCollate)}");
        }

        if (!string.IsNullOrWhiteSpace(collation.LcCtype))
        {
            options.Add($"LC_CTYPE = {QuoteLiteral(collation.LcCtype)}");
        }

        if (!collation.IsDeterministic)
        {
            options.Add("DETERMINISTIC = false");
        }

        if (!string.IsNullOrWhiteSpace(collation.Rules))
        {
            options.Add($"RULES = {QuoteLiteral(collation.Rules)}");
        }

        return AppendComment($"CREATE COLLATION {FormatCollationName(collation)} (\n    {string.Join(",\n    ", options)}\n);", CommentSqlHelper.GenerateObjectComment("COLLATION", collation.SchemaName, collation.Name, collation.Comment));
    }

    /// <summary>
    /// <c>CREATE STATISTICS</c> (G-150): the kinds when they are not all three, the columns (quoted) and expressions (in parentheses) in the order written, and the table.
    /// </summary>
    private static string GenerateCreateStatistics(StatisticsDefinition statistics)
    {
        var kinds = statistics.NormalizedKinds.SequenceEqual(StatisticsDefinition.AllKinds, StringComparer.Ordinal)
            ? string.Empty
            : $" ({string.Join(", ", statistics.NormalizedKinds)})";
        var elements = statistics.Elements.Select(static element => element.Name is { } column ? QuoteIdentifier(column) : $"({element.RawExpression})");
        var table = string.IsNullOrWhiteSpace(statistics.TableSchemaName) ? QuoteIdentifier(statistics.TableName ?? "<table>") : $"{QuoteIdentifier(statistics.TableSchemaName)}.{QuoteIdentifier(statistics.TableName ?? "<table>")}";
        return AppendComment($"CREATE STATISTICS {FormatStatisticsName(statistics)}{kinds} ON {string.Join(", ", elements)} FROM {table};", CommentSqlHelper.GenerateObjectComment("STATISTICS", statistics.SchemaName, statistics.Name, statistics.Comment));
    }

    internal static string AppendComment(string sql, string? comment) => comment is null ? sql : $"{sql}\n{comment}";

    /// <summary>A statistics object's name as SQL, its schema quoted.</summary>
    internal static string FormatStatisticsName(StatisticsDefinition statistics)
        => string.IsNullOrWhiteSpace(statistics.SchemaName) ? QuoteIdentifier(statistics.Name) : $"{QuoteIdentifier(statistics.SchemaName)}.{QuoteIdentifier(statistics.Name)}";

    /// <summary>A collation's name as SQL, its schema quoted.</summary>
    internal static string FormatCollationName(CollationDefinition collation)
        => string.IsNullOrWhiteSpace(collation.SchemaName) ? QuoteIdentifier(collation.Name) : $"{QuoteIdentifier(collation.SchemaName)}.{QuoteIdentifier(collation.Name)}";

    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string FormatOperatorReference(string operatorName)
    {
        var dot = operatorName.LastIndexOf('.');
        return dot < 0 ? operatorName : $"{QuoteDottedName(operatorName[..dot])}.{operatorName[(dot + 1)..]}";
    }

    /// <summary>An aggregate's name as SQL, its schema quoted.</summary>
    internal static string FormatAggregateName(AggregateDefinition aggregate)
        => string.IsNullOrWhiteSpace(aggregate.SchemaName) ? QuoteIdentifier(aggregate.Name) : $"{QuoteIdentifier(aggregate.SchemaName)}.{QuoteIdentifier(aggregate.Name)}";

    /// <summary>
    /// An aggregate's arguments as <c>CREATE AGGREGATE</c> and <c>DROP AGGREGATE</c> write them: <c>*</c> for none, the types in order (<c>VARIADIC</c> kept), and for an
    /// ordered-set aggregate <c>direct ORDER BY ordered</c>; a hypothetical-set aggregate whose one variadic argument stands for both repeats it after <c>ORDER BY</c>.
    /// </summary>
    internal static string FormatAggregateArguments(AggregateDefinition aggregate, bool withNames = false)
    {
        // CREATE AGGREGATE keeps an argument's name (G-292); the identity statements (DROP, OWNER TO) name an aggregate by its types alone.
        string Format(string argument, int index)
        {
            var name = withNames && index < aggregate.ArgumentNames.Count && aggregate.ArgumentNames[index] is { } argumentName ? QuoteIdentifier(argumentName) + " " : string.Empty;
            return argument.StartsWith("VARIADIC ", StringComparison.Ordinal) ? "VARIADIC " + name + FormatAggregateType(argument["VARIADIC ".Length..]) : name + FormatAggregateType(argument);
        }

        // "any" is a reserved word, so the pseudo-type that a hypothetical-set aggregate takes has to be quoted.
        string FormatAggregateType(string type) => string.Equals(type, "any", StringComparison.OrdinalIgnoreCase) ? "\"any\"" : FormatCastType(type, aggregate.WrittenTypes);

        if (!aggregate.IsOrderedSet)
        {
            return aggregate.Arguments.Count == 0 ? "*" : string.Join(", ", aggregate.Arguments.Select(Format));
        }

        var direct = aggregate.Arguments.Take(aggregate.DirectArgumentCount).Select(Format).ToList();
        var ordered = aggregate.Arguments.Skip(aggregate.DirectArgumentCount).Select((argument, index) => Format(argument, aggregate.DirectArgumentCount + index)).ToList();
        if (ordered.Count == 0 && direct.Count > 0)
        {
            ordered.Add(direct[^1]);
        }

        return $"{string.Join(", ", direct)}{(direct.Count > 0 ? " " : string.Empty)}ORDER BY {string.Join(", ", ordered)}";
    }

    /// <summary>
    /// <c>CREATE PUBLICATION name [FOR ALL TABLES | FOR TABLE ..., TABLES IN SCHEMA ...] [WITH (...)];</c> (G-134). A table is written with its column list and row filter
    /// (<c>"s"."t" ("a", "b") WHERE (a &gt; 0)</c>), a schema as <c>TABLES IN SCHEMA "s"</c>, and the options as they were written.
    /// </summary>
    private static string GenerateCreatePublication(PublicationDefinition publication)
    {
        var sql = $"CREATE PUBLICATION {QuoteIdentifier(publication.Name)}";
        if (publication.IsAllTables)
        {
            sql += "\n    FOR ALL TABLES";
        }
        else if (FormatPublicationMembers(publication) is { Length: > 0 } members)
        {
            sql += "\n    FOR " + members;
        }

        if (publication.Options.Count > 0)
        {
            sql += "\n    WITH (" + string.Join(", ", publication.Options.Select(static option => FormatPublicationOption(option.Key, option.Value))) + ")";
        }

        return sql + ";";
    }

    /// <summary>The tables and schemas of a publication as <c>CREATE</c> and <c>ALTER ... SET</c> list them: <c>TABLE a, b, TABLES IN SCHEMA s</c>; empty when it names neither.</summary>
    private static string FormatPublicationMembers(PublicationDefinition publication)
    {
        var members = new List<string>();
        if (publication.Tables.Count > 0)
        {
            members.Add("TABLE " + string.Join(", ", publication.Tables.Select(FormatPublicationTable)));
        }

        members.AddRange(publication.Schemas.Select(static schema => "TABLES IN SCHEMA " + QuoteIdentifier(schema)));
        return string.Join(", ", members);
    }

    private static string FormatPublicationTable(PublicationTableDefinition table)
    {
        var text = FormatQualifiedName(table.SchemaName, table.TableName);
        if (table.Columns.Count > 0)
        {
            text += " (" + string.Join(", ", table.Columns.Select(QuoteIdentifier)) + ")";
        }

        return string.IsNullOrWhiteSpace(table.RawFilter) ? text : $"{text} WHERE ({table.RawFilter})";
    }

    /// <summary>An option of a publication: the published operations as a string, the rest as the keyword or boolean they are.</summary>
    private static string FormatPublicationOption(string name, string value)
        => name == "publish" ? $"publish = {QuoteStringLiteral(value)}" : $"{name} = {value}";

    /// <summary>
    /// <c>ALTER PUBLICATION</c> for a modified publication (G-134), in place: <c>SET (...)</c> with every option a publication has (the ones that went away back at their
    /// defaults) when an option differs, and <c>SET TABLE ..., TABLES IN SCHEMA ...</c>, which replaces what the publication names, when its tables or schemas differ; when it
    /// names nothing any more, <c>DROP TABLE</c> and <c>DROP TABLES IN SCHEMA</c> instead. <c>FOR ALL TABLES</c> cannot be altered into or out of, so that is a drop and a create.
    /// </summary>
    internal static string GenerateAlterPublication(PublicationDefinition oldPublication, PublicationDefinition newPublication, IReadOnlyList<PgProj.Diff.ModificationDetail> details)
    {
        var name = QuoteIdentifier(newPublication.Name);
        var statements = new List<string>();

        if (details.Any(static detail => detail.PropertyName.StartsWith("Option:", StringComparison.Ordinal)))
        {
            var settings = new List<string>
            {
                FormatPublicationOption("publish", newPublication.Options.GetValueOrDefault("publish") ?? "insert, update, delete, truncate"),
                "publish_via_partition_root = " + (newPublication.NormalizedOptions.ContainsKey("publish_via_partition_root") ? "true" : "false"),
            };
            if (oldPublication.Options.ContainsKey("publish_generated_columns") || newPublication.Options.ContainsKey("publish_generated_columns"))
            {
                settings.Add("publish_generated_columns = " + (newPublication.Options.GetValueOrDefault("publish_generated_columns") ?? "none"));
            }

            statements.Add($"ALTER PUBLICATION {name} SET ({string.Join(", ", settings)});");
        }

        var membersChanged = details.Any(static detail => detail.PropertyName is nameof(PublicationDefinition.Tables) or nameof(PublicationDefinition.Schemas) || detail.PropertyName.StartsWith("Table:", StringComparison.Ordinal));
        if (membersChanged)
        {
            if (newPublication.Tables.Count == 0 && newPublication.Schemas.Count == 0)
            {
                if (oldPublication.Tables.Count > 0)
                {
                    statements.Add($"ALTER PUBLICATION {name} DROP TABLE {string.Join(", ", oldPublication.Tables.Select(static table => FormatQualifiedName(table.SchemaName, table.TableName)))};");
                }

                if (oldPublication.Schemas.Count > 0)
                {
                    statements.Add($"ALTER PUBLICATION {name} DROP TABLES IN SCHEMA {string.Join(", ", oldPublication.Schemas.Select(QuoteIdentifier))};");
                }
            }
            else
            {
                statements.Add($"ALTER PUBLICATION {name} SET {FormatPublicationMembers(newPublication)};");
            }
        }

        return string.Join("\n", statements);
    }

    /// <summary>
    /// <c>CREATE OPERATOR schema.name (LEFTARG = ..., RIGHTARG = ..., FUNCTION = ..., COMMUTATOR = ..., NEGATOR = ..., RESTRICT = ..., JOIN = ..., HASHES, MERGES);</c>
    /// (G-129). The operand types are written the way a column's type is (<see cref="FormatCastType"/>), a function or estimator quoted, and a commutator or
    /// negator as <c>OPERATOR(schema.name)</c>, the one form that names a schema. The operator's name is symbols, so it is never quoted.
    /// </summary>
    private static string GenerateCreateOperator(OperatorDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.OperatorName) || string.IsNullOrWhiteSpace(definition.FunctionName))
        {
            return $"-- TODO: Cannot emit CREATE OPERATOR for '{definition.Key}' without its name and function.";
        }

        var options = new List<string>();
        if (!string.IsNullOrWhiteSpace(definition.LeftType))
        {
            options.Add($"LEFTARG = {FormatCastType(definition.LeftType, definition.WrittenTypes)}");
        }

        if (!string.IsNullOrWhiteSpace(definition.RightType))
        {
            options.Add($"RIGHTARG = {FormatCastType(definition.RightType, definition.WrittenTypes)}");
        }

        options.Add($"FUNCTION = {QuoteDottedName(definition.FunctionName)}");
        AddOperatorReference(options, "COMMUTATOR", definition.Commutator);
        AddOperatorReference(options, "NEGATOR", definition.Negator);
        if (!string.IsNullOrWhiteSpace(definition.RestrictFunction))
        {
            options.Add($"RESTRICT = {QuoteDottedName(definition.RestrictFunction)}");
        }

        if (!string.IsNullOrWhiteSpace(definition.JoinFunction))
        {
            options.Add($"JOIN = {QuoteDottedName(definition.JoinFunction)}");
        }

        if (definition.IsHashes)
        {
            options.Add("HASHES");
        }

        if (definition.IsMerges)
        {
            options.Add("MERGES");
        }

        return $"CREATE OPERATOR {FormatOperatorName(definition)} (\n    {string.Join(",\n    ", options)}\n);";
    }

    private static void AddOperatorReference(List<string> options, string option, string? operatorName)
    {
        if (string.IsNullOrWhiteSpace(operatorName))
        {
            return;
        }

        var dot = operatorName.LastIndexOf('.');
        options.Add(dot < 0
            ? $"{option} = OPERATOR({operatorName})"
            : $"{option} = OPERATOR({QuoteDottedName(operatorName[..dot])}.{operatorName[(dot + 1)..]})");
    }

    private static string QuoteDottedName(string name) => string.Join('.', name.Split('.').Select(QuoteIdentifier));

    /// <summary>An operator's name as SQL: its schema quoted, its symbols not.</summary>
    internal static string FormatOperatorName(OperatorDefinition definition)
        => string.IsNullOrWhiteSpace(definition.SchemaName) ? definition.OperatorName : $"{QuoteIdentifier(definition.SchemaName)}.{definition.OperatorName}";

    /// <summary>An operator's operand list as <c>DROP OPERATOR</c> writes it: <c>NONE</c> for a missing side.</summary>
    internal static string FormatOperatorOperands(OperatorDefinition definition)
        => $"{(string.IsNullOrWhiteSpace(definition.LeftType) ? "NONE" : FormatCastType(definition.LeftType, definition.WrittenTypes))}, {(string.IsNullOrWhiteSpace(definition.RightType) ? "NONE" : FormatCastType(definition.RightType, definition.WrittenTypes))}";

    /// <summary>
    /// <c>CREATE CAST (source AS target) WITH FUNCTION f(args) | WITHOUT FUNCTION | WITH INOUT [AS ASSIGNMENT | AS IMPLICIT];</c> (G-131), the types
    /// and the function written the way a column's type is (<see cref="BuildTypeSql"/>): SQL-standard names for the built-in ones, quoted where needed.
    /// </summary>
    private static string GenerateCreateCast(CastDefinition cast)
    {
        if (string.IsNullOrWhiteSpace(cast.SourceType) || string.IsNullOrWhiteSpace(cast.TargetType))
        {
            return $"-- TODO: Cannot emit CREATE CAST for '{cast.Key}' without its source and target types.";
        }

        string method;
        if (string.Equals(cast.Method, "inout", StringComparison.OrdinalIgnoreCase))
        {
            method = "WITH INOUT";
        }
        else if (string.Equals(cast.Method, "binary", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(cast.FunctionName))
        {
            method = "WITHOUT FUNCTION";
        }
        else
        {
            var function = string.Join('.', cast.FunctionName.Split('.').Select(QuoteIdentifier));
            method = $"WITH FUNCTION {function}({string.Join(", ", cast.FunctionArguments.Select(argument => FormatCastType(argument, cast.WrittenTypes)))})";
        }

        var context = cast.Context.ToLowerInvariant() switch
        {
            "implicit" => " AS IMPLICIT",
            "assignment" => " AS ASSIGNMENT",
            _ => string.Empty,
        };

        return $"CREATE CAST ({FormatCastType(cast.SourceType, cast.WrittenTypes)} AS {FormatCastType(cast.TargetType, cast.WrittenTypes)})\n    {method}{context};";
    }

    /// <summary>A cast's type as SQL: <c>[]</c> for each array bound, and the rest as a column's type is written.</summary>
    /// <summary>
    /// A cast's, operator's, or aggregate's type as the statement wrote it (<paramref name="written"/>, G-207), or
    /// <see cref="FormatCastType(string?)"/>'s spelling of the normalized name when it was not read.
    /// </summary>
    internal static string FormatCastType(string? type, IReadOnlyDictionary<string, string> written)
    {
        var key = type?.Trim();
        if (key is not null && key.StartsWith("VARIADIC ", StringComparison.OrdinalIgnoreCase))
        {
            return "VARIADIC " + FormatCastType(key["VARIADIC ".Length..], written);
        }

        return key is not null && written.TryGetValue(key, out var text) ? text : FormatCastType(type);
    }

    internal static string FormatCastType(string? type)
    {
        var text = (type ?? "text").Trim();
        var dimensions = 0;
        while (text.EndsWith("[]", StringComparison.Ordinal))
        {
            dimensions++;
            text = text[..^2];
        }

        return BuildTypeSql(text, [], dimensions, null);
    }

    private static string FormatPolicyRole(string role)
        => string.Equals(role, "PUBLIC", StringComparison.Ordinal) ? "PUBLIC" : QuoteIdentifier(role);

    private static string GenerateCreateType(TypeDefinition type)
    {
        if (IsExtension(type))
        {
            // An extension is created as the project wrote it (G-213): IF NOT EXISTS, SCHEMA, VERSION, and CASCADE as written. The statement is built only
            // when there is no text, for a model made in code.
            var create = type.ExtensionWrittenText is { } written
                ? written + ";"
                : $"CREATE EXTENSION {QuoteIdentifier(type.Name)}"
                    + (type.ExtensionSchemaStated && !string.IsNullOrWhiteSpace(type.SchemaName) ? $" SCHEMA {QuoteIdentifier(type.SchemaName)}" : string.Empty)
                    + (type.ExtensionVersion is { } version ? $" VERSION {CommentSqlHelper.QuoteLiteral(version)}" : string.Empty)
                    + ";";

            // A stated comment replaces the one the extension comes with, IS NULL included; an unstated one leaves it.
            return type.CommentStated ? create + "\n" + CommentSqlHelper.GenerateExtensionComment(type.Name, type.Comment) : create;
        }

        var qualifiedName = FormatQualifiedName(type.SchemaName, type.Name);

        if (IsDomain(type))
        {
            return AppendTypeComment(type, GenerateCreateDomain(type, qualifiedName));
        }

        // A composite type with no fields is deployed too (G-268).
        if (type.CompositeFields.Count > 0 || type.IsComposite)
        {
            var fields = string.Join(",\n", type.CompositeFields.Select(field =>
                $"    {QuoteIdentifier(field.Name)} {FormatCompositeFieldType(field)}"));
            return AppendTypeComment(type, $"CREATE TYPE {qualifiedName} AS (\n{fields}\n);");
        }

        if (type.RangeOptions is { } range)
        {
            var options = new System.Text.StringBuilder()
                .Append("SUBTYPE = ").Append(range.Subtype ?? "<subtype>");

            if (!string.IsNullOrWhiteSpace(range.SubtypeOperatorClass))
            {
                options.Append(", SUBTYPE_OPCLASS = ").Append(range.SubtypeOperatorClass);
            }

            if (!string.IsNullOrWhiteSpace(range.CollationName))
            {
                options.Append(", COLLATION = ").Append(range.CollationName);
            }

            if (!string.IsNullOrWhiteSpace(range.CanonicalFunction))
            {
                options.Append(", CANONICAL = ").Append(range.CanonicalFunction);
            }

            if (!string.IsNullOrWhiteSpace(range.SubtypeDiffFunction))
            {
                options.Append(", SUBTYPE_DIFF = ").Append(range.SubtypeDiffFunction);
            }

            if (!string.IsNullOrWhiteSpace(range.MultirangeTypeName))
            {
                options.Append(", MULTIRANGE_TYPE_NAME = ").Append(range.MultirangeTypeName);
            }

            return AppendTypeComment(type, $"CREATE TYPE {qualifiedName} AS RANGE ({options});");
        }

        if (type.EnumLabels.Count > 0 || type.IsEnum)
        {
            var labels = string.Join(", ", type.EnumLabels.Select(QuoteStringLiteral));
            return AppendTypeComment(type, $"CREATE TYPE {qualifiedName} AS ENUM ({labels});");
        }

        // Enum types are not currently modeled structurally; emit a clearly-marked TODO rather
        // than inventing behavior (G-13).
        return $"-- TODO: Generate CREATE TYPE SQL for '{type.Key}'. Enum types are not yet captured structurally.";
    }

    private static string AppendTypeComment(TypeDefinition type, string sql)
    {
        var typeComment = CommentSqlHelper.GenerateTypeComment(type.SchemaName, type.Name, type.Comment);

        // A composite type's fields' comments (G-216).
        var fieldComments = CommentSqlHelper.GenerateColumnCommentChanges(type.SchemaName, type.Name, ColumnCommentMap.Empty, type.ColumnComments);
        return string.Join("\n", new[] { sql, typeComment }.OfType<string>().Concat(fieldComments));
    }

    /// <summary>
    /// <c>CREATE DOMAIN name AS type [DEFAULT expr] [CONSTRAINT name CHECK (expr)]... [NOT NULL];</c> (G-77). A
    /// CHECK constraint's name is always emitted explicitly, named or not (<see cref="DomainCheckConstraintDefinition.IsNameGenerated"/>
    /// carries no SQL-generation meaning of its own): PostgreSQL would assign the exact same name to an unnamed one
    /// deployed fresh, so this changes nothing, and it keeps every deployed constraint addressable by name for a
    /// later <c>ALTER DOMAIN ... DROP CONSTRAINT</c> (<see cref="AlterOperation"/>).
    /// </summary>
    /// <summary>A table's key (<c>schema.name</c>, as <see cref="DatabaseModel.CreateKey"/> makes it) as a quoted qualified name (G-194).</summary>
    internal static string FormatTableKey(string key)
    {
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? QuoteIdentifier(key) : FormatQualifiedName(key[..dot], key[(dot + 1)..]);
    }

    /// <summary>A composite field's type, with its <c>COLLATE</c> when it states one (G-192), as <c>CREATE TYPE</c> and <c>ALTER TYPE ... ATTRIBUTE</c> write it.</summary>
    internal static string FormatCompositeFieldType(CompositeTypeFieldDefinition field)
    {
        // The field's type as written (G-207); the normalized name is the fallback.
        var type = field.RawType ?? SqlIdentifiers.QuoteTypeName(field.DataType ?? "<type>");
        return string.IsNullOrWhiteSpace(field.CollationName) ? type : $"{type} COLLATE {QuoteDottedName(field.CollationName)}";
    }

    private static string GenerateCreateDomain(TypeDefinition type, string qualifiedName)
    {
        var builder = new System.Text.StringBuilder()
            .Append("CREATE DOMAIN ").Append(qualifiedName)
            .Append(" AS ").Append(type.DomainWrittenBaseType ?? BuildTypeSql(type.DomainBaseType, type.DomainBaseTypeModifiers, type.DomainBaseTypeArrayDimensions, type.DomainIntervalFields));

        if (!string.IsNullOrWhiteSpace(type.DomainCollationName))
        {
            builder.Append(" COLLATE ").Append(QuoteDottedName(type.DomainCollationName));
        }

        if (!string.IsNullOrWhiteSpace(type.DomainRawDefaultExpression))
        {
            builder.Append(" DEFAULT ").Append(type.DomainRawDefaultExpression);
        }

        foreach (var check in type.DomainCheckConstraints.Where(static check => !check.IsNotValid))
        {
            builder.Append(" CONSTRAINT ").Append(QuoteIdentifier(check.Name ?? "<check>")).Append(" CHECK (").Append(check.RawExpression).Append(')');
        }

        if (type.DomainIsNotNull)
        {
            builder.Append(" NOT NULL");
        }

        builder.Append(';');

        // CREATE DOMAIN has no NOT VALID: a check written so is added after the domain exists (G-233).
        foreach (var check in type.DomainCheckConstraints.Where(static check => check.IsNotValid))
        {
            builder.Append("\nALTER DOMAIN ").Append(qualifiedName).Append(" ADD CONSTRAINT ").Append(QuoteIdentifier(check.Name ?? "<check>"))
                .Append(" CHECK (").Append(check.RawExpression).Append(") NOT VALID;");
        }

        return builder.ToString();
    }

    private static string QuoteStringLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>The statements that follow an index or key constraint that the table is clustered on or replicates by (G-255, G-256), each on a line of its own; empty when there are none.</summary>
    internal static string IndexFlagStatements(string? schemaName, string? tableName, string indexName, bool isClustered, bool isReplicaIdentity)
    {
        var table = tableName ?? "<table>";
        return (isClustered ? "\n" + CommentSqlHelper.GenerateClusterOn(schemaName, table, indexName) : string.Empty)
            + (isReplicaIdentity ? "\n" + CommentSqlHelper.GenerateReplicaIdentityUsingIndex(schemaName, table, indexName) : string.Empty);
    }

    private static string GenerateCreateIndex(IndexDefinition index, bool onlyParent)
    {
        var indexSql = CreateIndexStatement(index, onlyParent: onlyParent);
        var indexComment = CommentSqlHelper.GenerateIndexComment(index.SchemaName, index.Name, index.Comment);
        return indexComment is null ? indexSql : $"{indexSql}\n{indexComment}";
    }

    /// <summary>
    /// The <c>CREATE INDEX</c> statement of <paramref name="index"/>, without its comment. <c>publish --concurrently</c> (G-271) builds it <paramref name="concurrently"/>, on a
    /// partition (<paramref name="tableName"/>) or under another name (<paramref name="name"/>), or <c>ON ONLY</c> a partitioned parent (<paramref name="onlyParent"/>).
    /// </summary>
    internal static string CreateIndexStatement(IndexDefinition index, string? tableName = null, string? name = null, bool concurrently = false, bool onlyParent = false)
    {
        var keyElementSql = index.KeyElements.Count > 0
            ? string.Join(", ", index.KeyElements.Select(FormatIndexKeyElement))
            : index.KeyColumns.Count > 0
                ? string.Join(", ", index.KeyColumns.Select(QuoteIdentifier))
                : null;

        if (keyElementSql is null)
        {
            return $"CREATE {(index.IsUnique ? "UNIQUE " : string.Empty)}INDEX {QuoteIdentifier(index.Name)} ON {FormatQualifiedName(index.SchemaName, index.TableName ?? "<table>")} (\n    -- TODO: Add index expression\n);";
        }

        var accessMethodClause = string.IsNullOrWhiteSpace(index.AccessMethod) ? string.Empty : $" USING {index.AccessMethod}";
        var includeClause = index.IncludeColumns.Count == 0
            ? string.Empty
            : $" INCLUDE ({string.Join(", ", index.IncludeColumns.Select(QuoteIdentifier))})";
        var whereClause = string.IsNullOrWhiteSpace(index.RawPredicateExpression)
            ? string.Empty
            : $" WHERE {NormalizeParenthesizedExpression(index.RawPredicateExpression!)}";

        // An index written without a name is deployed so, as written; PostgreSQL gives it the name the model has (G-225).
        var indexName = name is not null ? $"{QuoteIdentifier(name)} " : index.IsNameGenerated ? string.Empty : $"{QuoteIdentifier(index.Name)} ";
        var nullsClause = index.NullsNotDistinct ? " NULLS NOT DISTINCT" : string.Empty;
        var concurrentlyClause = concurrently ? "CONCURRENTLY " : string.Empty;
        var onlyClause = onlyParent ? "ONLY " : string.Empty;
        return $"CREATE {(index.IsUnique ? "UNIQUE " : string.Empty)}INDEX {concurrentlyClause}{indexName}ON {onlyClause}{FormatQualifiedName(index.SchemaName, tableName ?? index.TableName ?? "<table>")}{accessMethodClause} ({keyElementSql}){includeClause}{nullsClause}{BuildIndexStorageClause(index.StorageOptions)}{whereClause};";
    }

    private static string FormatIndexKeyElement(IndexKeyElementDefinition element)
    {
        var expression = !string.IsNullOrWhiteSpace(element.Name)
            ? QuoteIdentifier(element.Name)
            : !string.IsNullOrWhiteSpace(element.RawExpression)
                ? $"({NormalizeParenthesizedExpression(element.RawExpression!)})"
                : "<expr>";

        var collationClause = string.IsNullOrWhiteSpace(element.CollationName) ? string.Empty : $" COLLATE {QuoteDottedName(element.CollationName)}";

        var opclassClause = string.Empty;
        if (!string.IsNullOrWhiteSpace(element.OperatorClassName))
        {
            opclassClause = $" {element.OperatorClassName}";
            if (element.OperatorClassOptions.Count > 0)
            {
                opclassClause += $" ({string.Join(", ", element.OperatorClassOptions)})";
            }
        }

        var directionClause = element.IsDescending ? " DESC" : string.Empty;
        var nullsClause = element.NullsFirst switch
        {
            true => " NULLS FIRST",
            false => " NULLS LAST",
            null => string.Empty,
        };

        return $"{expression}{collationClause}{opclassClause}{directionClause}{nullsClause}";
    }

    private static string GenerateCreateConstraint(ConstraintDefinition constraint)
    {
        var alterPrefix = $"ALTER TABLE {FormatQualifiedName(constraint.SchemaName, constraint.TableName ?? "<table>")}\nADD CONSTRAINT {QuoteIdentifier(constraint.Name)}";
        var definition = BuildConstraintDefinitionSql(constraint);

        if (definition is null)
        {
            return $"{alterPrefix}\n-- TODO: Add constraint expression;";
        }

        var deferrableClause = BuildDeferrableClause(constraint);
        return $"{alterPrefix}\n{definition}{deferrableClause}{BuildEnforcedClause(constraint)}{BuildNotValidClause(constraint)};";
    }

    /// <summary><c> NOT ENFORCED</c> for a CHECK or FOREIGN KEY constraint that is not enforced (PostgreSQL 18, G-121); nothing otherwise.</summary>
    private static string BuildEnforcedClause(ConstraintDefinition constraint)
        => !constraint.IsEnforced && constraint.ConstraintType?.Trim().ToUpperInvariant() is "CHECK" or "FOREIGN KEY"
            ? " NOT ENFORCED"
            : string.Empty;

    /// <summary><c> NOT VALID</c> for a CHECK or FOREIGN KEY constraint written so (G-234); nothing otherwise.</summary>
    internal static string BuildNotValidClause(ConstraintDefinition constraint)
        => constraint.IsNotValid && constraint.IsEnforced && constraint.ConstraintType?.Trim().ToUpperInvariant() is "CHECK" or "FOREIGN KEY"
            ? " NOT VALID"
            : string.Empty;

    private static string BuildDeferrableClause(ConstraintDefinition constraint)
    {
        if (!constraint.IsDeferrable)
        {
            return string.Empty;
        }

        var initialTiming = constraint.IsInitiallyDeferred == true ? " INITIALLY DEFERRED" : " INITIALLY IMMEDIATE";
        return $" DEFERRABLE{initialTiming}";
    }

    /// <summary>An index's <c>WITH (...)</c>, each option as written (G-195); empty when it has none.</summary>
    /// <summary>A key or exclusion constraint's <c>INCLUDE (...)</c>, before its <c>WITH (...)</c> as PostgreSQL writes it (G-200); empty when it has none.</summary>
    internal static string BuildIncludeClause(IReadOnlyList<string> columns)
        => columns.Count == 0 ? string.Empty : $" INCLUDE ({string.Join(", ", columns.Select(QuoteIdentifier))})";

    internal static string BuildIndexStorageClause(IReadOnlyList<TableStorageOptionNode> options)
        => options.Count == 0 ? string.Empty : $" WITH ({string.Join(", ", options.Select(static option => option.RawText.Trim()))})";

    private static string? BuildConstraintDefinitionSql(ConstraintDefinition constraint)
        => constraint.ConstraintType?.Trim().ToUpperInvariant() switch
        {
            "PRIMARY KEY" => constraint.PrimaryKeyColumns.Count == 0
                ? null
                : $"PRIMARY KEY ({FormatKeyColumns(constraint.PrimaryKeyColumns, constraint.WithoutOverlaps ? " WITHOUT OVERLAPS" : null, null)}){BuildIncludeClause(constraint.IncludeColumns)}{BuildIndexStorageClause(constraint.IndexStorageOptions)}",
            "UNIQUE" => constraint.UniqueColumns.Count == 0
                ? null
                : $"UNIQUE{(constraint.NullsNotDistinct ? " NULLS NOT DISTINCT" : string.Empty)} ({FormatKeyColumns(constraint.UniqueColumns, constraint.WithoutOverlaps ? " WITHOUT OVERLAPS" : null, null)}){BuildIncludeClause(constraint.IncludeColumns)}{BuildIndexStorageClause(constraint.IndexStorageOptions)}",
            "FOREIGN KEY" => BuildForeignKeyDefinitionSql(constraint),
            "CHECK" => string.IsNullOrWhiteSpace(constraint.RawCheckExpression)
                ? null
                : $"CHECK ({NormalizeParenthesizedExpression(constraint.RawCheckExpression!)}){(constraint.IsNoInherit ? " NO INHERIT" : string.Empty)}",
            "EXCLUDE" => BuildExclusionDefinitionSql(constraint),
            _ => null,
        };

    private static string? BuildExclusionDefinitionSql(ConstraintDefinition constraint)
    {
        if (constraint.ExclusionElements.Count == 0)
        {
            return null;
        }

        var accessMethodClause = string.IsNullOrWhiteSpace(constraint.ExclusionAccessMethod) ? string.Empty : $" USING {constraint.ExclusionAccessMethod}";
        var elements = string.Join(", ", constraint.ExclusionElements.Select(FormatExclusionElement));
        var whereClause = string.IsNullOrWhiteSpace(constraint.RawExclusionPredicateExpression)
            ? string.Empty
            : $" WHERE ({NormalizeParenthesizedExpression(constraint.RawExclusionPredicateExpression!)})";

        return $"EXCLUDE{accessMethodClause} ({elements}){BuildIncludeClause(constraint.IncludeColumns)}{BuildIndexStorageClause(constraint.IndexStorageOptions)}{whereClause}";
    }

    private static string FormatExclusionElement(ConstraintExclusionElementDefinition element)
    {
        var expression = !string.IsNullOrWhiteSpace(element.Name)
            ? QuoteIdentifier(element.Name)
            : !string.IsNullOrWhiteSpace(element.RawExpression)
                ? $"({NormalizeParenthesizedExpression(element.RawExpression!)})"
                : "<expr>";

        return $"{expression} WITH {element.OperatorName}";
    }

    /// <summary>
    /// The quoted key columns, comma separated. The last one is followed by <paramref name="lastSuffix"/> (a temporal key's
    /// <c> WITHOUT OVERLAPS</c>) or preceded by <paramref name="lastPrefix"/> (a temporal foreign key's <c>PERIOD </c>), PostgreSQL 18 (G-121).
    /// </summary>
    private static string FormatKeyColumns(IReadOnlyList<string> columns, string? lastSuffix, string? lastPrefix)
        => string.Join(", ", columns.Select((column, index) => index == columns.Count - 1
            ? $"{lastPrefix}{QuoteIdentifier(column)}{lastSuffix}"
            : QuoteIdentifier(column)));

    private static string? BuildForeignKeyDefinitionSql(ConstraintDefinition constraint)
    {
        if (constraint.ForeignKeyColumns.Count == 0
            || string.IsNullOrWhiteSpace(constraint.ForeignKeyReferencedTable)
            || constraint.ForeignKeyReferencedColumns.Count == 0)
        {
            return null;
        }

        var localColumns = FormatKeyColumns(constraint.ForeignKeyColumns, null, constraint.ForeignKeyUsesPeriod ? "PERIOD " : null);
        var referencedTable = FormatQualifiedName(constraint.ForeignKeyReferencedSchemaName, constraint.ForeignKeyReferencedTable!);
        var referencedColumns = FormatKeyColumns(constraint.ForeignKeyReferencedColumns, null, constraint.ForeignKeyUsesPeriod ? "PERIOD " : null);

        var builder = new System.Text.StringBuilder()
            .Append("FOREIGN KEY (").Append(localColumns).Append(')')
            .Append(" REFERENCES ").Append(referencedTable)
            .Append(" (").Append(referencedColumns).Append(')');

        // MATCH comes before the ON DELETE / ON UPDATE actions in PostgreSQL's grammar (G-182).
        var matchType = TryFormatForeignKeyMatchType(constraint.ForeignKeyMatchType);
        if (matchType is not null)
        {
            builder.Append(" MATCH ").Append(matchType);
        }

        var updateAction = TryFormatForeignKeyAction(constraint.ForeignKeyUpdateAction);
        if (updateAction is not null)
        {
            builder.Append(" ON UPDATE ").Append(updateAction);
        }

        var deleteAction = TryFormatForeignKeyAction(constraint.ForeignKeyDeleteAction);
        if (deleteAction is not null)
        {
            builder.Append(" ON DELETE ").Append(deleteAction);

            if (constraint.ForeignKeyDeleteSetColumns.Count > 0 && constraint.ForeignKeyDeleteAction is "n" or "d")
            {
                builder.Append(" (").Append(string.Join(", ", constraint.ForeignKeyDeleteSetColumns.Select(QuoteIdentifier))).Append(')');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Translates a pg_query FK action code into its SQL keyword. Returns null for the implicit
    /// default (NO ACTION / unspecified) so unremarkable constraints don't emit a redundant clause.
    /// </summary>
    private static string? TryFormatForeignKeyAction(string? actionCode)
        => actionCode switch
        {
            "r" => "RESTRICT",
            "c" => "CASCADE",
            "n" => "SET NULL",
            "d" => "SET DEFAULT",
            _ => null, // "a" (NO ACTION) and null/unknown codes are the implicit default.
        };

    /// <summary>
    /// Translates a pg_query FK match type code into its SQL keyword. Returns null for the
    /// implicit default (MATCH SIMPLE) so unremarkable constraints don't emit a redundant clause.
    /// </summary>
    private static string? TryFormatForeignKeyMatchType(string? matchTypeCode)
        => matchTypeCode switch
        {
            "f" => "FULL",
            "p" => "PARTIAL",
            _ => null, // "s" (MATCH SIMPLE) and null/unknown codes are the implicit default.
        };

    private static string NormalizeParenthesizedExpression(string expression)
    {
        var trimmed = expression.Trim();
        if (!(trimmed.StartsWith('(') && trimmed.EndsWith(')') && FirstParenthesisClosesAtEnd(trimmed)))
        {
            return trimmed;
        }

        // A subquery's parentheses are its own, not the clause's: "(SELECT ...)" stays as it is, so the clause wraps it again (G-238).
        var inner = trimmed[1..^1].Trim();
        return IsSubquery(inner) ? trimmed : inner;
    }

    private static bool IsSubquery(string text)
        => System.Text.RegularExpressions.Regex.IsMatch(text, @"^(SELECT|WITH|VALUES|TABLE)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // "(a > 0) AND (b > 0)" starts with "(" and ends with ")" but the outer pair is not one group; stripping it changes the meaning (G-73).
    // Parentheses inside a string literal or a quoted name do not count.
    private static bool FirstParenthesisClosesAtEnd(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\'' or '"':
                    var quote = text[i];
                    i++;
                    while (i < text.Length && (text[i] != quote || (i + 1 < text.Length && text[i + 1] == quote && ++i > 0)))
                    {
                        i++;
                    }

                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return i == text.Length - 1;
                    }

                    break;
            }
        }

        return false;
    }

    private static string GenerateCreateSequence(SequenceDefinition sequence)
    {
        if (sequence.IsIdentityOwned)
        {
            // PostgreSQL creates the backing sequence automatically as part of
            // `GENERATED ... AS IDENTITY` column DDL (see GenerateCreateTable); an explicit
            // CREATE SEQUENCE here would either fail (sequence already exists) or race the
            // owning table's creation.
            return $"-- Sequence {FormatQualifiedName(sequence.SchemaName, sequence.Name)} is created implicitly by its identity column; no explicit CREATE SEQUENCE required.";
        }

        var builder = new System.Text.StringBuilder()
            .Append(sequence.IsUnlogged ? "CREATE UNLOGGED SEQUENCE " : "CREATE SEQUENCE ")
            .Append(FormatQualifiedName(sequence.SchemaName, sequence.Name));

        if (!string.IsNullOrWhiteSpace(sequence.DataType))
        {
            builder.Append(" AS ").Append(FormatFriendlyTypeName(sequence.DataType));
        }

        if (!string.IsNullOrWhiteSpace(sequence.IncrementBy))
        {
            builder.Append(" INCREMENT BY ").Append(sequence.IncrementBy);
        }

        if (!string.IsNullOrWhiteSpace(sequence.MinValue))
        {
            builder.Append(" MINVALUE ").Append(sequence.MinValue);
        }

        if (!string.IsNullOrWhiteSpace(sequence.MaxValue))
        {
            builder.Append(" MAXVALUE ").Append(sequence.MaxValue);
        }

        if (!string.IsNullOrWhiteSpace(sequence.StartValue))
        {
            builder.Append(" START WITH ").Append(sequence.StartValue);
        }

        if (!string.IsNullOrWhiteSpace(sequence.CacheSize))
        {
            builder.Append(" CACHE ").Append(sequence.CacheSize);
        }

        if (sequence.Cycle == true)
        {
            builder.Append(" CYCLE");
        }

        if (!string.IsNullOrWhiteSpace(sequence.OwnedByTable) && !string.IsNullOrWhiteSpace(sequence.OwnedByColumn))
        {
            builder
                .Append(" OWNED BY ")
                .Append(sequence.OwnedByTable)
                .Append('.')
                .Append(QuoteIdentifier(sequence.OwnedByColumn));
        }

        builder.Append(';');

        var sequenceComment = CommentSqlHelper.GenerateSequenceComment(sequence.SchemaName, sequence.Name, sequence.Comment);
        if (sequenceComment is not null)
        {
            builder.Append('\n').Append(sequenceComment);
        }

        return builder.ToString();
    }

    private static string GenerateGrant(GrantDefinition grant) => GrantSqlBuilder.Grant(grant);

    private static string FormatQualifiedName(string? schemaName, string name)
        => string.IsNullOrWhiteSpace(schemaName)
            ? QuoteIdentifier(name)
            : $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(name)}";

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
