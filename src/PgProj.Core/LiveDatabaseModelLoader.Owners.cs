using Npgsql;

namespace PgProj.Core;

/// <summary>
/// The owner of an object the loader read (G-146), with the <c>ALTER ... OWNER TO</c> that says so. <paramref name="Kind"/> is the owner node's
/// object kind (<c>table</c>, <c>function</c>, ...); <paramref name="Arguments"/> is a routine's identity arguments, so the scaffold can put the
/// statement in the file of the overload it is about.
/// </summary>
internal sealed record LiveOwnerSnapshot(string Kind, string? SchemaName, string Name, string Arguments, string Owner, string Statement);

public sealed partial class LiveDatabaseModelLoader
{
    /// <summary>One row of the owner query, before it is matched to an object the loader read.</summary>
    internal sealed record OwnerRow(string Kind, string? SchemaName, string Name, string Arguments, string? LeftType, string? RightType, string Owner);

    /// <summary>
    /// The owner of every schema, relation, routine, aggregate, operator, type, event trigger, and publication in the database. Not scoped to the
    /// project's schemas and not limited to what the project could own: <see cref="BuildOwnerSnapshots"/> keeps the owners of the objects the other
    /// readers returned, so an owner never names an object the model does not have.
    /// </summary>
    private static async Task<IReadOnlyList<OwnerRow>> ReadOwnerRowsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string ownerSql =
            """
            select 'schema' as kind, n.nspname as schema_name, n.nspname as name, '' as arguments, null::text as left_type, null::text as right_type,
                   pg_catalog.pg_get_userbyid(n.nspowner) as owner
            from pg_catalog.pg_namespace n
            where n.nspname not like 'pg\_%' and n.nspname <> 'information_schema'
            union all
            select case c.relkind when 'v' then 'view' when 'm' then 'materialized view' when 'S' then 'sequence' else 'table' end,
                   n.nspname, c.relname, '', null, null, pg_catalog.pg_get_userbyid(c.relowner)
            from pg_catalog.pg_class c
            inner join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p', 'v', 'm', 'S')
              and n.nspname not like 'pg\_%' and n.nspname <> 'information_schema'
            union all
            select case p.prokind when 'p' then 'procedure' when 'a' then 'aggregate' else 'function' end,
                   n.nspname, p.proname, pg_catalog.pg_get_function_identity_arguments(p.oid), null, null, pg_catalog.pg_get_userbyid(p.proowner)
            from pg_catalog.pg_proc p
            inner join pg_catalog.pg_namespace n on n.oid = p.pronamespace
            where p.prokind in ('f', 'p', 'a')
              and n.nspname not like 'pg\_%' and n.nspname <> 'information_schema'
            union all
            select case t.typtype when 'd' then 'domain' else 'type' end,
                   n.nspname, t.typname, '', null, null, pg_catalog.pg_get_userbyid(t.typowner)
            from pg_catalog.pg_type t
            inner join pg_catalog.pg_namespace n on n.oid = t.typnamespace
            where t.typtype in ('e', 'r', 'd', 'c')
              and (t.typtype <> 'c' or exists (select 1 from pg_catalog.pg_class rc where rc.oid = t.typrelid and rc.relkind = 'c'))
              and n.nspname not like 'pg\_%' and n.nspname <> 'information_schema'
            union all
            select 'operator', n.nspname, o.oprname, '',
                   case when o.oprleft = 0 then null else pg_catalog.format_type(o.oprleft, null) end,
                   case when o.oprright = 0 then null else pg_catalog.format_type(o.oprright, null) end,
                   pg_catalog.pg_get_userbyid(o.oprowner)
            from pg_catalog.pg_operator o
            inner join pg_catalog.pg_namespace n on n.oid = o.oprnamespace
            where n.nspname not like 'pg\_%' and n.nspname <> 'information_schema'
            union all
            select 'event trigger', null, e.evtname, '', null, null, pg_catalog.pg_get_userbyid(e.evtowner)
            from pg_catalog.pg_event_trigger e
            union all
            select 'publication', null, p.pubname, '', null, null, pg_catalog.pg_get_userbyid(p.pubowner)
            from pg_catalog.pg_publication p
            union all
            select 'collation', n.nspname, c.collname, '', null, null, pg_catalog.pg_get_userbyid(c.collowner)
            from pg_catalog.pg_collation c
            inner join pg_catalog.pg_namespace n on n.oid = c.collnamespace
            where c.oid >= 16384
            union all
            select 'statistics', n.nspname, s.stxname, '', null, null, pg_catalog.pg_get_userbyid(s.stxowner)
            from pg_catalog.pg_statistic_ext s
            inner join pg_catalog.pg_namespace n on n.oid = s.stxnamespace
            union all
            select 'text search dictionary', n.nspname, d.dictname, '', null, null, pg_catalog.pg_get_userbyid(d.dictowner)
            from pg_catalog.pg_ts_dict d
            inner join pg_catalog.pg_namespace n on n.oid = d.dictnamespace
            where d.oid >= 16384
            union all
            select 'text search configuration', n.nspname, c.cfgname, '', null, null, pg_catalog.pg_get_userbyid(c.cfgowner)
            from pg_catalog.pg_ts_config c
            inner join pg_catalog.pg_namespace n on n.oid = c.cfgnamespace
            where c.oid >= 16384;
            """;

        var rows = new List<OwnerRow>();
        await using var command = new NpgsqlCommand(ownerSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OwnerRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6)));
        }

        return rows;
    }

    /// <summary>
    /// The owners of the objects the loader read, each with its <c>ALTER ... OWNER TO</c>. A row whose object is not among the snapshots (one in
    /// a schema outside the project's scope, an extension's, a system one) is left out, which is what keeps an owner from naming something the
    /// model lacks. In order, so the snapshot and the scaffold are the same on every run.
    /// </summary>
    internal static IReadOnlyList<LiveOwnerSnapshot> BuildOwnerSnapshots(
        IReadOnlyList<OwnerRow> rows,
        IReadOnlyList<LiveSchemaSnapshot> schemas,
        IReadOnlyList<LiveTableSnapshot> tables,
        IReadOnlyList<LiveViewSnapshot> views,
        IReadOnlyList<LiveSequenceSnapshot> sequences,
        IReadOnlyList<LiveFunctionSnapshot> functions,
        IReadOnlyList<LiveTypeSnapshot> types,
        IReadOnlyList<LiveOperatorSnapshot> operators,
        IReadOnlyList<LiveAggregateSnapshot> aggregates,
        IReadOnlyList<LiveEventTriggerSnapshot> eventTriggers,
        IReadOnlyList<LivePublicationSnapshot> publications,
        IReadOnlyList<LiveCollationSnapshot>? collations = null,
        IReadOnlyList<LiveStatisticsSnapshot>? statistics = null,
        LiveTextSearchSnapshot? textSearch = null)
    {
        textSearch ??= LiveTextSearchSnapshot.Empty;
        var schemaNames = schemas.Select(static schema => schema.SchemaName).ToHashSet(StringComparer.Ordinal);
        var tableNames = tables.Select(static table => (table.SchemaName, table.TableName)).ToHashSet();
        var viewNames = views.Select(static view => (view.SchemaName, view.ViewName)).ToHashSet();
        var sequenceNames = sequences.Select(static sequence => (sequence.SchemaName, sequence.SequenceName)).ToHashSet();
        var routineNames = functions.Select(static function => (function.SchemaName, function.FunctionName, function.IdentityArguments ?? string.Empty, function.IsProcedure)).ToHashSet();
        var typeNames = types
            .Where(static type => !string.Equals(type.TypeKind, "extension", StringComparison.OrdinalIgnoreCase))
            .Select(static type => (type.SchemaName, type.TypeName))
            .ToHashSet();
        var operatorNames = operators.Select(static op => (op.OperatorSchema, op.OperatorName, op.LeftType, op.RightType)).ToHashSet();
        var aggregateNames = aggregates.Select(static aggregate => (aggregate.SchemaName, aggregate.Name)).ToHashSet();
        var eventTriggerNames = eventTriggers.Select(static eventTrigger => eventTrigger.Name).ToHashSet(StringComparer.Ordinal);
        var publicationNames = publications.Select(static publication => publication.Name).ToHashSet(StringComparer.Ordinal);
        var collationNames = (collations ?? []).Select(static collation => (collation.SchemaName, collation.Name)).ToHashSet();
        var statisticsNames = (statistics ?? []).Select(static item => (item.SchemaName, item.Name)).ToHashSet();
        var dictionaryNames = textSearch.Dictionaries.Select(static dictionary => (dictionary.SchemaName, dictionary.Name)).ToHashSet();
        var configurationNames = textSearch.Configurations.Select(static configuration => (configuration.SchemaName, configuration.Name)).ToHashSet();

        var owners = new List<LiveOwnerSnapshot>();
        foreach (var row in rows)
        {
            var schema = row.SchemaName ?? string.Empty;
            string? statement = row.Kind switch
            {
                "schema" when schemaNames.Contains(row.Name) => $"ALTER SCHEMA {RenderIdentifier(row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                "table" when tableNames.Contains((schema, row.Name)) => $"ALTER TABLE {QualifyIdentifier(schema, row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                "view" when viewNames.Contains((schema, row.Name)) => $"ALTER VIEW {QualifyIdentifier(schema, row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                "materialized view" when viewNames.Contains((schema, row.Name)) => $"ALTER MATERIALIZED VIEW {QualifyIdentifier(schema, row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                "sequence" when sequenceNames.Contains((schema, row.Name)) => $"ALTER SEQUENCE {QualifyIdentifier(schema, row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                "function" when routineNames.Contains((schema, row.Name, row.Arguments, false)) => $"ALTER FUNCTION {QualifyIdentifier(schema, row.Name)}({row.Arguments}) OWNER TO {RenderIdentifier(row.Owner)};",
                "procedure" when routineNames.Contains((schema, row.Name, row.Arguments, true)) => $"ALTER PROCEDURE {QualifyIdentifier(schema, row.Name)}({row.Arguments}) OWNER TO {RenderIdentifier(row.Owner)};",
                "aggregate" when aggregateNames.Contains((schema, row.Name)) => $"ALTER AGGREGATE {QualifyIdentifier(schema, row.Name)} ({(row.Arguments.Length == 0 ? "*" : row.Arguments)}) OWNER TO {RenderIdentifier(row.Owner)};",
                "type" when typeNames.Contains((schema, row.Name)) => $"ALTER TYPE {QualifyIdentifier(schema, row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                "domain" when typeNames.Contains((schema, row.Name)) => $"ALTER DOMAIN {QualifyIdentifier(schema, row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                "operator" when operatorNames.Contains((schema, row.Name, row.LeftType, row.RightType))
                    => $"ALTER OPERATOR {QualifyOperator(schema, row.Name)} ({row.LeftType ?? "NONE"}, {row.RightType ?? "NONE"}) OWNER TO {RenderIdentifier(row.Owner)};",
                "event trigger" when eventTriggerNames.Contains(row.Name) => $"ALTER EVENT TRIGGER {RenderIdentifier(row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                "publication" when publicationNames.Contains(row.Name) => $"ALTER PUBLICATION {RenderIdentifier(row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                "collation" when collationNames.Contains((schema, row.Name)) => $"ALTER COLLATION {QualifyIdentifier(schema, row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                "statistics" when statisticsNames.Contains((schema, row.Name)) => $"ALTER STATISTICS {QualifyIdentifier(schema, row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                "text search dictionary" when dictionaryNames.Contains((schema, row.Name)) => $"ALTER TEXT SEARCH DICTIONARY {QualifyIdentifier(schema, row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                "text search configuration" when configurationNames.Contains((schema, row.Name)) => $"ALTER TEXT SEARCH CONFIGURATION {QualifyIdentifier(schema, row.Name)} OWNER TO {RenderIdentifier(row.Owner)};",
                _ => null,
            };

            if (statement is not null)
            {
                owners.Add(new LiveOwnerSnapshot(row.Kind, row.SchemaName, row.Name, row.Arguments, row.Owner, statement));
            }
        }

        return owners
            .OrderBy(static owner => owner.Kind, StringComparer.Ordinal)
            .ThenBy(static owner => owner.SchemaName, StringComparer.Ordinal)
            .ThenBy(static owner => owner.Name, StringComparer.Ordinal)
            .ThenBy(static owner => owner.Arguments, StringComparer.Ordinal)
            .ToArray();
    }
}
