using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PgProj.Parser;

/// <summary>
/// Decides whether a top-level statement is one pgproj models (G-106). The parser's dispatch used to skip a statement it had no
/// case for without a trace, so <c>build</c> passed a project whose event triggers, rules, data statements, and so on were never
/// deployed. Every top-level statement pgproj does not model is now reported as a warning that names the statement, and its
/// contents are not read (so a <c>CREATE FOREIGN TABLE</c>'s columns or an <c>ALTER DOMAIN</c>'s constraint no longer leak into
/// the model as stray nodes).
/// </summary>
/// <remarks>
/// Deliberately silent, by user decision (2026-09-25): transaction control, <c>SET</c>/<c>RESET</c> of anything but
/// <c>search_path</c> (a session setting has nothing to deploy), and a plain <c>CREATE SCHEMA</c> (schemas are inferred from the
/// objects in them). Every warning is a <see cref="SqlDiagnosticSeverity.Warning"/>, including data and code statements, which
/// point to <c>predeploy</c>/<c>postdeploy</c>. An <c>ALTER TABLE</c> is handled per action (G-47) and a <c>REVOKE</c> by the grant
/// model (G-65), so neither is reported here.
/// </remarks>
internal static class UnmodeledStatementReader
{
    /// <summary>The <see cref="SqlDiagnostic.Source"/> of the diagnostics this class produces.</summary>
    public const string DiagnosticSource = "UnmodeledStatement";

    private const int SnippetLength = 80;

    private const string Ignored = "is not modeled; the statement is ignored";

    private const string IgnoredScript = "is not modeled; the statement is ignored (to run it on every publish, put it in the predeploy or postdeploy script)";

    /// <summary>What to report about a statement, and whether the parser should still read it.</summary>
    internal sealed record Finding(string Kind, string Consequence, bool ReadStatement = false);

    private static readonly HashSet<string> ModeledStatements = new(StringComparer.Ordinal)
    {
        "AlterTableStmt",
        "CreateFunctionStmt",
        "CreateTrigStmt",
        "CreatePolicyStmt",
        "RuleStmt",
        "CreatePublicationStmt",
        "AlterPublicationStmt",
        "CreateStatsStmt",
        "CreateEventTrigStmt",
        "AlterEventTrigStmt",
        "CreateCastStmt",
        "IndexStmt",
        "GrantStmt",
        "GrantRoleStmt",
        "AlterDefaultPrivilegesStmt",
        "CompositeTypeStmt",
        "CreateEnumStmt",
        "CreateRangeStmt",
        "CreateDomainStmt",
        "CreateExtensionStmt",
        "TransactionStmt",
    };

    /// <summary>The object kinds a <c>COMMENT ON</c> is read for (<c>PgQueryParser.TryCreateObjectCommentNode</c>).</summary>
    private static readonly HashSet<string> ModeledCommentTargets = new(StringComparer.Ordinal)
    {
        "OBJECT_TABLE",
        "OBJECT_COLUMN",
        "OBJECT_VIEW",
        "OBJECT_MATVIEW",
        "OBJECT_INDEX",
        "OBJECT_SEQUENCE",
        "OBJECT_TYPE",
        "OBJECT_DOMAIN",
        "OBJECT_FUNCTION",
        "OBJECT_PROCEDURE",
        "OBJECT_COLLATION",
        "OBJECT_SCHEMA",
        "OBJECT_TABCONSTRAINT",
        "OBJECT_TRIGGER",
        "OBJECT_POLICY",
        "OBJECT_STATISTIC_EXT",
        "OBJECT_TSDICTIONARY",
        "OBJECT_TSCONFIGURATION",
        "OBJECT_EXTENSION",
    };

    /// <summary>Statements that change data or run code: pgproj never runs them, but a pre- or post-deploy script can.</summary>
    private static readonly HashSet<string> ScriptStatements = new(StringComparer.Ordinal)
    {
        "InsertStmt",
        "UpdateStmt",
        "DeleteStmt",
        "MergeStmt",
        "TruncateStmt",
        "CopyStmt",
        "SelectStmt",
        "DoStmt",
        "CallStmt",
        "DropStmt",
        "CreateTableAsStmt",
        "RefreshMatViewStmt",
    };

    private static readonly Dictionary<string, string> StatementKinds = new(StringComparer.Ordinal)
    {
        ["CreateSubscriptionStmt"] = "CREATE SUBSCRIPTION",
        ["AlterSubscriptionStmt"] = "ALTER SUBSCRIPTION",
        ["CreateFdwStmt"] = "CREATE FOREIGN DATA WRAPPER",
        ["AlterFdwStmt"] = "ALTER FOREIGN DATA WRAPPER",
        ["CreateForeignServerStmt"] = "CREATE SERVER",
        ["AlterForeignServerStmt"] = "ALTER SERVER",
        ["CreateUserMappingStmt"] = "CREATE USER MAPPING",
        ["AlterUserMappingStmt"] = "ALTER USER MAPPING",
        ["CreateForeignTableStmt"] = "CREATE FOREIGN TABLE",
        ["ImportForeignSchemaStmt"] = "IMPORT FOREIGN SCHEMA",
        ["CreateRoleStmt"] = "CREATE ROLE",
        ["AlterRoleStmt"] = "ALTER ROLE",
        ["AlterRoleSetStmt"] = "ALTER ROLE ... SET",
        ["DropRoleStmt"] = "DROP ROLE",
        ["AlterStatsStmt"] = "ALTER STATISTICS",
        ["CreateTableSpaceStmt"] = "CREATE TABLESPACE",
        ["CreateTransformStmt"] = "CREATE TRANSFORM",
        ["CreatePLangStmt"] = "CREATE LANGUAGE",
        ["CreateAmStmt"] = "CREATE ACCESS METHOD",
        ["CreateConversionStmt"] = "CREATE CONVERSION",
        ["CreateOpClassStmt"] = "CREATE OPERATOR CLASS",
        ["CreateOpFamilyStmt"] = "CREATE OPERATOR FAMILY",
        ["AlterOpFamilyStmt"] = "ALTER OPERATOR FAMILY",
        ["AlterOperatorStmt"] = "ALTER OPERATOR",
        ["AlterCollationStmt"] = "ALTER COLLATION",
        ["SecLabelStmt"] = "SECURITY LABEL",
        ["CreatedbStmt"] = "CREATE DATABASE",
        ["AlterDatabaseStmt"] = "ALTER DATABASE",
        ["AlterDatabaseSetStmt"] = "ALTER DATABASE ... SET",
        ["AlterSystemStmt"] = "ALTER SYSTEM",
        ["AlterSeqStmt"] = "ALTER SEQUENCE",
        ["AlterOwnerStmt"] = "ALTER ... OWNER TO",
        ["AlterEnumStmt"] = "ALTER TYPE ... VALUE",
        ["AlterDomainStmt"] = "ALTER DOMAIN",
        ["AlterTypeStmt"] = "ALTER TYPE ... SET",
        ["RenameStmt"] = "ALTER ... RENAME",
        ["AlterObjectSchemaStmt"] = "ALTER ... SET SCHEMA",
        ["AlterObjectDependsStmt"] = "ALTER ... DEPENDS ON EXTENSION",
        ["AlterExtensionStmt"] = "ALTER EXTENSION",
        ["AlterExtensionContentsStmt"] = "ALTER EXTENSION ... ADD/DROP",
        ["AlterPolicyStmt"] = "ALTER POLICY",
        ["ReassignOwnedStmt"] = "REASSIGN OWNED",
        ["DropOwnedStmt"] = "DROP OWNED",
        ["InsertStmt"] = "INSERT",
        ["UpdateStmt"] = "UPDATE",
        ["DeleteStmt"] = "DELETE",
        ["MergeStmt"] = "MERGE",
        ["TruncateStmt"] = "TRUNCATE",
        ["CopyStmt"] = "COPY",
        ["DoStmt"] = "DO",
        ["CallStmt"] = "CALL",
        ["RefreshMatViewStmt"] = "REFRESH MATERIALIZED VIEW",
        ["VacuumStmt"] = "VACUUM/ANALYZE",
        ["ClusterStmt"] = "CLUSTER",
        ["ReindexStmt"] = "REINDEX",
        ["LockStmt"] = "LOCK",
        ["NotifyStmt"] = "NOTIFY",
        ["ListenStmt"] = "LISTEN",
        ["UnlistenStmt"] = "UNLISTEN",
        ["CheckPointStmt"] = "CHECKPOINT",
        ["DiscardStmt"] = "DISCARD",
        ["PrepareStmt"] = "PREPARE",
        ["ExecuteStmt"] = "EXECUTE",
        ["DeallocateStmt"] = "DEALLOCATE",
        ["ExplainStmt"] = "EXPLAIN",
        ["VariableShowStmt"] = "SHOW",
        ["LoadStmt"] = "LOAD",
    };

    /// <summary>The names of the statements reported by kind (G-300: the notes list every one, and a test checks it).</summary>
    internal static IReadOnlyCollection<string> ReportedStatementKinds => StatementKinds.Values;

    /// <summary>
    /// What to report about a top-level statement (the value of pg_query's <c>stmt</c>), or <see langword="null"/> when pgproj
    /// models it or it is deliberately silent.
    /// </summary>
    public static Finding? Classify(JsonElement statement)
    {
        if (statement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in statement.EnumerateObject())
        {
            return Classify(property.Name, property.Value);
        }

        return null;
    }

    private static Finding? Classify(string name, JsonElement body)
    {
        switch (name)
        {
            case "CreateStmt":
                return TemporaryObject(body, "relation", "TABLE");
            case "ViewStmt":
                return TemporaryObject(body, "view", "VIEW");
            case "CreateSeqStmt":
                return TemporaryObject(body, "sequence", "SEQUENCE");
            case "AlterTableStmt":
                // ALTER VIEW/MATERIALIZED VIEW/INDEX/SEQUENCE/TYPE/FOREIGN TABLE share this node. Only a table's actions are modeled
                // (per action, G-47); for every other kind nothing is, and calling it a table (as G-47's warning did) misleads.
                var alterObjectKind = String(body, "objtype");
                // The one thing read for a view, materialized view, or sequence is its owner (G-146), when that is all the statement does.
                // ALTER INDEX ... ATTACH PARTITION is modeled (G-236), and so is a view column's SET|DROP DEFAULT (G-291).
                return alterObjectKind is null or "OBJECT_TABLE" || OwnerStatementReader.IsReadOwnerOnlyAlterTable(body) || PgQueryParser.IsAlterIndexAttachPartition(body) || PgQueryParser.IsAlterViewColumnDefault(body)
                    ? null
                    : new Finding($"ALTER {ObjectKind(alterObjectKind)}", Ignored);
            case "AlterTSConfigurationStmt":
                // ADD, ALTER, and DROP MAPPING FOR a token type are modeled (G-149); the REPLACE forms are not.
                return PgQueryParser.IsModeledTextSearchMappingKind(String(body, "kind"))
                    ? null
                    : new Finding("ALTER TEXT SEARCH CONFIGURATION ... REPLACE", Ignored);
            case "AlterTSDictionaryStmt":
                return null;
            case "AlterOwnerStmt":
                // The owner of a schema, routine, aggregate, operator, type, domain, event trigger, or publication is modeled (G-146); another
                // kind of object (a collation, a foreign data wrapper, ...) still is not, and neither is an owner that is not a named role.
                return OwnerStatementReader.IsReadAlterOwner(body)
                    ? null
                    : new Finding("ALTER ... OWNER TO", Ignored);
            case "CreateTableAsStmt":
                return String(body, "objtype") == "OBJECT_MATVIEW"
                    ? null
                    : new Finding("CREATE TABLE AS", IgnoredScript);
            case "SelectStmt":
                return new Finding(body.TryGetProperty("intoClause", out _) ? "SELECT INTO" : "SELECT", IgnoredScript);
            case "AlterSeqStmt":
                // ALTER SEQUENCE ... OWNED BY is modeled (G-236); every other option is still not.
                return PgQueryParser.IsAlterSequenceOwnedBy(body, out _) ? null : new Finding("ALTER SEQUENCE", Ignored);
            case "AlterDomainStmt":
                // ADD CONSTRAINT ... CHECK is modeled (G-233); every other action of ALTER DOMAIN is still not.
                return TypeStatementReader.IsAlterDomainCheck(body, out _) ? null : new Finding("ALTER DOMAIN", Ignored);
            case "DropStmt":
                return new Finding($"DROP {ObjectKind(String(body, "removeType"))}", IgnoredScript);
            case "DefineStmt":
                var defineKind = String(body, "kind");
                // A collation made with options is modeled (G-148); one made FROM another has no attributes of its own to compare.
                if (defineKind == "OBJECT_COLLATION" && PgQueryParser.IsCollationFromForm(body))
                {
                    return new Finding("CREATE COLLATION ... FROM", Ignored);
                }

                // A base type or shell type is not modeled (G-241): it needs C or internal functions and a superuser. The statement is still read, so its
                // support functions can be left out with it (BaseTypeApplier).
                if (defineKind == "OBJECT_TYPE")
                {
                    return body.TryGetProperty("definition", out var typeDefinition) && typeDefinition.ValueKind == JsonValueKind.Array && typeDefinition.GetArrayLength() > 0
                        ? new Finding("CREATE TYPE (base type)", "is not modeled (a base type needs C or internal functions and a superuser); the statement and the functions it names are ignored", ReadStatement: true)
                        : new Finding("CREATE TYPE (shell type)", Ignored, ReadStatement: true);
                }

                return defineKind is "OBJECT_OPERATOR" or "OBJECT_AGGREGATE" or "OBJECT_COLLATION" or "OBJECT_TSDICTIONARY" or "OBJECT_TSCONFIGURATION" ? null : new Finding($"CREATE {ObjectKind(defineKind)}", Ignored);
            case "CommentStmt":
                var commentTarget = String(body, "objtype");
                return commentTarget is not null && ModeledCommentTargets.Contains(commentTarget)
                    ? null
                    : new Finding($"COMMENT ON {ObjectKind(commentTarget)}", Ignored);
            case "CreateSchemaStmt":
                // A named owner is modeled (G-146); CURRENT_USER and the like name whoever runs the script, which cannot be compared.
                return body.TryGetProperty("authrole", out _) && !OwnerStatementReader.IsReadCreateSchemaOwner(body)
                    ? new Finding("CREATE SCHEMA ... AUTHORIZATION", "is not modeled; the schema's owner is ignored", ReadStatement: true)
                    : null;
            case "AlterFunctionStmt":
                return new Finding($"ALTER {ObjectKind(String(body, "objtype"))}", Ignored);
            case "VariableSetStmt":
                return string.Equals(String(body, "name"), "search_path", StringComparison.OrdinalIgnoreCase)
                    ? new Finding(
                        String(body, "kind") is "VAR_RESET" or "VAR_RESET_ALL" ? "RESET search_path" : "SET search_path",
                        "is not modeled; the statement is ignored, and unqualified names follow the project's searchPath")
                    : null;
        }

        if (ModeledStatements.Contains(name))
        {
            return null;
        }

        return new Finding(
            StatementKinds.TryGetValue(name, out var kind) ? kind : StatementName(name),
            ScriptStatements.Contains(name) ? IgnoredScript : Ignored);
    }

    /// <summary>
    /// A <c>TEMP</c>/<c>TEMPORARY</c> table, view, or sequence (pg_query's <c>relpersistence</c> <c>t</c>) exists only for the session
    /// that creates it, so it is not part of a schema; it was modeled as a permanent object in the default schema (G-117).
    /// </summary>
    private static Finding? TemporaryObject(JsonElement body, string relationProperty, string objectKind)
        => body.TryGetProperty(relationProperty, out var relation) && String(relation, "relpersistence") == "t"
            ? new Finding(
                $"CREATE TEMPORARY {objectKind}",
                "is not modeled (a temporary object lasts only for the session that creates it); the statement is ignored")
            : null;

    /// <summary>
    /// The statement as a short, single-line quotation, with any password masked before it is shortened (G-142), and its line number in <paramref name="sql"/>, both from its first word
    /// (pg_query's location includes any comment written before the statement).
    /// </summary>
    public static (string Snippet, int Line) Describe(string sql, int location, int length)
    {
        var (snippet, line, _) = DescribeWithColumn(sql, location, length);
        return (snippet, line);
    }

    /// <summary><see cref="Describe"/>, with the 1-based column of the statement's first word too (G-123).</summary>
    public static (string Snippet, int Line, int Column) DescribeWithColumn(string sql, int location, int length)
    {
        var range = DescribeWithRange(sql, location, length);
        return (range.Snippet, range.Line, range.Column);
    }

    /// <summary>A statement as <see cref="DescribeWithColumn"/> describes it, with where it ends too (G-308).</summary>
    public readonly record struct StatementRange(string Snippet, int Line, int Column, int EndLine, int EndColumn);

    /// <summary>
    /// <see cref="DescribeWithColumn"/>, with the line and column just past the statement's last character too (G-308): the statement
    /// without the whitespace after it and the <c>;</c> that ends it, which pg_query's length may or may not include.
    /// </summary>
    public static StatementRange DescribeWithRange(string sql, int location, int length)
    {
        if (location < 0 || location >= sql.Length)
        {
            location = 0;
        }

        var end = length <= 0 || location + length > sql.Length ? sql.Length : location + length;
        var start = SkipWhitespaceAndComments(sql, location, end);
        var text = SecretRedactor.Redact(CollapseWhitespace(sql[start..end]));
        if (text.Length > SnippetLength)
        {
            text = string.Concat(text.AsSpan(0, SnippetLength).TrimEnd(), "...");
        }

        var (line, column) = SqlTextPosition.OfIndex(sql, start);
        var (endLine, endColumn) = SqlTextPosition.OfIndex(sql, TrimStatementEnd(sql, start, end));
        return new StatementRange(text, line, column, endLine, endColumn);
    }

    /// <summary>The range of the statement at <paramref name="location"/>, as <see cref="DescribeWithRange"/> finds it, without the text (G-309e).</summary>
    public static SourceRange SourceRangeOf(string sql, int location, int length)
    {
        if (location < 0 || location >= sql.Length)
        {
            location = 0;
        }

        var end = length <= 0 || location + length > sql.Length ? sql.Length : location + length;
        var start = SkipWhitespaceAndComments(sql, location, end);
        return SourceRange.Of(sql, start, TrimStatementEnd(sql, start, end));
    }

    /// <summary>The end of the statement that runs from <paramref name="start"/> to <paramref name="end"/>, before its trailing whitespace and <c>;</c> (G-308).</summary>
    internal static int TrimStatementEnd(string sql, int start, int end)
    {
        end = Math.Clamp(end, start, sql.Length);
        while (end > start && char.IsWhiteSpace(sql[end - 1]))
        {
            end--;
        }

        if (end > start && sql[end - 1] == ';')
        {
            end--;
            while (end > start && char.IsWhiteSpace(sql[end - 1]))
            {
                end--;
            }
        }

        return end;
    }

    private static int SkipWhitespaceAndComments(string sql, int start, int end)
    {
        var index = start;
        while (index < end)
        {
            if (char.IsWhiteSpace(sql[index]))
            {
                index++;
            }
            else if (index + 1 < end && sql[index] == '-' && sql[index + 1] == '-')
            {
                var newline = sql.IndexOf('\n', index, end - index);
                index = newline < 0 ? end : newline + 1;
            }
            else if (index + 1 < end && sql[index] == '/' && sql[index + 1] == '*')
            {
                var close = sql.IndexOf("*/", index + 2, end - index - 2, StringComparison.Ordinal);
                index = close < 0 ? end : close + 2;
            }
            else
            {
                break;
            }
        }

        return index;
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string? String(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    /// <summary>pg_query's <c>OBJECT_*</c> name as SQL writes it (<c>OBJECT_MATVIEW</c> is <c>MATERIALIZED VIEW</c>).</summary>
    private static string ObjectKind(string? objectType)
        => objectType switch
        {
            null or "" => "an object",
            "OBJECT_MATVIEW" => "MATERIALIZED VIEW",
            "OBJECT_FDW" => "FOREIGN DATA WRAPPER",
            "OBJECT_FOREIGN_SERVER" => "SERVER",
            "OBJECT_TABCONSTRAINT" => "CONSTRAINT",
            "OBJECT_DOMCONSTRAINT" => "CONSTRAINT ... ON DOMAIN",
            "OBJECT_TSCONFIGURATION" => "TEXT SEARCH CONFIGURATION",
            "OBJECT_TSDICTIONARY" => "TEXT SEARCH DICTIONARY",
            "OBJECT_TSPARSER" => "TEXT SEARCH PARSER",
            "OBJECT_TSTEMPLATE" => "TEXT SEARCH TEMPLATE",
            "OBJECT_OPCLASS" => "OPERATOR CLASS",
            "OBJECT_OPFAMILY" => "OPERATOR FAMILY",
            "OBJECT_ACCESS_METHOD" => "ACCESS METHOD",
            "OBJECT_LARGEOBJECT" => "LARGE OBJECT",
            "OBJECT_STATISTIC_EXT" => "STATISTICS",
            "OBJECT_USER_MAPPING" => "USER MAPPING",
            "OBJECT_ATTRIBUTE" => "ATTRIBUTE",
            _ => objectType.StartsWith("OBJECT_", StringComparison.Ordinal)
                ? objectType["OBJECT_".Length..].Replace('_', ' ')
                : objectType,
        };

    /// <summary>A statement kind with no entry in the table above, from its pg_query name: <c>CreateFooStmt</c> is <c>CREATE FOO</c>.</summary>
    private static string StatementName(string name)
    {
        var bare = name.EndsWith("Stmt", StringComparison.Ordinal) ? name[..^"Stmt".Length] : name;
        var builder = new StringBuilder();
        for (var index = 0; index < bare.Length; index++)
        {
            if (index > 0 && char.IsUpper(bare[index]) && !char.IsUpper(bare[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(char.ToUpper(bare[index], CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
