using System.Text.Json;

namespace PgProj.Parser;

/// <summary>
/// The clause-coverage check (G-227). A statement pgproj models can carry a clause no reader asks for; the clause is then dropped from what is deployed, the database matches what was deployed,
/// and nothing shows the loss. For the node types of the statements pgproj models, this lists the fields pgproj reads or ignores on purpose, and reports any other field a parsed statement has
/// (pg_query leaves out a field that has its default value, so a field that is there was written): <c>build</c> warns that the clause is ignored.
/// </summary>
/// <remarks>
/// <para>
/// The lists are an allowlist, not an instrumentation of the readers: a recursive reader marks whole subtrees as read and would hide the gaps. A field is listed when some reader of this project
/// names it, or when <see cref="IgnoredFields"/> says why it is ignored. A node type that is not listed is not checked, so the check never warns about an expression's own nodes. A field a new
/// PostgreSQL version adds is reported until it is read or listed.
/// </para>
/// <para>
/// To add a clause: read it (and delete nothing here), or, when it carries nothing to model, add it to <see cref="IgnoredFields"/> with the reason. The reasons are documentation.
/// </para>
/// </remarks>
internal static class ClauseCoverage
{
    public const string DiagnosticSource = "ClauseCoverage";

    private static readonly Dictionary<string, HashSet<string>> Known = new(StringComparer.Ordinal)
    {
        ["CreateStmt"] = ["accessMethod", "constraints", "if_not_exists", "inhRelations", "ofTypename", "options", "partbound", "partspec", "relation", "tableElts", "tablespacename"],
        ["ColumnDef"] = ["collClause", "colname", "compression", "constraints", "generated", "is_not_null", "location", "storage", "storage_name", "typeName"],
        ["Constraint"] = ["access_method", "conname", "contype", "deferrable", "exclusions", "fk_attrs", "fk_del_action", "fk_del_set_cols", "fk_matchtype", "fk_upd_action", "fk_with_period", "generated_kind", "generated_when", "including", "initdeferred", "is_enforced", "is_no_inherit", "keys", "location", "nulls_not_distinct", "options", "pk_attrs", "pk_with_period", "pktable", "raw_expr", "skip_validation", "where_clause", "without_overlaps"],
        ["IndexStmt"] = ["accessMethod", "concurrent", "deferrable", "idxcomment", "idxname", "if_not_exists", "indexIncludingParams", "indexOid", "indexParams", "initdeferred", "isconstraint", "iswithoutoverlaps", "nulls_not_distinct", "oldNumber", "options", "primary", "relation", "reset_default_tblspc", "transformed", "unique", "whereClause"],
        ["IndexElem"] = ["collation", "expr", "name", "nulls_ordering", "opclass", "opclassopts", "ordering"],
        ["ViewStmt"] = ["aliases", "options", "query", "replace", "view", "withCheckOption"],
        ["CreateTrigStmt"] = ["args", "columns", "constrrel", "deferrable", "events", "funcname", "initdeferred", "isconstraint", "relation", "replace", "row", "timing", "transitionRels", "trigname", "whenClause"],
        ["CreateFunctionStmt"] = ["funcname", "is_procedure", "options", "parameters", "replace", "returnType", "sql_body"],
        ["FunctionParameter"] = ["argType", "defexpr", "location", "mode", "name"],
        ["CreateSeqStmt"] = ["for_identity", "if_not_exists", "options", "ownerId", "sequence"],
        ["CreateDomainStmt"] = ["collClause", "constraints", "domainname", "typeName"],
        ["CreateEnumStmt"] = ["typeName", "vals"],
        ["CompositeTypeStmt"] = ["coldeflist", "typevar"],
        ["CreatePolicyStmt"] = ["cmd_name", "permissive", "policy_name", "qual", "roles", "table", "with_check"],
        ["CreateStatsStmt"] = ["defnames", "exprs", "if_not_exists", "relations", "stat_types", "stxcomment", "transformed"],
        ["RuleStmt"] = ["actions", "event", "instead", "relation", "replace", "rulename", "whereClause"],
        ["CreateSchemaStmt"] = ["authrole", "if_not_exists", "schemaElts", "schemaname"],
        ["CreateTableAsStmt"] = ["if_not_exists", "into", "objtype", "query"],
        ["CreatePublicationStmt"] = ["for_all_tables", "options", "pubname", "pubobjects"],
        ["CreateEventTrigStmt"] = ["eventname", "funcname", "trigname", "whenclause"],
        ["CreateExtensionStmt"] = ["extname", "if_not_exists", "options"],
        ["CreateRangeStmt"] = ["params", "typeName"],
        ["CreateCastStmt"] = ["context", "func", "inout", "sourcetype", "targettype"],
        ["AlterSeqStmt"] = ["missing_ok", "options", "sequence"],
        ["PartitionElem"] = ["collation", "expr", "location", "name", "opclass"],
        ["PartitionSpec"] = ["location", "partParams", "strategy"],
    };

    /// <summary>Fields that are ignored only while they have the grammar's default value.</summary>
    private static readonly Dictionary<string, string[]> DefaultValues = new(StringComparer.Ordinal)
    {
        ["CreateStmt.oncommit"] = ["\"ONCOMMIT_NOOP\""],
        ["ColumnDef.is_local"] = ["true"],
        ["Constraint.initially_valid"] = ["true"],
    };

    /// <summary>Fields that are ignored on purpose, with the reason.</summary>
    internal static readonly IReadOnlyDictionary<string, string> IgnoredFields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Constraint.pk_with_period"] = "a temporal foreign key has PERIOD on both sides, and fk_with_period carries it (G-121)",
        ["CreateExtensionStmt.if_not_exists"] = "IF NOT EXISTS makes no difference to the extension a project declares",
        ["CreateFunctionStmt.replace"] = "OR REPLACE is how a routine is written, not a property of it",
        ["CreateSchemaStmt.if_not_exists"] = "IF NOT EXISTS makes no difference to the schema a project declares",
        ["CreateSchemaStmt.schemaElts"] = "the schema elements are read as statements of their own",
        ["CreateSeqStmt.for_identity"] = "set by PostgreSQL for an identity column",
        ["CreateSeqStmt.if_not_exists"] = "IF NOT EXISTS makes no difference to the sequence a project declares",
        ["CreateSeqStmt.ownerId"] = "set by PostgreSQL after parsing",
        ["CreateStatsStmt.if_not_exists"] = "IF NOT EXISTS makes no difference to the statistics object a project declares",
        ["CreateStatsStmt.stxcomment"] = "set by PostgreSQL after parsing",
        ["CreateStatsStmt.transformed"] = "set by PostgreSQL after parsing",
        ["CreateStmt.if_not_exists"] = "IF NOT EXISTS makes no difference to the table a project declares",
        ["CreateTableAsStmt.if_not_exists"] = "IF NOT EXISTS makes no difference to the object a project declares",
        ["CreateTrigStmt.replace"] = "OR REPLACE is how a trigger is written, not a property of it",
        ["IndexStmt.concurrent"] = "CONCURRENTLY is a way to build the index, not a property of it",
        ["IndexStmt.deferrable"] = "set by PostgreSQL when it builds a key constraint",
        ["IndexStmt.idxcomment"] = "set by PostgreSQL after parsing",
        ["IndexStmt.if_not_exists"] = "IF NOT EXISTS makes no difference to the index a project declares",
        ["IndexStmt.indexOid"] = "set by PostgreSQL after parsing",
        ["IndexStmt.initdeferred"] = "set by PostgreSQL when it builds a key constraint",
        ["IndexStmt.isconstraint"] = "set by PostgreSQL when it builds a key constraint",
        ["IndexStmt.iswithoutoverlaps"] = "set by PostgreSQL when it builds a key constraint",
        ["IndexStmt.oldNumber"] = "set by PostgreSQL after parsing",
        ["IndexStmt.primary"] = "set by PostgreSQL when it builds a key constraint",
        ["IndexStmt.reset_default_tblspc"] = "set by PostgreSQL after parsing",
        ["IndexStmt.transformed"] = "set by PostgreSQL after parsing",
        ["RuleStmt.actions"] = "the rule is kept as the statement text",
        ["ViewStmt.replace"] = "OR REPLACE is how a view is written, not a property of it",
    };

    /// <summary>The clause as a user writes it, for the fields whose JSON name says little.</summary>
    private static readonly Dictionary<string, string> ClauseNames = new(StringComparer.Ordinal)
    {
        ["CreateStmt.oncommit"] = "ON COMMIT",
        ["Constraint.without_overlaps"] = "WITHOUT OVERLAPS",
        ["CreateTableAsStmt.is_select_into"] = "SELECT ... INTO",
    };

    private static readonly Dictionary<string, string> StatementNames = new(StringComparer.Ordinal)
    {
        ["CreateStmt"] = "CREATE TABLE",
        ["IndexStmt"] = "CREATE INDEX",
        ["ViewStmt"] = "CREATE VIEW",
        ["CreateTrigStmt"] = "CREATE TRIGGER",
        ["CreateFunctionStmt"] = "CREATE FUNCTION",
        ["CreateSeqStmt"] = "CREATE SEQUENCE",
        ["AlterSeqStmt"] = "ALTER SEQUENCE",
        ["CreateDomainStmt"] = "CREATE DOMAIN",
        ["CreateEnumStmt"] = "CREATE TYPE",
        ["CompositeTypeStmt"] = "CREATE TYPE",
        ["CreateRangeStmt"] = "CREATE TYPE",
        ["CreatePolicyStmt"] = "CREATE POLICY",
        ["CreateStatsStmt"] = "CREATE STATISTICS",
        ["RuleStmt"] = "CREATE RULE",
        ["CreateSchemaStmt"] = "CREATE SCHEMA",
        ["CreateTableAsStmt"] = "CREATE TABLE AS / MATERIALIZED VIEW",
        ["CreatePublicationStmt"] = "CREATE PUBLICATION",
        ["CreateEventTrigStmt"] = "CREATE EVENT TRIGGER",
        ["CreateExtensionStmt"] = "CREATE EXTENSION",
        ["CreateCastStmt"] = "CREATE CAST",
        ["AlterTableStmt"] = "ALTER TABLE",
    };

    /// <summary>
    /// The warnings for the fields of <paramref name="statementBody"/> (the <c>stmt</c> of one raw statement) that are not read and not ignored on purpose, one for each statement and field.
    /// </summary>
    public static IEnumerable<string> Find(JsonElement statementBody)
    {
        string? statement = null;
        foreach (var property in statementBody.EnumerateObject())
        {
            statement = StatementNames.GetValueOrDefault(property.Name, property.Name);
            break;
        }

        if (statement is null)
        {
            yield break;
        }

        var reported = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<string>();
        Walk(statementBody, statement, reported, messages);
        foreach (var message in messages)
        {
            yield return message;
        }
    }

    private static void Walk(JsonElement element, string statement, HashSet<string> reported, List<string> messages)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object && Known.TryGetValue(property.Name, out var known))
                    {
                        foreach (var field in property.Value.EnumerateObject())
                        {
                            var key = property.Name + "." + field.Name;
                            if (known.Contains(field.Name) || IsDefault(key, field.Value) || !reported.Add(key))
                            {
                                continue;
                            }

                            var clause = ClauseNames.TryGetValue(key, out var name) ? name : $"'{field.Name}' ({property.Name})";
                            messages.Add($"{statement}: the {clause} clause is not modeled; it is ignored.");
                        }
                    }

                    Walk(property.Value, statement, reported, messages);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, statement, reported, messages);
                }

                break;
        }
    }

    private static bool IsDefault(string key, JsonElement value)
        => DefaultValues.TryGetValue(key, out var defaults) && defaults.Contains(value.GetRawText(), StringComparer.Ordinal);
}
