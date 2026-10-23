using System.Globalization;
using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    private static IndexNode? TryCreateIndexNode(JsonElement element, string statementSql, int statementOffset)
    {
        if (!TryGetRelationIdentity(element, "relation", out var tableName, out var schemaName))
        {
            return null;
        }

        return new IndexNode(
            TryGetStringPropertyRecursive(element, "idxname"),
            schemaName,
            tableName,
            TryGetStringPropertyRecursive(element, "accessMethod"))
        {
            KeyColumns = ExtractIndexKeyColumns(element),
            IsUnique = TryGetDirectBooleanProperty(element, "unique", out var isUnique) && isUnique,
            NullsNotDistinct = TryGetDirectBooleanProperty(element, "nulls_not_distinct", out var indexNullsNotDistinct) && indexNullsNotDistinct,
            KeyElements = ExtractIndexKeyElements(element, statementSql, statementOffset),
            IncludeColumns = ExtractIndexIncludeColumns(element),
            // The predicate is sliced from the statement as written; the renderer drops what it does not know (G-73). The tree is
            // kept for the canonical comparison.
            RawPredicateExpression = TryGetDirectProperty(element, "whereClause", out var whereClauseElement)
                ? ConstraintIndexTextReader.TryReadIndexPredicate(statementSql) ?? ReadExpressionText(whereClauseElement)
                : null,
            PredicateAst = TryGetDirectProperty(element, "whereClause", out var predicateAst) ? predicateAst.GetRawText() : null,
            // WITH (fillfactor = 70), each option as written, as a table's are (G-195).
            StorageOptions = ExtractStorageOptions(element, statementSql, statementOffset),
        };
    }

    private static IReadOnlyList<string> ExtractIndexKeyColumns(JsonElement element)
    {
        var keyColumns = new List<string>();

        if (!TryGetDirectProperty(element, "indexParams", out var indexParams) || indexParams.ValueKind != JsonValueKind.Array)
        {
            return keyColumns;
        }

        foreach (var indexParam in indexParams.EnumerateArray())
        {
            if (!TryGetDirectProperty(indexParam, "IndexElem", out var indexElem))
            {
                continue;
            }

            var columnName = TryGetDirectStringProperty(indexElem, "name");
            if (!string.IsNullOrWhiteSpace(columnName))
            {
                keyColumns.Add(columnName);
            }
        }

        return keyColumns;
    }

    private static IReadOnlyList<IndexKeyElementNode> ExtractIndexKeyElements(JsonElement element, string statementSql, int statementOffset)
    {
        var keyExpressionTexts = ConstraintIndexTextReader.ReadKeyExpressions(statementSql);
        var elementNumber = -1;
        var keyElements = new List<IndexKeyElementNode>();

        if (!TryGetDirectProperty(element, "indexParams", out var indexParams) || indexParams.ValueKind != JsonValueKind.Array)
        {
            return keyElements;
        }

        foreach (var indexParam in indexParams.EnumerateArray())
        {
            if (!TryGetDirectProperty(indexParam, "IndexElem", out var indexElem))
            {
                continue;
            }

            elementNumber++;
            var name = TryGetDirectStringProperty(indexElem, "name");
            string? rawExpression = null;
            string? expressionAst = null;
            if (string.IsNullOrWhiteSpace(name) && TryGetDirectProperty(indexElem, "expr", out var exprElement))
            {
                expressionAst = exprElement.GetRawText();
                rawExpression = (elementNumber < keyExpressionTexts.Count ? keyExpressionTexts[elementNumber] : null)
                    ?? ReadExpressionText(exprElement);
            }

            var collationName = TryGetQualifiedNameFromDirectProperty(indexElem, "collation");
            var operatorClassName = TryGetQualifiedNameFromDirectProperty(indexElem, "opclass");
            var operatorClassOptions = ExtractOperatorClassOptions(indexElem, statementSql, statementOffset);

            var ordering = TryGetDirectStringProperty(indexElem, "ordering");
            var isDescending = string.Equals(ordering, "SORTBY_DESC", StringComparison.Ordinal);

            var nullsOrdering = TryGetDirectStringProperty(indexElem, "nulls_ordering");
            bool? nullsFirst = nullsOrdering switch
            {
                "SORTBY_NULLS_FIRST" => true,
                "SORTBY_NULLS_LAST" => false,
                _ => null,
            };

            keyElements.Add(new IndexKeyElementNode(
                name,
                rawExpression,
                collationName,
                operatorClassName,
                operatorClassOptions,
                isDescending,
                nullsFirst)
            {
                ExpressionAst = expressionAst,
            });
        }

        return keyElements;
    }

    /// <summary>
    /// An operator class's options, <c>tsvector_ops (siglen = 1)</c>, each as <c>name = value</c> as the author wrote it, sliced from the statement by the
    /// location of the option's name (G-239). The values alone were kept before, so <c>(siglen = 1)</c> was deployed as <c>(1)</c>.
    /// </summary>
    private static IReadOnlyList<string> ExtractOperatorClassOptions(JsonElement indexElem, string statementSql, int statementOffset)
    {
        if (!TryGetDirectProperty(indexElem, "opclassopts", out var opclassOptsElement) || opclassOptsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var locations = new List<int>();
        var fallback = new List<string>();
        foreach (var optionElement in opclassOptsElement.EnumerateArray())
        {
            if (!TryGetDirectProperty(optionElement, "DefElem", out var defElem))
            {
                continue;
            }

            if (TryGetDirectInt32Property(defElem, "location", out var location))
            {
                locations.Add(location - statementOffset);
            }

            var optionName = TryGetDirectStringProperty(defElem, "defname");
            var value = TryGetDirectProperty(defElem, "arg", out var arg) ? TryRenderSqlFragment(arg) : null;
            if (!string.IsNullOrWhiteSpace(optionName))
            {
                fallback.Add(string.IsNullOrWhiteSpace(value) ? optionName : $"{optionName} = {value}");
            }
        }

        var written = TableStorageTextReader.ReadOptions(statementSql, locations);
        return written.Count == fallback.Count ? written : fallback;
    }

    private static IReadOnlyList<string> ExtractIndexIncludeColumns(JsonElement element)
    {
        var includeColumns = new List<string>();

        if (!TryGetDirectProperty(element, "indexIncludingParams", out var includingParams) || includingParams.ValueKind != JsonValueKind.Array)
        {
            return includeColumns;
        }

        foreach (var includingParam in includingParams.EnumerateArray())
        {
            if (!TryGetDirectProperty(includingParam, "IndexElem", out var indexElem))
            {
                continue;
            }

            var columnName = TryGetDirectStringProperty(indexElem, "name");
            if (!string.IsNullOrWhiteSpace(columnName))
            {
                includeColumns.Add(columnName);
            }
        }

        return includeColumns;
    }

    /// <summary>
    /// CONSTR_ATTR_DEFERRABLE/CONSTR_ATTR_NOT_DEFERRABLE and CONSTR_ATTR_DEFERRED/CONSTR_ATTR_IMMEDIATE are
    /// anonymous "constraint attribute" pseudo-nodes libpg_query emits as separate list entries following an
    /// inline column constraint (e.g. "REFERENCES ... DEFERRABLE INITIALLY DEFERRED"); they are not
    /// themselves constraints and must be folded onto the most recently added real constraint for that
    /// column/table instead of producing their own (invalid) ConstraintNode.
    /// </summary>
    private static bool TryApplyConstraintAttribute(JsonElement element, ICollection<SqlNode> nodes)
    {
        var constraintType = TryGetConstraintType(element);
        if (constraintType is null)
        {
            return false;
        }

        bool? isDeferrable = constraintType switch
        {
            "CONSTR_ATTR_DEFERRABLE" => true,
            "CONSTR_ATTR_NOT_DEFERRABLE" => false,
            _ => null,
        };

        bool? isInitiallyDeferred = constraintType switch
        {
            "CONSTR_ATTR_DEFERRED" => true,
            "CONSTR_ATTR_IMMEDIATE" => false,
            _ => null,
        };

        // NOT ENFORCED / ENFORCED (PostgreSQL 18) follow a CREATE TABLE constraint the same way (G-121).
        bool? isEnforced = constraintType switch
        {
            "CONSTR_ATTR_ENFORCED" => true,
            "CONSTR_ATTR_NOT_ENFORCED" => false,
            _ => null,
        };

        if (isDeferrable is null && isInitiallyDeferred is null && isEnforced is null)
        {
            return false;
        }

        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            if (nodes is not IList<SqlNode> nodeList)
            {
                break;
            }

            if (nodeList[i] is not ConstraintNode targetConstraint)
            {
                continue;
            }

            nodeList[i] = targetConstraint with
            {
                IsDeferrable = isDeferrable ?? targetConstraint.IsDeferrable,
                IsInitiallyDeferred = isInitiallyDeferred ?? targetConstraint.IsInitiallyDeferred,
                IsEnforced = isEnforced ?? targetConstraint.IsEnforced,
            };

            return true;
        }

        return true;
    }

    /// <summary>
    /// The marker for a <c>NOT NULL</c> table constraint that lists its column in <c>keys</c>; null for the column-level form, which has none and
    /// is read as the column's own nullability (G-126).
    /// </summary>
    private static NotNullConstraintNode? TryCreateNotNullConstraintNode(JsonElement element, string? currentTableName, string? currentSchemaName)
    {
        var columns = TryGetStringListFromDirectProperty(element, "keys");
        return columns.Count == 0 || string.IsNullOrWhiteSpace(currentTableName)
            ? null
            : new NotNullConstraintNode(currentTableName, currentSchemaName, columns[0], TryGetDirectStringProperty(element, "conname"))
            {
                IsNotValid = TryGetDirectBooleanProperty(element, "skip_validation", out var skipValidation) && skipValidation,
                NoInherit = TryGetDirectBooleanProperty(element, "is_no_inherit", out var noInherit) && noInherit,
            };
    }

    private static ConstraintNode? TryCreateConstraintNode(
        JsonElement element,
        string? currentTableName,
        string? currentSchemaName,
        string? currentColumnName,
        string statementSql,
        int statementOffset)
    {
        var constraintName = TryGetStringPropertyRecursive(element, "conname");
        var constraintType = TryGetConstraintType(element);
        var tableName = currentTableName;
        var schemaName = currentSchemaName;

        if (TryGetRelationIdentity(element, "relation", out var relationTableName, out var relationSchemaName))
        {
            tableName = relationTableName;
            schemaName = relationSchemaName;
        }
        else if (TryGetQualifiedName(element, "relation") is { Length: > 0 } qualifiedRelation)
        {
            SplitSchemaQualifiedName(qualifiedRelation, out var qualifiedSchemaName, out var qualifiedTableName);
            tableName = qualifiedTableName;
            schemaName = qualifiedSchemaName;
        }

        if (string.IsNullOrWhiteSpace(constraintName) && string.IsNullOrWhiteSpace(constraintType))
        {
            return null;
        }

        // CONSTR_NOTNULL/CONSTR_NULL are anonymous pseudo-constraint nodes libpg_query emits for
        // every "NOT NULL"/"NULL" column modifier. They carry no name and are not real named
        // constraints (nullability is already captured on ColumnDefinition.IsNullable). Without
        // this filter, every such column produced a ConstraintNode with the same synthesized
        // fallback name ("{table}_constraint"), colliding with each other and with any real
        // unnamed inline PK/UNIQUE/FK constraint on the same table, and had no structural mapping
        // in DeepConstraintMappingPass so it emitted a broken "ADD CONSTRAINT ... -- TODO" statement.
        if (string.IsNullOrWhiteSpace(constraintName)
            && (string.Equals(constraintType, "CONSTR_NOTNULL", StringComparison.OrdinalIgnoreCase)
                || string.Equals(constraintType, "CONSTR_NULL", StringComparison.OrdinalIgnoreCase)
                || string.Equals(constraintType, "CONSTR_IDENTITY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(constraintType, "CONSTR_GENERATED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(constraintType, "CONSTR_DEFAULT", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        // Table-level PK/UNIQUE/FK constraints carry an explicit "keys" array; inline (column-level)
        // PK/UNIQUE constraints have no "keys" array and rely on the enclosing column's name.
        var keyColumns = TryGetStringListFromDirectProperty(element, "keys");
        if (keyColumns.Count == 0
            && !string.IsNullOrWhiteSpace(currentColumnName)
            && (string.Equals(constraintType, "CONSTR_PRIMARY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(constraintType, "CONSTR_UNIQUE", StringComparison.OrdinalIgnoreCase)))
        {
            keyColumns = [currentColumnName];
        }

        string? fkSchemaName = null;
        string? fkTableName = null;
        if (TryGetRelationIdentity(element, "pktable", out var pkRelationName, out var pkRelationSchemaName))
        {
            fkTableName = pkRelationName;
            fkSchemaName = pkRelationSchemaName;
        }
        else if (TryGetQualifiedName(element, "pktable") is { Length: > 0 } qualifiedPkTable)
        {
            SplitSchemaQualifiedName(qualifiedPkTable, out fkSchemaName, out fkTableName);
        }

        var fkColumns = TryGetStringListFromDirectProperty(element, "fk_attrs");
        if (fkColumns.Count == 0
            && !string.IsNullOrWhiteSpace(currentColumnName)
            && string.Equals(constraintType, "CONSTR_FOREIGN", StringComparison.OrdinalIgnoreCase))
        {
            fkColumns = [currentColumnName];
        }

        var fkReferencedColumns = TryGetStringListFromDirectProperty(element, "pk_attrs");

        // A CHECK expression is sliced from the statement as written; the renderer drops what it does not know (G-73). The tree is kept
        // for the canonical comparison.
        var isCheck = string.Equals(constraintType, "CONSTR_CHECK", StringComparison.OrdinalIgnoreCase);
        var rawCheckExpression = TryGetDirectProperty(element, "raw_expr", out var rawExpressionElement)
            ? (isCheck && TryGetDirectInt32Property(element, "location", out var constraintLocation)
                ? ConstraintIndexTextReader.TryReadCheckExpression(statementSql, constraintLocation - statementOffset)
                : null) ?? ReadExpressionText(rawExpressionElement)
            : null;
        var checkExpressionAst = isCheck && TryGetDirectProperty(element, "raw_expr", out var checkAstElement)
            ? checkAstElement.GetRawText()
            : null;

        var isDeferrable = TryGetDirectBooleanProperty(element, "deferrable", out var deferrableValue) && deferrableValue;
        bool? isInitiallyDeferred = TryGetDirectBooleanProperty(element, "initdeferred", out var initiallyDeferredValue)
            ? initiallyDeferredValue
            : null;

        // pg_query (PostgreSQL 18) writes is_enforced only for an enforced CHECK or FOREIGN KEY constraint; one written NOT ENFORCED lacks it.
        // In CREATE TABLE the NOT ENFORCED may instead be a separate attribute entry, folded on by TryApplyConstraintAttribute.
        var isEnforced = !(isCheck || string.Equals(constraintType, "CONSTR_FOREIGN", StringComparison.OrdinalIgnoreCase))
            || (TryGetDirectBooleanProperty(element, "is_enforced", out var enforcedValue) && enforcedValue);

        var exclusionAccessMethod = TryGetDirectStringProperty(element, "access_method");
        var exclusionElements = ExtractExclusionElements(element);
        var rawExclusionPredicateExpression = TryGetDirectProperty(element, "where_clause", out var whereClauseElement)
            ? ReadExpressionText(whereClauseElement)
            : null;

        return new ConstraintNode(constraintName, schemaName, tableName, constraintType)
        {
            // Where the constraint and a foreign key's referenced table are written (G-309e).
            Range = ConstraintRangeAt(LocationOf(element, "Constraint")),
            ReferencedTableRange = TryGetDirectProperty(element, "pktable", out var placedTable) ? NameRangeAt(LocationOf(placedTable, "RangeVar")) : null,
            KeyColumns = keyColumns,
            ForeignKeyReferencedSchemaName = fkSchemaName,
            ForeignKeyReferencedTableName = fkTableName,
            ForeignKeyColumns = fkColumns,
            ForeignKeyReferencedColumns = fkReferencedColumns,
            ForeignKeyUpdateAction = TryGetDirectStringProperty(element, "fk_upd_action"),
            ForeignKeyDeleteAction = TryGetDirectStringProperty(element, "fk_del_action"),
            ForeignKeyDeleteSetColumns = TryGetStringListFromDirectProperty(element, "fk_del_set_cols"),
            ForeignKeyMatchType = TryGetDirectStringProperty(element, "fk_matchtype"),
            RawCheckExpression = rawCheckExpression,
            CheckExpressionAst = checkExpressionAst,
            CheckReferencedColumnNames = TryGetDirectProperty(element, "raw_expr", out var checkExpressionElement)
                ? CollectReferencedColumnNames(checkExpressionElement)
                : [],
            IsDeferrable = isDeferrable,
            IsInitiallyDeferred = isInitiallyDeferred,
            IsEnforced = isEnforced,
            IsNotValid = isEnforced && (isCheck || string.Equals(constraintType, "CONSTR_FOREIGN", StringComparison.OrdinalIgnoreCase))
                && TryGetDirectBooleanProperty(element, "skip_validation", out var skipValidation) && skipValidation,
            NullsNotDistinct = TryGetDirectBooleanProperty(element, "nulls_not_distinct", out var constraintNullsNotDistinct) && constraintNullsNotDistinct,
            WithoutOverlaps = TryGetDirectBooleanProperty(element, "without_overlaps", out var withoutOverlaps) && withoutOverlaps,
            ForeignKeyUsesPeriod = TryGetDirectBooleanProperty(element, "fk_with_period", out var fkWithPeriod) && fkWithPeriod,
            ExclusionAccessMethod = exclusionAccessMethod,
            ExclusionElements = exclusionElements,
            RawExclusionPredicateExpression = rawExclusionPredicateExpression,
            IsNoInherit = TryGetDirectBooleanProperty(element, "is_no_inherit", out var isNoInherit) && isNoInherit,
            // The WITH (...) of a key or exclusion constraint's index, each option as written (G-195).
            IndexStorageOptions = ExtractStorageOptions(element, statementSql, statementOffset),
            // INCLUDE (...) of a key or exclusion constraint's index (G-200).
            IncludeColumns = TryGetStringListFromDirectProperty(element, "including"),
        };
    }

    private static void MarkCreateTableConstraints(ICollection<SqlNode> nodes, int firstChildIndex, bool isPartitionOfList = false)
    {
        if (nodes is not IList<SqlNode> nodeList)
        {
            return;
        }

        for (var i = firstChildIndex; i < nodeList.Count; i++)
        {
            if (nodeList[i] is ConstraintNode constraint)
            {
                // A constraint in the element list of PARTITION OF parent (...) is made by the CREATE TABLE that deploys the list as written (G-266).
                nodeList[i] = constraint with { IsDeclaredInCreateTable = true, IsInPartitionOfList = isPartitionOfList };
            }
            else if (isPartitionOfList && nodeList[i] is NotNullConstraintNode notNull)
            {
                nodeList[i] = notNull with { IsInPartitionOfList = true };
            }
        }
    }

    /// <summary>
    /// PostgreSQL silently discards a PRIMARY KEY/UNIQUE constraint declared in <c>CREATE TABLE</c> when an
    /// earlier one covers the same columns in the same order (the primary key is always considered first),
    /// so the deployed table never contains it. If the discarded constraint carried an explicit name and the
    /// surviving one is unnamed, the name transfers to the survivor, as PostgreSQL does. Mirrors the
    /// redundancy check in <c>transformIndexConstraints</c>. Constraints added by <c>ALTER TABLE</c> are kept.
    /// </summary>
    private static void RemoveRedundantCreateTableKeys(List<SqlNode> nodes)
    {
        static bool IsKey(ConstraintNode node)
            => node.IsDeclaredInCreateTable
                && node.KeyColumns.Count > 0
                && !string.IsNullOrWhiteSpace(node.TableName)
                && (string.Equals(node.ConstraintType, DefaultConstraintNamer.PrimaryKeyConstraintType, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(node.ConstraintType, DefaultConstraintNamer.UniqueConstraintType, StringComparison.OrdinalIgnoreCase));

        var keyIndexesByTable = new Dictionary<string, List<int>>(PgProj.Parser.SqlIdentifiers.NameComparer);
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is ConstraintNode constraint && IsKey(constraint))
            {
                var tableKey = $"{constraint.SchemaName}.{constraint.TableName}";
                if (!keyIndexesByTable.TryGetValue(tableKey, out var indexes))
                {
                    indexes = [];
                    keyIndexesByTable[tableKey] = indexes;
                }

                indexes.Add(i);
            }
        }

        var removed = new HashSet<int>();
        foreach (var indexes in keyIndexesByTable.Values)
        {
            // The primary key is placed first, then the remaining keys in declaration order.
            var ordered = indexes
                .OrderBy(index => string.Equals(((ConstraintNode)nodes[index]).ConstraintType, DefaultConstraintNamer.PrimaryKeyConstraintType, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(static index => index)
                .ToList();

            var kept = new List<int>();
            foreach (var index in ordered)
            {
                var candidate = (ConstraintNode)nodes[index];
                var survivorIndex = kept.FirstOrDefault(
                    keptIndex => ((ConstraintNode)nodes[keptIndex]).KeyColumns.SequenceEqual(candidate.KeyColumns, PgProj.Parser.SqlIdentifiers.NameComparer),
                    -1);

                if (survivorIndex < 0)
                {
                    kept.Add(index);
                    continue;
                }

                var survivor = (ConstraintNode)nodes[survivorIndex];
                if (string.IsNullOrWhiteSpace(survivor.Name) && !string.IsNullOrWhiteSpace(candidate.Name))
                {
                    nodes[survivorIndex] = survivor with { Name = candidate.Name };
                }

                removed.Add(index);
            }
        }

        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            if (removed.Contains(i))
            {
                nodes.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Collects the distinct column names referenced (as unqualified or qualified column references) by an
    /// expression, in order of first appearance. Only the final name part of a reference is kept.
    /// </summary>
    private static IReadOnlyList<string> CollectReferencedColumnNames(JsonElement expression)
    {
        var names = new List<string>();
        CollectReferencedColumnNames(expression, names);
        return names;
    }

    private static void CollectReferencedColumnNames(JsonElement element, List<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "ColumnRef"
                        && TryGetDirectProperty(property.Value, "fields", out var fields)
                        && fields.ValueKind == JsonValueKind.Array)
                    {
                        var lastField = fields.EnumerateArray().LastOrDefault();
                        if (lastField.ValueKind == JsonValueKind.Object
                            && TryGetDirectProperty(lastField, "String", out var stringNode)
                            && TryGetDirectStringProperty(stringNode, "sval") is { Length: > 0 } columnName
                            && !names.Contains(columnName, StringComparer.Ordinal))
                        {
                            names.Add(columnName);
                        }

                        continue;
                    }

                    CollectReferencedColumnNames(property.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    CollectReferencedColumnNames(child, names);
                }

                break;
        }
    }

    private static IReadOnlyList<ConstraintExclusionElementNode> ExtractExclusionElements(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "exclusions", out var exclusionsElement) || exclusionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<ConstraintExclusionElementNode>();
        foreach (var exclusionEntry in exclusionsElement.EnumerateArray())
        {
            if (!TryGetDirectProperty(exclusionEntry, "List", out var listElement)
                || !TryGetDirectProperty(listElement, "items", out var itemsElement)
                || itemsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var items = itemsElement.EnumerateArray().ToArray();
            if (items.Length < 2)
            {
                continue;
            }

            var indexElemContainer = items[0];
            if (!TryGetDirectProperty(indexElemContainer, "IndexElem", out var indexElem))
            {
                continue;
            }

            var name = TryGetDirectStringProperty(indexElem, "name");
            string? rawExpression = null;
            string? expressionAst = null;
            if (string.IsNullOrWhiteSpace(name) && TryGetDirectProperty(indexElem, "expr", out var exprElement))
            {
                rawExpression = ReadExpressionText(exprElement);
                expressionAst = exprElement.GetRawText();
            }

            var operatorName = ExtractQualifiedOperatorName(items[1]);

            results.Add(new ConstraintExclusionElementNode(name, rawExpression, operatorName) { ExpressionAst = expressionAst });
        }

        return results;
    }

    private static string ExtractQualifiedOperatorName(JsonElement operatorListContainer)
    {
        if (!TryGetDirectProperty(operatorListContainer, "List", out var listElement)
            || !TryGetDirectProperty(listElement, "items", out var itemsElement)
            || itemsElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var item in itemsElement.EnumerateArray())
        {
            var value = TryGetStringPropertyRecursive(item, "sval") ?? TryGetStringPropertyRecursive(item, "str");
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value);
            }
        }

        return string.Join(".", parts);
    }

    private static string? TryGetConstraintType(JsonElement element)
    {
        if (!TryFindPropertyRecursive(element, "contype", out var constraintType))
        {
            return null;
        }

        return constraintType.ValueKind switch
        {
            JsonValueKind.Number when constraintType.TryGetInt32(out var intValue) => intValue.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => constraintType.GetString(),
            _ => null,
        };
    }

    private static IEnumerable<JsonElement> EnumerateConstraintNodes(JsonElement constraintsElement)
    {
        if (constraintsElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var constraintElement in constraintsElement.EnumerateArray())
        {
            if (TryGetDirectProperty(constraintElement, "Constraint", out var constraintNode))
            {
                yield return constraintNode;
                continue;
            }

            if (constraintElement.ValueKind == JsonValueKind.Object)
            {
                yield return constraintElement;
            }
        }
    }
}
