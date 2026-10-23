using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>
/// Shared helpers for emitting <c>COMMENT ON ...</c> SQL for tables, columns, views, routines,
/// indexes, sequences, and types (G-24a and G-34).
/// </summary>
internal static class CommentSqlHelper
{
    public static string? GenerateTableComment(string? schemaName, string tableName, string? comment)
    {
        var qualifiedName = FormatQualifiedName(schemaName, tableName);
        return comment is null
            ? null
            : $"COMMENT ON TABLE {qualifiedName} IS {QuoteLiteral(comment)};";
    }

    public static string GenerateColumnComment(string? schemaName, string tableName, string columnName, string? comment)
    {
        var qualifiedName = FormatQualifiedName(schemaName, tableName);
        var value = comment is null ? "NULL" : QuoteLiteral(comment);
        return $"COMMENT ON COLUMN {qualifiedName}.{QuoteIdentifier(columnName)} IS {value};";
    }

    /// <summary><c>COMMENT ON TRIGGER name ON table</c>, or <c>IS NULL</c> for no comment (G-163).</summary>
    public static string GenerateTriggerComment(string? schemaName, string tableName, string triggerName, string? comment)
        => $"COMMENT ON TRIGGER {QuoteIdentifier(triggerName)} ON {FormatQualifiedName(schemaName, tableName)} IS {(comment is null ? "NULL" : QuoteLiteral(comment))};";

    /// <summary><c>COMMENT ON POLICY name ON table</c>, or <c>IS NULL</c> for no comment (G-164).</summary>
    public static string GeneratePolicyComment(string? schemaName, string tableName, string policyName, string? comment)
        => $"COMMENT ON POLICY {QuoteIdentifier(policyName)} ON {FormatQualifiedName(schemaName, tableName)} IS {(comment is null ? "NULL" : QuoteLiteral(comment))};";

    /// <summary><c>COMMENT ON SCHEMA</c>, or <c>IS NULL</c> for no comment (G-160).</summary>
    /// <summary>
    /// <c>COMMENT ON COLUMN relation.column</c> for each column of a view, materialized view, or composite type whose comment differs between
    /// <paramref name="before"/> and <paramref name="after"/>, <c>IS NULL</c> for one removed (G-216).
    /// </summary>
    public static IReadOnlyList<string> GenerateColumnCommentChanges(string? schemaName, string relationName, IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
        => [.. ColumnCommentMap.Changes(before, after).Select(change =>
            $"COMMENT ON COLUMN {FormatQualifiedName(schemaName, relationName)}.{QuoteIdentifier(change.Column)} IS {(change.Comment is null ? "NULL" : QuoteLiteral(change.Comment))};")];

    /// <summary><c>COMMENT ON EXTENSION name</c>, or <c>IS NULL</c> for no comment (G-213).</summary>
    public static string GenerateExtensionComment(string extensionName, string? comment)
        => $"COMMENT ON EXTENSION {QuoteIdentifier(extensionName)} IS {(comment is null ? "NULL" : QuoteLiteral(comment))};";

    public static string GenerateSchemaComment(string schemaName, string? comment)
        => $"COMMENT ON SCHEMA {QuoteIdentifier(schemaName)} IS {(comment is null ? "NULL" : QuoteLiteral(comment))};";

    /// <summary><c>COMMENT ON CONSTRAINT name ON table</c>, or <c>IS NULL</c> for no comment (G-161).</summary>
    public static string GenerateConstraintComment(string? schemaName, string tableName, string constraintName, string? comment)
        => $"COMMENT ON CONSTRAINT {QuoteIdentifier(constraintName)} ON {FormatQualifiedName(schemaName, tableName)} IS {(comment is null ? "NULL" : QuoteLiteral(comment))};";

    /// <summary><c>COMMENT ON INDEX</c> for the index of a primary key, unique, or exclusion constraint, which has the constraint's name, or <c>IS NULL</c> for no comment (G-295).</summary>
    public static string GenerateConstraintIndexComment(string? schemaName, string constraintName, string? comment)
        => $"COMMENT ON INDEX {FormatQualifiedName(schemaName, constraintName)} IS {(comment is null ? "NULL" : QuoteLiteral(comment))};";

    /// <summary>
    /// The comment on a column's NOT NULL constraint (G-235), which only PostgreSQL 18 stores: a <c>DO</c> block runs it where the constraint exists, so the same script
    /// serves an older server, where the statement would fail.
    /// </summary>
    public static string GenerateNotNullConstraintComment(string? schemaName, string tableName, string constraintName, string? comment)
        => "DO $pgproj$\nBEGIN\n    IF current_setting('server_version_num')::int >= 180000 THEN\n        "
            + GenerateConstraintComment(schemaName, tableName, constraintName, comment)
            + "\n    END IF;\nEND\n$pgproj$;";

    /// <summary><c>ALTER TABLE t { ENABLE | DISABLE | ENABLE REPLICA | ENABLE ALWAYS } TRIGGER name</c> (G-257).</summary>
    public static string GenerateTriggerState(string? schemaName, string tableName, string triggerName, string? state)
        => $"ALTER TABLE {FormatQualifiedName(schemaName, tableName)} {state ?? "ENABLE"} TRIGGER {QuoteIdentifier(triggerName)};";

    /// <summary>
    /// The statements that set a trigger's copy on each partition the model states a mode for (G-293), in name order; with <paramref name="previous"/>, only those that differ from it (a copy it does not state is enabled).
    /// </summary>
    public static IEnumerable<string> GeneratePartitionTriggerStates(string triggerName, IReadOnlyDictionary<string, string> states, IReadOnlyDictionary<string, string>? previous = null)
        => states
            .Where(pair => previous is null || !string.Equals(previous.GetValueOrDefault(pair.Key) ?? "ENABLE", pair.Value, StringComparison.Ordinal))
            .Select(pair => $"ALTER TABLE {FormatPartitionName(pair.Key)} {pair.Value} TRIGGER {QuoteIdentifier(triggerName)};");

    private static string FormatPartitionName(string key)
    {
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? FormatQualifiedName(null, key) : FormatQualifiedName(key[..dot], key[(dot + 1)..]);
    }

    /// <summary><c>ALTER TABLE t { ENABLE | DISABLE | ENABLE REPLICA | ENABLE ALWAYS } RULE name</c> (G-257).</summary>
    public static string GenerateRuleState(string? schemaName, string tableName, string ruleName, string? state)
        => $"ALTER TABLE {FormatQualifiedName(schemaName, tableName)} {state ?? "ENABLE"} RULE {QuoteIdentifier(ruleName)};";

    /// <summary><c>ALTER TABLE t { ENABLE | DISABLE } TRIGGER ALL</c> (G-261).</summary>
    public static string GenerateTriggerAllState(string? schemaName, string tableName, string? state)
        => $"ALTER TABLE {FormatQualifiedName(schemaName, tableName)} {state ?? "ENABLE"} TRIGGER ALL;";

    /// <summary><c>ALTER TABLE t CLUSTER ON index</c> (G-256).</summary>
    public static string GenerateClusterOn(string? schemaName, string tableName, string indexName)
        => $"ALTER TABLE {FormatQualifiedName(schemaName, tableName)} CLUSTER ON {QuoteIdentifier(indexName)};";

    /// <summary><c>ALTER TABLE t SET WITHOUT CLUSTER</c> (G-260).</summary>
    public static string GenerateWithoutCluster(string? schemaName, string tableName)
        => $"ALTER TABLE {FormatQualifiedName(schemaName, tableName)} SET WITHOUT CLUSTER;";

    /// <summary><c>ALTER TABLE t REPLICA IDENTITY { DEFAULT | FULL | NOTHING }</c> (G-255).</summary>
    public static string GenerateReplicaIdentity(string? schemaName, string tableName, string mode)
        => $"ALTER TABLE {FormatQualifiedName(schemaName, tableName)} REPLICA IDENTITY {mode};";

    /// <summary><c>ALTER TABLE t REPLICA IDENTITY USING INDEX index</c> (G-255).</summary>
    public static string GenerateReplicaIdentityUsingIndex(string? schemaName, string tableName, string indexName)
        => $"ALTER TABLE {FormatQualifiedName(schemaName, tableName)} REPLICA IDENTITY USING INDEX {QuoteIdentifier(indexName)};";

    public static string GenerateTableCommentClear(string? schemaName, string tableName)
        => $"COMMENT ON TABLE {FormatQualifiedName(schemaName, tableName)} IS NULL;";

    public static string? GenerateViewComment(string? schemaName, string viewName, bool isMaterialized, string? comment)
        => comment is null
            ? null
            : $"COMMENT ON {(isMaterialized ? "MATERIALIZED VIEW" : "VIEW")} {FormatQualifiedName(schemaName, viewName)} IS {QuoteLiteral(comment)};";

    public static string GenerateViewCommentClear(string? schemaName, string viewName, bool isMaterialized)
        => $"COMMENT ON {(isMaterialized ? "MATERIALIZED VIEW" : "VIEW")} {FormatQualifiedName(schemaName, viewName)} IS NULL;";

    public static string? GenerateIndexComment(string? schemaName, string indexName, string? comment)
        => comment is null
            ? null
            : $"COMMENT ON INDEX {FormatQualifiedName(schemaName, indexName)} IS {QuoteLiteral(comment)};";

    public static string GenerateIndexCommentClear(string? schemaName, string indexName)
        => $"COMMENT ON INDEX {FormatQualifiedName(schemaName, indexName)} IS NULL;";

    public static string? GenerateSequenceComment(string? schemaName, string sequenceName, string? comment)
        => comment is null
            ? null
            : $"COMMENT ON SEQUENCE {FormatQualifiedName(schemaName, sequenceName)} IS {QuoteLiteral(comment)};";

    public static string GenerateSequenceCommentClear(string? schemaName, string sequenceName)
        => $"COMMENT ON SEQUENCE {FormatQualifiedName(schemaName, sequenceName)} IS NULL;";

    public static string? GenerateTypeComment(string? schemaName, string typeName, string? comment)
        => comment is null
            ? null
            : $"COMMENT ON TYPE {FormatQualifiedName(schemaName, typeName)} IS {QuoteLiteral(comment)};";

    public static string GenerateTypeCommentClear(string? schemaName, string typeName)
        => $"COMMENT ON TYPE {FormatQualifiedName(schemaName, typeName)} IS NULL;";

    public static string? GenerateFunctionComment(string? schemaName, string functionName, string? argumentTypeSignature, string? comment)
        => comment is null
            ? null
            : $"COMMENT ON FUNCTION {FormatQualifiedName(schemaName, functionName)}({argumentTypeSignature}) IS {QuoteLiteral(comment)};";

    public static string GenerateFunctionCommentClear(string? schemaName, string functionName, string? argumentTypeSignature)
        => $"COMMENT ON FUNCTION {FormatQualifiedName(schemaName, functionName)}({argumentTypeSignature}) IS NULL;";

    public static string? GenerateProcedureComment(string? schemaName, string procedureName, string? argumentTypeSignature, string? comment)
        => comment is null
            ? null
            : $"COMMENT ON PROCEDURE {FormatQualifiedName(schemaName, procedureName)}({argumentTypeSignature}) IS {QuoteLiteral(comment)};";

    public static string GenerateProcedureCommentClear(string? schemaName, string procedureName, string? argumentTypeSignature)
        => $"COMMENT ON PROCEDURE {FormatQualifiedName(schemaName, procedureName)}({argumentTypeSignature}) IS NULL;";

    /// <summary><c>COMMENT ON keyword name IS 'text'</c> for an object kind named by its SQL keywords (<c>COLLATION</c>, <c>TEXT SEARCH DICTIONARY</c>); null when there is no comment (G-154).</summary>
    public static string? GenerateObjectComment(string keyword, string? schemaName, string name, string? comment)
        => comment is null ? null : $"COMMENT ON {keyword} {FormatQualifiedName(schemaName, name)} IS {QuoteLiteral(comment)};";

    public static string GenerateObjectCommentClear(string keyword, string? schemaName, string name)
        => $"COMMENT ON {keyword} {FormatQualifiedName(schemaName, name)} IS NULL;";

    private static string FormatQualifiedName(string? schemaName, string name)
        => string.IsNullOrWhiteSpace(schemaName)
            ? QuoteIdentifier(name)
            : $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(name)}";

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    internal static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";
}

