using System.Globalization;
using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    private static TableNode? TryCreateTableNode(JsonElement element, string statementSql, int statementOffset)
    {
        if (!TryGetRelationIdentity(element, "relation", out var tableName, out var schemaName))
        {
            return null;
        }

        var columns = ExtractColumns(element, tableName, schemaName, statementSql);
        var isUnlogged = TryGetDirectProperty(element, "relation", out var relationElement)
            && string.Equals(TryGetDirectStringProperty(relationElement, "relpersistence"), "u", StringComparison.Ordinal);

        var (partitionStrategy, partitionKeyElements) = ExtractPartitionSpec(element, statementSql, statementOffset);
        var (partitionOfSchemaName, partitionOfTableName, partitionBoundRaw) = ExtractPartitionBound(element, statementSql, statementOffset);
        string? ofTypeName = null;
        string? ofTypeSchemaName = null;
        if (TryGetDirectProperty(element, "ofTypename", out var ofType))
        {
            var ofParts = TryGetStringListFromDirectProperty(ofType, "names");
            ofTypeName = ofParts.Count > 0 ? ofParts[^1] : null;
            ofTypeSchemaName = ofParts.Count > 1 ? string.Join('.', ofParts.Take(ofParts.Count - 1)) : null;
        }

        // A partition's element list holds options for the columns it takes from its parent, not columns (G-266): a default and a NOT NULL are applied to the inherited
        // column by the semantic layer, as a child's own default and NOT NULL are (G-237, G-253), and the list is deployed as written.
        IReadOnlyList<InheritedColumnDefault> partitionDefaults = [];
        IReadOnlyList<InheritedColumnNotNull> partitionNotNulls = [];
        string? partitionElementsText = null;
        if (partitionOfTableName is not null)
        {
            var options = columns.Where(static column => column.DataType is null).ToArray();
            columns = columns.Where(static column => column.DataType is not null).ToArray();
            partitionDefaults = options
                .Where(static column => column.RawDefaultExpression is not null)
                .Select(static column => new InheritedColumnDefault(column.Name!, column.RawDefaultExpression, column.DefaultExpressionAst) { IsInPartitionOfList = true })
                .ToArray();
            partitionNotNulls = options
                .Where(static column => column.IsNullable == false)
                .Select(static column => new InheritedColumnNotNull(column.Name!, column.NotNullName, column.NotNullNoInherit))
                .ToArray();
            partitionElementsText = ReadPartitionElementsText(element, statementSql, statementOffset);
        }

        return new TableNode(tableName, schemaName, columns)
        {
            InheritedColumnDefaults = partitionDefaults,
            InheritedColumnNotNulls = partitionNotNulls,
            PartitionElementsText = partitionElementsText,
            IsUnlogged = isUnlogged,
            TablespaceName = TryGetDirectStringProperty(element, "tablespacename"),
            AccessMethod = TryGetDirectStringProperty(element, "accessMethod"),
            StorageOptions = ExtractStorageOptions(element, statementSql, statementOffset),
            PartitionStrategy = partitionStrategy,
            PartitionKeyElements = partitionKeyElements,
            PartitionOfSchemaName = partitionOfSchemaName,
            PartitionOfTableName = partitionOfTableName,
            PartitionBoundRaw = partitionBoundRaw,
            OfTypeName = ofTypeName,
            OfTypeSchemaName = ofTypeSchemaName,
            InheritsFrom = ExtractInheritsFrom(element),
            LikeClauses = ExtractLikeClauses(element, statementSql, statementOffset),
        };
    }

    /// <summary>
    /// The element list of <c>PARTITION OF parent (...)</c> as written, parentheses included, or null when the statement has none (G-266). The list has no position of its own
    /// in the tree: it is the parenthesized group right after the parent's name, before <c>FOR VALUES</c> or <c>DEFAULT</c>.
    /// </summary>
    private static string? ReadPartitionElementsText(JsonElement element, string statementSql, int statementOffset)
    {
        if (!TryGetDirectProperty(element, "tableElts", out var elements) || elements.ValueKind != JsonValueKind.Array || elements.GetArrayLength() == 0
            || !TryGetDirectProperty(element, "inhRelations", out var relations) || relations.ValueKind != JsonValueKind.Array
            || relations.EnumerateArray().FirstOrDefault() is not { ValueKind: JsonValueKind.Object } parent
            || !TryGetDirectProperty(parent, "RangeVar", out var parentRelation)
            || !TryGetDirectInt32Property(parentRelation, "location", out var parentLocation))
        {
            return null;
        }

        var start = parentLocation - statementOffset;
        if (start < 0 || start >= statementSql.Length || SourceText.Scan(statementSql[start..]) is not { } tokens)
        {
            return null;
        }

        var text = statementSql[start..];
        var open = -1;
        var depth = 0;
        foreach (var token in tokens)
        {
            var value = text[token.Start..token.End];
            if (open < 0)
            {
                if (value == "(")
                {
                    open = token.Start;
                    depth = 1;
                }
                else if (value.Equals("FOR", StringComparison.OrdinalIgnoreCase) || value.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                continue;
            }

            depth += value == "(" ? 1 : value == ")" ? -1 : 0;
            if (depth == 0)
            {
                return text[open..token.End];
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>LIKE</c> clauses of the column list (G-221), each with the number of the table's own columns written before it. The clause has no position of
    /// its own in the tree, so its text runs from the <c>LIKE</c> before its relation's position to the comma or parenthesis that ends it.
    /// </summary>
    private static IReadOnlyList<TableLikeNode> ExtractLikeClauses(JsonElement element, string statementSql, int statementOffset)
    {
        if (!TryGetDirectProperty(element, "tableElts", out var elements) || elements.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var clauses = new List<TableLikeNode>();
        var ownColumns = 0;
        foreach (var tableElement in elements.EnumerateArray())
        {
            if (TryGetDirectProperty(tableElement, "ColumnDef", out _))
            {
                ownColumns++;
                continue;
            }

            if (!TryGetDirectProperty(tableElement, "TableLikeClause", out var like)
                || !TryGetDirectProperty(like, "relation", out var relation)
                || TryGetDirectStringProperty(relation, "relname") is not { Length: > 0 } name)
            {
                continue;
            }

            var options = TryGetDirectInt32Property(like, "options", out var bits) ? bits : 0;
            var location = TryGetDirectInt32Property(relation, "location", out var relationLocation) ? relationLocation - statementOffset : -1;
            clauses.Add(new TableLikeNode(TryGetDirectStringProperty(relation, "schemaname"), name, options, ReadLikeClauseText(statementSql, location) ?? $"LIKE {name}", ownColumns));
        }

        return clauses;
    }

    private static string? ReadLikeClauseText(string statementSql, int relationStart)
    {
        // The tokens come from the statement's one run of the lexer, and only as far as the clause ends: lexing and copying the rest of the
        // statement for each LIKE clause is quadratic in its length (G-310).
        if (relationStart < 0 || relationStart >= statementSql.Length || SourceText.TokensFromOrNull(statementSql, relationStart) is not { } tokens)
        {
            return null;
        }

        var end = statementSql.Length;
        var depth = 0;
        foreach (var (tokenStart, tokenEnd) in tokens)
        {
            var single = tokenEnd - tokenStart == 1 ? statementSql[tokenStart] : '\0';
            if (depth == 0 && single is ',' or ')')
            {
                end = tokenStart;
                break;
            }

            depth += single == '(' ? 1 : single == ')' ? -1 : 0;
        }

        // The keyword as written, when it is right before the relation; LIKE otherwise.
        var beforeEnd = relationStart;
        while (beforeEnd > 0 && char.IsWhiteSpace(statementSql[beforeEnd - 1]))
        {
            beforeEnd--;
        }

        var keyword = beforeEnd >= 4 && statementSql.AsSpan(beforeEnd - 4, 4).Equals("LIKE", StringComparison.OrdinalIgnoreCase) ? statementSql.Substring(beforeEnd - 4, 4) : "LIKE";
        return $"{keyword} {statementSql[relationStart..end].Trim()}";
    }

    /// <summary>
    /// The parents of <c>CREATE TABLE ... INHERITS (...)</c>, in order (G-194). <c>inhRelations</c> is also a partition's parent, which
    /// <see cref="ExtractPartitionBound"/> reads when <c>partbound</c> is present, so it is read here only when it is not.
    /// </summary>
    private static IReadOnlyList<TableParentNode> ExtractInheritsFrom(JsonElement element)
    {
        if (TryGetDirectProperty(element, "partbound", out _)
            || !TryGetDirectProperty(element, "inhRelations", out var relations)
            || relations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parents = new List<TableParentNode>();
        foreach (var relation in relations.EnumerateArray())
        {
            if (TryGetDirectProperty(relation, "RangeVar", out var rangeVar) && TryGetDirectStringProperty(rangeVar, "relname") is { Length: > 0 } name)
            {
                parents.Add(new TableParentNode(TryGetDirectStringProperty(rangeVar, "schemaname"), name));
            }
        }

        return parents;
    }

    /// <summary>
    /// A partitioned parent's <c>PARTITION BY strategy (...)</c> (G-24c). A plain column key element is read from its own
    /// <c>name</c>; anything else is sliced verbatim from the statement using the element's own location, the same reasoning
    /// as <see cref="TableStorageTextReader"/>.
    /// </summary>
    private static (string? Strategy, IReadOnlyList<PartitionKeyElementNode> KeyElements) ExtractPartitionSpec(JsonElement element, string statementSql, int statementOffset)
    {
        if (!TryGetDirectProperty(element, "partspec", out var partSpec))
        {
            return (null, []);
        }

        var strategy = TryGetDirectStringProperty(partSpec, "strategy") switch
        {
            "PARTITION_STRATEGY_RANGE" => "RANGE",
            "PARTITION_STRATEGY_LIST" => "LIST",
            "PARTITION_STRATEGY_HASH" => "HASH",
            _ => null,
        };

        if (strategy is null || !TryGetDirectProperty(partSpec, "partParams", out var partParams) || partParams.ValueKind != JsonValueKind.Array)
        {
            return (strategy, []);
        }

        var keyElements = new List<PartitionKeyElementNode>();
        foreach (var param in partParams.EnumerateArray())
        {
            if (!TryGetDirectProperty(param, "PartitionElem", out var partitionElem))
            {
                continue;
            }

            var name = TryGetDirectStringProperty(partitionElem, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                // A plain column may carry its own COLLATE and operator class (G-240); an expression's are in the text sliced below.
                keyElements.Add(new PartitionKeyElementNode(name, null)
                {
                    CollationName = TryGetQualifiedNameFromDirectProperty(partitionElem, "collation"),
                    OperatorClassName = TryGetQualifiedNameFromDirectProperty(partitionElem, "opclass"),
                });
                continue;
            }

            if (TryGetDirectProperty(partitionElem, "expr", out _) && TryGetDirectInt32Property(partitionElem, "location", out var location))
            {
                var text = TableStorageTextReader.ReadOptions(statementSql, [location - statementOffset]).SingleOrDefault();
                if (text is not null)
                {
                    keyElements.Add(new PartitionKeyElementNode(null, text));
                }
            }
        }

        return (strategy, keyElements);
    }

    /// <summary>
    /// A partition's parent and <c>FOR VALUES ...</c>/<c>DEFAULT</c> bound (G-24c). <c>inhRelations</c> also appears on a plain
    /// <c>CREATE TABLE ... INHERITS (...)</c>, which is not partitioning, so this only reads it when <c>partbound</c> is present too.
    /// </summary>
    private static (string? SchemaName, string? TableName, string? BoundRaw) ExtractPartitionBound(JsonElement element, string statementSql, int statementOffset)
    {
        if (!TryGetDirectProperty(element, "partbound", out var partBound))
        {
            return (null, null, null);
        }

        TryGetRelationIdentity(element, "inhRelations", out var parentTableName, out var parentSchemaName);

        var strategy = TryGetDirectBooleanProperty(partBound, "is_default", out var isDefault) && isDefault
            ? null
            : TryGetDirectStringProperty(partBound, "strategy");
        var boundRaw = TryGetDirectInt32Property(partBound, "location", out var location)
            ? PartitionBoundTextReader.TryRead(statementSql, location - statementOffset, strategy)
            : null;

        return (parentSchemaName, parentTableName, boundRaw);
    }

    /// <summary>
    /// The table's <c>WITH (...)</c> storage parameters (G-24b), each sliced verbatim from the statement (see
    /// <see cref="TableStorageTextReader"/>) using the character offset pg_query gives each option (<c>DefElem.location</c>), rather
    /// than re-rendered from the parsed value, because a re-serialized value can silently stop matching what the author wrote.
    /// </summary>
    private static IReadOnlyList<TableStorageOptionNode> ExtractStorageOptions(JsonElement element, string statementSql, int statementOffset)
    {
        if (!TryGetDirectProperty(element, "options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var locations = new List<int>();
        foreach (var option in options.EnumerateArray())
        {
            if (TryGetDirectProperty(option, "DefElem", out var defElem) && TryGetDirectInt32Property(defElem, "location", out var location))
            {
                locations.Add(location - statementOffset);
            }
        }

        var texts = TableStorageTextReader.ReadOptions(statementSql, locations);
        return texts
            .Select(static text =>
            {
                var separator = text.IndexOf('=', StringComparison.Ordinal);
                var name = (separator < 0 ? text : text[..separator]).Trim();
                return new TableStorageOptionNode(name, text);
            })
            .ToArray();
    }

    /// <summary>
    /// Records what an <c>ALTER TABLE</c> statement does that the model can act on or must report (G-47). <c>ADD COLUMN</c> becomes an
    /// <see cref="AlterTableAddColumnNode"/>; <c>ADD CONSTRAINT</c> is read from the statement's children as before; every other action
    /// becomes an <see cref="UnsupportedAlterTableNode"/>, so it is reported rather than silently ignored.
    /// </summary>
    private static void AddAlterTableCommandNodes(JsonElement alterTable, ICollection<SqlNode> nodes, string? tableName, string? schemaName, string statementSql)
    {
        if (!TryGetDirectProperty(alterTable, "cmds", out var commands) || commands.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var commandWrapper in commands.EnumerateArray())
        {
            if (!TryGetDirectProperty(commandWrapper, "AlterTableCmd", out var command))
            {
                continue;
            }

            var subtype = TryGetDirectStringProperty(command, "subtype");
            switch (subtype)
            {
                case "AT_AddColumn":
                    if (TryGetDirectProperty(command, "def", out var definition)
                        && TryGetDirectProperty(definition, "ColumnDef", out var columnDefinition)
                        && TryCreateColumnNode(columnDefinition, tableName, schemaName, statementSql) is { } column)
                    {
                        _ = TryGetDirectBooleanProperty(command, "missing_ok", out var ifNotExists);
                        nodes.Add(new AlterTableAddColumnNode(tableName, schemaName, column, ifNotExists));
                    }

                    break;
                case "AT_AddConstraint":
                    break;
                case "AT_EnableTrig" or "AT_DisableTrig" or "AT_EnableAlwaysTrig" or "AT_EnableReplicaTrig"
                    or "AT_EnableRule" or "AT_DisableRule" or "AT_EnableAlwaysRule" or "AT_EnableReplicaRule" when TryGetDirectStringProperty(command, "name") is { Length: > 0 } stateObject:
                    // A trigger's or rule's firing mode (G-257).
                    nodes.Add(new AlterTableObjectStateNode(
                        tableName,
                        schemaName,
                        subtype!.EndsWith("Rule", StringComparison.Ordinal) ? "rule" : "trigger",
                        stateObject,
                        subtype.StartsWith("AT_Disable", StringComparison.Ordinal) ? "DISABLE"
                            : subtype.Contains("Replica", StringComparison.Ordinal) ? "ENABLE REPLICA"
                            : subtype.Contains("Always", StringComparison.Ordinal) ? "ENABLE ALWAYS"
                            : "ENABLE"));
                    break;
                case "AT_EnableTrigUser" or "AT_DisableTrigUser":
                    // Every trigger the project declares on the table, not the internal ones (G-259).
                    nodes.Add(new AlterTableObjectStateNode(
                        tableName,
                        schemaName,
                        AlterTableObjectStateNode.UserTriggersKind,
                        string.Empty,
                        subtype == "AT_DisableTrigUser" ? "DISABLE" : "ENABLE"));
                    break;
                case "AT_EnableTrigAll" or "AT_DisableTrigAll":
                    // Every trigger on the table, the internal foreign-key ones included (G-261).
                    nodes.Add(new AlterTableObjectStateNode(
                        tableName,
                        schemaName,
                        AlterTableObjectStateNode.AllTriggersKind,
                        string.Empty,
                        subtype == "AT_DisableTrigAll" ? "DISABLE" : "ENABLE"));
                    break;
                case "AT_DropCluster":
                    nodes.Add(new AlterTableClusterOnNode(tableName, schemaName, null));
                    break;
                case "AT_ClusterOn" when TryGetDirectStringProperty(command, "name") is { Length: > 0 } clusterIndex:
                    nodes.Add(new AlterTableClusterOnNode(tableName, schemaName, clusterIndex));
                    break;
                case "AT_ReplicaIdentity" when TryGetDirectProperty(command, "def", out var replicaDefinition)
                    && TryGetDirectProperty(replicaDefinition, "ReplicaIdentityStmt", out var replicaStatement)
                    && TryGetDirectStringProperty(replicaStatement, "identity_type") is { Length: > 0 } replicaType:
                    nodes.Add(new AlterTableReplicaIdentityNode(
                        tableName,
                        schemaName,
                        replicaType switch { "f" => "FULL", "n" => "NOTHING", "i" => "USING INDEX", _ => "DEFAULT" },
                        TryGetDirectStringProperty(replicaStatement, "name")));
                    break;
                case "AT_ColumnDefault" when TryGetDirectStringProperty(command, "name") is { Length: > 0 } defaultColumn:
                {
                    // pg_dump writes a serial column's default as ALTER COLUMN ... SET DEFAULT (G-236); DROP DEFAULT has no def.
                    var hasDefault = TryGetDirectProperty(command, "def", out var defaultExpression);
                    nodes.Add(new AlterTableColumnDefaultNode(
                        tableName,
                        schemaName,
                        defaultColumn,
                        hasDefault ? ReadExpressionText(defaultExpression) : null,
                        hasDefault ? defaultExpression.GetRawText() : null));
                    break;
                }

                case "AT_AddIdentity" when TryGetDirectStringProperty(command, "name") is { Length: > 0 } identityColumn
                    && TryGetDirectProperty(command, "def", out var identityDefinition)
                    && TryGetDirectProperty(identityDefinition, "Constraint", out var identityConstraint):
                {
                    // A synthetic column holding the constraint, so the readers a column's own identity uses can be reused.
                    using var identityColumnDocument = PgQueryJson.Parse("{\"constraints\":[{\"Constraint\":" + identityConstraint.GetRawText() + "}]}");
                    var identityColumnElement = identityColumnDocument.RootElement;
                    nodes.Add(new AlterTableAddIdentityNode(
                        tableName,
                        schemaName,
                        identityColumn,
                        TryGetIdentityGeneration(identityColumnElement),
                        TryGetSequenceOptions(identityConstraint),
                        ReadIdentityOptionsText(identityConstraint, statementSql)));
                    break;
                }

                case "AT_SetStorage" or "AT_SetCompression" when TryGetDirectStringProperty(command, "name") is { Length: > 0 } storageColumn
                    && TryGetDirectProperty(command, "def", out var storageValue)
                    && TryGetStringPropertyRecursive(storageValue, "sval") is { Length: > 0 } storageText:
                    nodes.Add(subtype == "AT_SetStorage"
                        ? new AlterTableColumnStorageNode(tableName, schemaName, storageColumn, storageText.ToLowerInvariant(), null)
                        : new AlterTableColumnStorageNode(tableName, schemaName, storageColumn, null, storageText.ToLowerInvariant()));
                    break;
                case "AT_AttachPartition" when string.Equals(TryGetDirectStringProperty(alterTable, "objtype"), "OBJECT_INDEX", StringComparison.Ordinal)
                    && TryGetDirectProperty(command, "def", out var indexAttachDefinition)
                    && TryGetDirectProperty(indexAttachDefinition, "PartitionCmd", out var indexAttachCommand)
                    && TryGetRelationIdentity(indexAttachCommand, "name", out var childIndexName, out var childIndexSchema):
                    // A partition's index tied to the parent's (G-236).
                    nodes.Add(new AlterIndexAttachPartitionNode(tableName, schemaName, childIndexName!, childIndexSchema));
                    break;
                case "AT_AttachPartition" when TryGetDirectProperty(command, "def", out var attachDefinition)
                    && TryGetDirectProperty(attachDefinition, "PartitionCmd", out var attachCommand)
                    && TryGetRelationIdentity(attachCommand, "name", out var childName, out var childSchemaName)
                    && TryGetDirectProperty(attachCommand, "bound", out var attachBound):
                {
                    var attachStrategy = TryGetDirectBooleanProperty(attachBound, "is_default", out var attachIsDefault) && attachIsDefault
                        ? null
                        : TryGetDirectStringProperty(attachBound, "strategy");
                    var offset = s_context is { } attachContext && string.Equals(attachContext.StatementSql, statementSql, StringComparison.Ordinal) ? attachContext.StatementOffset : 0;
                    var attachBoundRaw = TryGetDirectInt32Property(attachBound, "location", out var attachLocation)
                        ? PartitionBoundTextReader.TryRead(statementSql, attachLocation - offset, attachStrategy)
                        : null;
                    nodes.Add(new AlterTableAttachPartitionNode(tableName, schemaName, childName!, childSchemaName, attachBoundRaw));
                    break;
                }

                case "AT_ChangeOwner" when OwnerStatementReader.ReadAlterTableOwner(alterTable, command, tableName, schemaName) is { } ownerNode:
                    // The owner of a table, view, materialized view, or sequence (G-146). OWNER TO CURRENT_USER names no role, so it falls through
                    // to the default below and is reported as an action the model ignores.
                    nodes.Add(ownerNode);
                    break;
                case "AT_EnableRowSecurity":
                    nodes.Add(new AlterTableRowSecurityNode(tableName, schemaName, EnableRowSecurity: true, ForceRowSecurity: null));
                    break;
                case "AT_DisableRowSecurity":
                    nodes.Add(new AlterTableRowSecurityNode(tableName, schemaName, EnableRowSecurity: false, ForceRowSecurity: null));
                    break;
                case "AT_ForceRowSecurity":
                    nodes.Add(new AlterTableRowSecurityNode(tableName, schemaName, EnableRowSecurity: null, ForceRowSecurity: true));
                    break;
                case "AT_NoForceRowSecurity":
                    nodes.Add(new AlterTableRowSecurityNode(tableName, schemaName, EnableRowSecurity: null, ForceRowSecurity: false));
                    break;
                default:
                    nodes.Add(new UnsupportedAlterTableNode(tableName, schemaName, DescribeAlterTableAction(subtype)));
                    break;
            }
        }
    }

    /// <summary>True when an <c>ALTER VIEW</c> does nothing but <c>ALTER COLUMN ... SET|DROP DEFAULT</c> (G-291).</summary>
    internal static bool IsAlterViewColumnDefault(JsonElement alterTable)
        => string.Equals(TryGetDirectStringProperty(alterTable, "objtype"), "OBJECT_VIEW", StringComparison.Ordinal)
            && TryGetDirectProperty(alterTable, "cmds", out var commands)
            && commands.ValueKind == JsonValueKind.Array
            && commands.GetArrayLength() > 0
            && commands.EnumerateArray().All(static wrapper => TryGetDirectProperty(wrapper, "AlterTableCmd", out var command)
                && string.Equals(TryGetDirectStringProperty(command, "subtype"), "AT_ColumnDefault", StringComparison.Ordinal));

    /// <summary>True when an <c>ALTER INDEX</c> does nothing but <c>ATTACH PARTITION</c> (G-236).</summary>
    internal static bool IsAlterIndexAttachPartition(JsonElement alterTable)
        => string.Equals(TryGetDirectStringProperty(alterTable, "objtype"), "OBJECT_INDEX", StringComparison.Ordinal)
            && TryGetDirectProperty(alterTable, "cmds", out var commands)
            && commands.ValueKind == JsonValueKind.Array
            && commands.GetArrayLength() > 0
            && commands.EnumerateArray().All(static wrapper => TryGetDirectProperty(wrapper, "AlterTableCmd", out var command)
                && string.Equals(TryGetDirectStringProperty(command, "subtype"), "AT_AttachPartition", StringComparison.Ordinal));

    /// <summary>
    /// <c>ALTER SEQUENCE s OWNED BY table.column</c> and nothing else (G-236); null for any other <c>ALTER SEQUENCE</c>, which is reported as not modeled.
    /// </summary>
    internal static AlterSequenceOwnedByNode? ReadAlterSequenceOwnedBy(JsonElement alterSequence)
    {
        if (!IsAlterSequenceOwnedBy(alterSequence, out var ownedBy)
            || !TryGetRelationIdentity(alterSequence, "sequence", out var sequenceName, out var sequenceSchema))
        {
            return null;
        }

        return new AlterSequenceOwnedByNode(sequenceName, sequenceSchema, ownedBy!);
    }

    /// <summary>True when an <c>AlterSeqStmt</c> has exactly one option and it is <c>OWNED BY</c> a column (G-236).</summary>
    internal static bool IsAlterSequenceOwnedBy(JsonElement alterSequence, out string? ownedBy)
    {
        ownedBy = null;
        if (!TryGetDirectProperty(alterSequence, "options", out var options) || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() != 1)
        {
            return false;
        }

        var parsed = TryGetSequenceOptions(alterSequence);
        if (parsed is null || !parsed.TryGetValue("owned_by", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ownedBy = value;
        return true;
    }

    private static string DescribeAlterTableAction(string? subtype)
        => subtype switch
        {
            "AT_DropColumn" => "DROP COLUMN",
            "AT_AlterColumnType" => "ALTER COLUMN ... TYPE",
            "AT_ColumnDefault" => "ALTER COLUMN ... SET/DROP DEFAULT",
            "AT_SetNotNull" or "AT_DropNotNull" => "ALTER COLUMN ... SET/DROP NOT NULL",
            "AT_SetStatistics" => "ALTER COLUMN ... SET STATISTICS",
            "AT_SetStorage" => "ALTER COLUMN ... SET STORAGE",
            "AT_DropConstraint" => "DROP CONSTRAINT",
            "AT_ValidateConstraint" => "VALIDATE CONSTRAINT",
            "AT_AlterConstraint" => "ALTER CONSTRAINT",
            "AT_ChangeOwner" => "OWNER TO",
            "AT_SetTableSpace" => "SET TABLESPACE",
            "AT_SetRelOptions" or "AT_ResetRelOptions" => "SET/RESET (storage parameters)",
            "AT_SetLogged" or "AT_SetUnLogged" => "SET LOGGED/UNLOGGED",
            "AT_EnableTrig" or "AT_DisableTrig" or "AT_EnableAlwaysTrig" or "AT_EnableReplicaTrig"
                or "AT_EnableTrigAll" or "AT_DisableTrigAll" => "ENABLE/DISABLE TRIGGER",
            "AT_AttachPartition" or "AT_DetachPartition" or "AT_DetachPartitionFinalize" => "ATTACH/DETACH PARTITION",
            null or "" => "an action",
            _ => subtype.StartsWith("AT_", StringComparison.Ordinal) ? $"the action {subtype[3..]}" : $"the action {subtype}",
        };

    private static ColumnNode? TryCreateColumnNode(JsonElement element, string? currentTableName, string? currentSchemaName, string statementSql)
    {
        var columnName = TryGetStringPropertyRecursive(element, "colname");
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return null;
        }

        var rawType = TryGetTypeName(element);
        var isSerial = TryGetSerialUnderlyingType(rawType, out var normalizedType);
        if (isSerial)
        {
            rawType = normalizedType;
        }

        var isNullable = TryGetColumnNullability(element);
        // pg_query reports a column DEFAULT in the column's constraints list, and the SQL renderer is lossy, so the
        // expression is read verbatim from the statement text (G-56).
        var rawDefaultExpression = ColumnDefaultTextReader.TryRead(statementSql, columnName)
            ?? TryGetColumnRawDefaultExpression(element);
        var identityGeneration = TryGetIdentityGeneration(element);
        if (isSerial)
        {
            isNullable ??= false;
            rawDefaultExpression ??= BuildSerialDefaultExpression(currentSchemaName, currentTableName, columnName);
        }

        if (!string.IsNullOrWhiteSpace(identityGeneration))
        {
            // PostgreSQL implicitly forces NOT NULL on GENERATED ... AS IDENTITY columns, even
            // when the column definition doesn't spell out an explicit NOT NULL constraint.
            isNullable ??= false;
        }

        // TODO: Capture column-level statistics targets when pg_query exposes those options consistently in the JSON shape we consume.
        // TODO: Capture column-level compression settings separately from generic storage metadata when downstream semantic consumers are defined.

        // GENERATED ALWAYS AS (expr) STORED is a CONSTR_GENERATED entry in the column's constraints, not a property of the column (G-71).
        // The expression is sliced verbatim from the statement (the renderer is lossy); the tree is kept for the canonical comparison.
        var generatedAst = TryGetGeneratedConstraintExpression(element);
        var generatedExpression = generatedAst is null
            ? TryGetGeneratedExpression(element)
            : ColumnDefaultTextReader.TryReadGeneratedExpression(statementSql, columnName) ?? TryGetGeneratedExpression(element);
        var typeDetails = ColumnTypeDetailsReader.Read(element);

        return new ColumnNode(
            columnName,
            currentTableName,
            currentSchemaName,
            rawType,
            rawType,
            isNullable,
            rawDefaultExpression,
            generatedAst is not null || TryIsGeneratedColumn(element),
            generatedExpression,
            TryGetCollationName(element),
            TryGetStorageParameters(element),
            identityGeneration,
            isSerial,
            TryGetRenamedFromAnnotation(element, statementSql))
        {
            WrittenType = isSerial || !TryGetDirectProperty(element, "typeName", out var typeNameElement) ? null : ReadTypeText(typeNameElement),
            TypeModifiers = typeDetails.Modifiers,
            ArrayDimensions = typeDetails.ArrayDimensions,
            IntervalFields = typeDetails.IntervalFields,
            GeneratedExpressionAst = generatedAst,
            IsVirtual = generatedAst is not null && TryIsVirtualGeneratedColumn(element),
            DefaultExpressionAst = isSerial ? null : TryGetDefaultConstraintExpression(element),
            IdentityOptions = TryGetIdentityConstraint(element) is { } identity ? TryGetSequenceOptions(identity) : null,
            IdentityOptionsText = TryGetIdentityConstraint(element) is { } written ? ReadIdentityOptionsText(written, statementSql) : null,
            Storage = TryGetDirectStringProperty(element, "storage_name"),
            Compression = TryGetDirectStringProperty(element, "compression"),
            // Where the column and its type are written (G-309e): the name, and the type's name.
            Range = NameRangeAt(LocationOf(element, "ColumnDef")),
            TypeRange = isSerial ? null : NameRangeAt(TryGetDirectProperty(Unwrap(element, "ColumnDef"), "typeName", out var placedType) ? LocationOf(placedType, "TypeName") : null),
            NotNullName = isNullable == false ? TryGetColumnNotNullName(element) : null,
            NotNullNoInherit = isNullable == false && TryGetColumnNotNullNoInherit(element),
        };
    }

    /// <summary>The name written on a column's own <c>NOT NULL</c> constraint, or null (G-235).</summary>
    private static string? TryGetColumnNotNullName(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "constraints", out var constraintsElement))
        {
            return null;
        }

        foreach (var constraint in EnumerateConstraintNodes(constraintsElement))
        {
            if (string.Equals(TryGetConstraintType(constraint), "CONSTR_NOTNULL", StringComparison.OrdinalIgnoreCase)
                && TryGetDirectStringProperty(constraint, "conname") is { Length: > 0 } name)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>True when a column's own <c>NOT NULL</c> says <c>NO INHERIT</c> (G-253).</summary>
    private static bool TryGetColumnNotNullNoInherit(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "constraints", out var constraintsElement))
        {
            return false;
        }

        foreach (var constraint in EnumerateConstraintNodes(constraintsElement))
        {
            if (string.Equals(TryGetConstraintType(constraint), "CONSTR_NOTNULL", StringComparison.OrdinalIgnoreCase)
                && TryGetDirectBooleanProperty(constraint, "is_no_inherit", out var noInherit) && noInherit)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A column's <c>GENERATED ... AS IDENTITY</c> constraint, or null.</summary>
    private static JsonElement? TryGetIdentityConstraint(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "constraints", out var constraintsElement))
        {
            return null;
        }

        foreach (var constraint in EnumerateConstraintNodes(constraintsElement))
        {
            if (string.Equals(TryGetConstraintType(constraint), "CONSTR_IDENTITY", StringComparison.OrdinalIgnoreCase))
            {
                return constraint;
            }
        }

        return null;
    }

    /// <summary>
    /// The text inside an identity's parentheses as written (<c>START WITH 10 INCREMENT BY 5</c>), which is what is deployed (G-214); null when it has
    /// none or the text cannot be found. PostgreSQL's lexer finds the parentheses after <c>IDENTITY</c>, starting at the constraint's position.
    /// </summary>
    private static string? ReadIdentityOptionsText(JsonElement constraint, string statementSql)
    {
        if (!TryGetDirectProperty(constraint, "location", out var locationElement) || locationElement.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var offset = s_context is { } context && string.Equals(context.StatementSql, statementSql, StringComparison.Ordinal) ? context.StatementOffset : 0;
        var start = locationElement.GetInt32() - offset;
        if (start < 0 || start >= statementSql.Length)
        {
            return null;
        }

        // The tokens come from the statement's one run of the lexer, from the constraint on, and are read only as far as the parentheses
        // close: lexing the rest of the statement for each identity column is quadratic in its length (G-310).
        if (SourceText.TokensFromOrNull(statementSql, start) is not { } tokens)
        {
            return null;
        }

        var afterIdentity = false;
        var openEnd = -1;
        var depth = 0;
        foreach (var (tokenStart, tokenEnd) in tokens)
        {
            var single = tokenEnd - tokenStart == 1 ? statementSql[tokenStart] : '\0';
            if (openEnd < 0)
            {
                if (afterIdentity)
                {
                    // The token after IDENTITY must open the options.
                    if (single != '(')
                    {
                        return null;
                    }

                    openEnd = tokenEnd;
                    depth = 1;
                }
                else if (statementSql.AsSpan(tokenStart, tokenEnd - tokenStart).Equals("IDENTITY", StringComparison.OrdinalIgnoreCase))
                {
                    afterIdentity = true;
                }

                continue;
            }

            depth += single == '(' ? 1 : single == ')' ? -1 : 0;
            if (depth == 0)
            {
                var inner = statementSql[openEnd..tokenStart].Trim();
                return inner.Length == 0 ? null : inner;
            }
        }

        return null;
    }

    /// <summary>
    /// Detects an explicit rename annotation (e.g. <c>-- @renamed-from: old_name</c>) placed on the line(s)
    /// immediately preceding a column definition, allowing renames to be expressed declaratively instead of
    /// being inferred heuristically from add/drop pairs. See docs/renaming-columns.md for the full contract.
    /// </summary>
    private static string? TryGetRenamedFromAnnotation(JsonElement element, string statementSql)
    {
        if (!TryGetDirectInt32Property(element, "location", out var location)
            || location <= 0
            || location > statementSql.Length)
        {
            return null;
        }

        // The lines before the column, nearest first, found from the column backwards: only the few lines the annotation can be on are read,
        // not all the text before the column, which for a wide table is quadratic (G-310). The part of the column's own line before it comes
        // first: when it holds anything but blanks (the statement's start, or another column on the same line), no line above can hold the
        // column's annotation. That is found by walking back over the blanks, not by copying the line, which for a table written on one line
        // is quadratic in its columns too (G-322).
        var ownLineStart = location;
        while (ownLineStart > 0 && statementSql[ownLineStart - 1] != '\n' && char.IsWhiteSpace(statementSql[ownLineStart - 1]))
        {
            ownLineStart--;
        }

        if (ownLineStart == 0 || statementSql[ownLineStart - 1] != '\n')
        {
            return null;
        }

        var lineEnd = ownLineStart - 1;
        var more = true;
        while (more)
        {
            var newline = lineEnd == 0 ? -1 : statementSql.LastIndexOf('\n', lineEnd - 1);
            var lineStart = newline + 1;
            var lineText = statementSql.Substring(lineStart, lineEnd - lineStart);
            more = newline >= 0;
            lineEnd = newline;
            var line = lineText.Trim().TrimEnd('\r');

            if (line.Length == 0)
            {
                continue;
            }

            if (!line.StartsWith("--", StringComparison.Ordinal))
            {
                // Stop at the first non-blank, non-comment line: the annotation must be immediately
                // above the column definition (ignoring blank lines) to be considered its own.
                break;
            }

            var commentBody = line[2..].Trim();
            const string marker = "@renamed-from:";
            if (commentBody.StartsWith(marker, StringComparison.OrdinalIgnoreCase)
                && commentBody[marker.Length..].Trim() is { Length: > 0 } oldName)
            {
                // Read as PostgreSQL reads a name, once, here (G-287): `Email` and `"email"` are the column email, `"Email"` is Email. Every use
                // (the diff, the rename the deploy layer writes, the view renderer, the constraint matching) compares this name exactly.
                return SqlIdentifiers.FoldName(oldName);
            }

            // A comment that starts like the annotation and is not one (no colon, `@renamed_from`, no name) would turn the rename into a dropped and an added
            // column without a word, so it is a warning (G-285).
            if (commentBody.StartsWith("@renamed", StringComparison.OrdinalIgnoreCase))
            {
                WarnNearMissRenameAnnotation(statementSql, lineStart + lineText.IndexOf("--", StringComparison.Ordinal), line);
            }

            // Encountered a comment line that isn't the annotation; keep scanning upward in case the
            // annotation sits above other comment lines, but do not fall through into unrelated columns.
        }

        return null;
    }

    /// <summary>The warning for a comment above a column that looks like <c>-- @renamed-from: old_name</c> and is not read as one (G-285).</summary>
    private static void WarnNearMissRenameAnnotation(string statementSql, int offsetInStatement, string comment)
    {
        if (s_context is not { } context
            || !string.Equals(context.StatementSql, statementSql, StringComparison.Ordinal)
            || !context.WarnedRenameComments.Add(context.StatementOffset + offsetInStatement))
        {
            return;
        }

        // From the `--` to the end of the comment's text (G-308).
        var start = context.StatementOffset + offsetInStatement;
        context.Diagnostics.Add(LocatedAt(
            SqlDiagnosticSeverity.Warning,
            $"This comment is not read as a column rename, so the column below it is compared as a new column and the old one as removed: {comment}. "
                + "Write it as -- @renamed-from: old_name (with the colon and the old name).",
            nameof(PgQueryParser),
            context.Sql,
            start,
            start + comment.Length));
    }

    private static bool TryGetSerialUnderlyingType(string? rawType, out string? normalizedType)
    {
        switch (rawType?.Trim().ToLowerInvariant())
        {
            case "serial":
            case "serial4":
                normalizedType = "integer";
                return true;
            case "bigserial":
            case "serial8":
                normalizedType = "bigint";
                return true;
            case "smallserial":
            case "serial2":
                normalizedType = "smallint";
                return true;
            default:
                normalizedType = rawType;
                return false;
        }
    }

    /// <summary>
    /// The default PostgreSQL gives a <c>serial</c> column. The sequence is schema-qualified so it resolves whatever the
    /// search path is: an unqualified name would fail for a table outside it (G-58). A name is quoted only when it has to be.
    /// </summary>
    private static string BuildSerialDefaultExpression(string? schemaName, string? tableName, string columnName)
    {
        var sequenceName = tableName is null
            ? $"{columnName}_seq"
            : $"{tableName}_{columnName}_seq";

        var reference = string.IsNullOrWhiteSpace(schemaName)
            ? QuoteIdentifierIfNeeded(sequenceName)
            : $"{QuoteIdentifierIfNeeded(schemaName)}.{QuoteIdentifierIfNeeded(sequenceName)}";

        return $"nextval('{reference.Replace("'", "''", StringComparison.Ordinal)}'::regclass)";
    }

    private static string QuoteIdentifierIfNeeded(string identifier)
        => identifier.Length > 0
            && (identifier[0] is (>= 'a' and <= 'z') or '_')
            && identifier.All(static character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '$')
                ? identifier
                : "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static SequenceNode? TryCreateSequenceNode(JsonElement element)
    {
        if (!TryGetRelationIdentity(element, "sequence", out var sequenceName, out var schemaName))
        {
            return null;
        }

        return new SequenceNode(sequenceName, schemaName, TryGetSequenceOptions(element))
        {
            IsUnlogged = TryGetDirectProperty(element, "sequence", out var relation) && string.Equals(TryGetDirectStringProperty(relation, "relpersistence"), "u", StringComparison.Ordinal),
        };
    }

    private static IReadOnlyDictionary<string, string>? TryGetSequenceOptions(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "options", out var optionsElement) || optionsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var optionElement in optionsElement.EnumerateArray())
        {
            if (!TryGetDirectProperty(optionElement, "DefElem", out var defElement))
            {
                continue;
            }

            var defName = TryGetDirectStringProperty(defElement, "defname");
            if (string.IsNullOrWhiteSpace(defName))
            {
                continue;
            }

            // OWNED BY targets a qualified column name nested under a List of String parts rather than a scalar/TypeName value.
            var value = string.Equals(defName, "owned_by", StringComparison.OrdinalIgnoreCase)
                ? TryGetQualifiedName(defElement, "items")
                : TryRenderDirectSqlProperty(defElement, "arg");

            // LOGGED and UNLOGGED have no value: an identity's sequence says whether it is logged (G-294).
            if (string.IsNullOrWhiteSpace(value) && (string.Equals(defName, "unlogged", StringComparison.OrdinalIgnoreCase) || string.Equals(defName, "logged", StringComparison.OrdinalIgnoreCase)))
            {
                value = "true";
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                options[defName] = value;
            }
        }

        return options.Count == 0 ? null : options;
    }

    private static IReadOnlyList<ColumnNode> ExtractColumns(JsonElement element, string? tableName, string? schemaName, string statementSql)
    {
        var columns = new List<ColumnNode>();

        if (!TryFindPropertyRecursive(element, "tableElts", out var tableElements) || tableElements.ValueKind != JsonValueKind.Array)
        {
            return columns;
        }

        foreach (var tableElement in tableElements.EnumerateArray())
        {
            if (!TryFindPropertyRecursive(tableElement, "ColumnDef", out var columnDefinition))
            {
                continue;
            }

            var column = TryCreateColumnNode(columnDefinition, tableName, schemaName, statementSql);
            if (column is not null)
            {
                columns.Add(column);
            }
        }

        return columns;
    }

    private static string? TryGetTypeName(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "typeName", out var typeNameElement))
        {
            return null;
        }

        return TryGetQualifiedNameFromDirectProperty(typeNameElement, "names")
            ?? TryGetDirectStringProperty(typeNameElement, "sval")
            ?? TryGetStringPropertyRecursive(typeNameElement, "sval");
    }

    private static bool? TryGetColumnNullability(JsonElement element)
    {
        if (TryGetDirectBooleanProperty(element, "is_not_null", out var isNotNull))
        {
            return !isNotNull;
        }

        if (!TryGetDirectProperty(element, "constraints", out var constraintsElement))
        {
            return null;
        }

        foreach (var constraint in EnumerateConstraintNodes(constraintsElement))
        {
            if (TryGetDirectBooleanProperty(constraint, "is_not_null", out isNotNull))
            {
                return !isNotNull;
            }

            var constraintType = TryGetConstraintType(constraint);
            if (string.Equals(constraintType, "CONSTR_NOTNULL", StringComparison.OrdinalIgnoreCase)
                || string.Equals(constraintType, "not_null", StringComparison.OrdinalIgnoreCase)
                || string.Equals(constraintType, "notnull", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(constraintType, "CONSTR_NULL", StringComparison.OrdinalIgnoreCase)
                || string.Equals(constraintType, "null", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return null;
    }

    private static string? TryGetColumnRawDefaultExpression(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "raw_expr", out var rawExpressionElement))
        {
            return null;
        }

        return ReadExpressionText(rawExpressionElement);
    }

    /// <summary>The pg_query JSON of the expression of a <c>CONSTR_GENERATED</c> constraint, or null when the column is not generated.</summary>
    private static string? TryGetGeneratedConstraintExpression(JsonElement element)
        => TryGetColumnConstraintExpression(element, "CONSTR_GENERATED");

    /// <summary>The pg_query JSON of the column's <c>DEFAULT</c> expression (G-102), or null.</summary>
    private static string? TryGetDefaultConstraintExpression(JsonElement element)
        => TryGetColumnConstraintExpression(element, "CONSTR_DEFAULT");

    private static string? TryGetColumnConstraintExpression(JsonElement element, string constraintType)
    {
        if (!TryGetDirectProperty(element, "constraints", out var constraints))
        {
            return null;
        }

        foreach (var constraint in EnumerateConstraintNodes(constraints))
        {
            if (string.Equals(TryGetConstraintType(constraint), constraintType, StringComparison.OrdinalIgnoreCase)
                && TryGetDirectProperty(constraint, "raw_expr", out var expression))
            {
                return expression.GetRawText();
            }
        }

        return null;
    }

    /// <summary>
    /// True when the column's <c>CONSTR_GENERATED</c> entry says <c>generated_kind</c> <c>v</c> (PostgreSQL 18's <c>VIRTUAL</c>, which is also
    /// what <c>GENERATED ALWAYS AS (...)</c> means there when neither <c>STORED</c> nor <c>VIRTUAL</c> is written; G-121).
    /// </summary>
    private static bool TryIsVirtualGeneratedColumn(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "constraints", out var constraints))
        {
            return false;
        }

        foreach (var constraint in EnumerateConstraintNodes(constraints))
        {
            if (string.Equals(TryGetConstraintType(constraint), "CONSTR_GENERATED", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(TryGetDirectStringProperty(constraint, "generated_kind"), "v", StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static bool TryIsGeneratedColumn(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "generated", out var generatedElement))
        {
            return false;
        }

        if (TryGetDirectBooleanProperty(element, "generated", out var isGenerated))
        {
            return isGenerated;
        }

        return generatedElement.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(generatedElement.GetString())
                && !string.Equals(generatedElement.GetString(), "\0", StringComparison.Ordinal),
            JsonValueKind.Null => false,
            JsonValueKind.Undefined => false,
            _ => true,
        };
    }

    private static string? TryGetGeneratedExpression(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "generated_expr", out var generatedExpressionElement))
        {
            return null;
        }

        return ReadExpressionText(generatedExpressionElement);
    }

    private static string? TryGetIdentityGeneration(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "constraints", out var constraintsElement))
        {
            return null;
        }

        foreach (var constraint in EnumerateConstraintNodes(constraintsElement))
        {
            var contype = TryGetConstraintType(constraint);
            if (!string.Equals(contype, "CONSTR_IDENTITY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // pg_query encodes generated_when as "a" (ALWAYS) or "d" (BY DEFAULT).
            if (TryGetDirectProperty(constraint, "generated_when", out var generatedWhenElement))
            {
                var val = generatedWhenElement.ValueKind == JsonValueKind.String
                    ? generatedWhenElement.GetString()
                    : null;

                return val switch
                {
                    "a" => "ALWAYS",
                    "d" => "BY DEFAULT",
                    _ => null,
                };
            }

            return null;
        }

        return null;
    }

    private static string? TryGetCollationName(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "collClause", out var collationElement))
        {
            return null;
        }

        return TryGetQualifiedNameFromDirectProperty(collationElement, "fields")
            ?? TryGetQualifiedNameFromDirectProperty(collationElement, "collname")
            ?? TryGetDirectStringProperty(collationElement, "collname")
            ?? TryRenderSqlFragment(collationElement);
    }

    private static IDictionary<string, string>? TryGetStorageParameters(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "storage", out var storageElement))
        {
            return null;
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectStorageParameters(storageElement, parameters, prefix: null);
        return parameters.Count == 0 ? null : parameters;
    }

    private static void CollectStorageParameters(JsonElement element, IDictionary<string, string> parameters, string? prefix)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                parameters[prefix ?? "value"] = element.ToString();
                return;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    CollectStorageParameters(child, parameters, prefix);
                }

                return;
            case JsonValueKind.Object:
                if (TryGetStringPropertyRecursive(element, "defname") is { Length: > 0 } defName)
                {
                    parameters[defName] = TryRenderDirectSqlProperty(element, "arg")
                        ?? TryRenderDirectSqlProperty(element, "value")
                        ?? TryRenderDirectSqlProperty(element, "val")
                        ?? element.GetRawText();
                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (IsScalarJsonValue(property.Value))
                    {
                        parameters[property.Name] = property.Value.ToString();
                        continue;
                    }

                    CollectStorageParameters(property.Value, parameters, property.Name);
                }

                return;
        }
    }

    private static bool LooksLikeGeneratedMode(string? value)
        => string.Equals(value, "s", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "v", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "\0", StringComparison.Ordinal);
}
