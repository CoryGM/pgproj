using Npgsql;
using PgProj.Semantics;

namespace PgProj.Core;

public sealed partial class LiveDatabaseModelLoader
{
    /// <summary>
    /// The objects that depend on each relation or column (G-103), from <c>pg_depend</c>: a view's or materialized view's query (a rewrite
    /// rule), a policy, a trigger (its <c>WHEN</c> or <c>UPDATE OF</c>), a generated column's expression, and a <c>BEGIN ATOMIC</c> routine
    /// body. These are what PostgreSQL won't let a column be retyped or dropped under. Indexes and constraints are left out: PostgreSQL
    /// rebuilds or drops them itself. Not scoped by the project's <c>schemas</c>, so a dependent another project owns is seen, and a
    /// change it would block is refused rather than failed.
    /// </summary>
    private static async Task<IReadOnlyList<LiveDependency>> ReadDependenciesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql =
            """
            with referenced as (
                select dep.classid, dep.objid, dep.objsubid, dep.refobjid, dep.refobjsubid,
                       ref_ns.nspname as referenced_schema, ref.relname as referenced_name, att.attname as referenced_column
                from pg_catalog.pg_depend dep
                inner join pg_catalog.pg_class ref on dep.refclassid = 'pg_catalog.pg_class'::regclass and ref.oid = dep.refobjid
                inner join pg_catalog.pg_namespace ref_ns on ref_ns.oid = ref.relnamespace
                left join pg_catalog.pg_attribute att on att.attrelid = ref.oid and att.attnum = dep.refobjsubid and dep.refobjsubid > 0
                -- Before PostgreSQL 15 a generated column depends on the columns it uses directly, column to column (deptype a), not through its expression (G-269).
                where (dep.deptype = 'n' or (dep.deptype = 'a' and dep.classid = 'pg_catalog.pg_class'::regclass and dep.objsubid > 0 and dep.refobjsubid > 0))
                  and ref_ns.nspname not in ('pg_catalog', 'information_schema')
            )
            select distinct case cls.relkind when 'm' then 'materialized view' else 'view' end, ns.nspname, cls.relname, null::text,
                   r.referenced_schema, r.referenced_name, r.referenced_column
            from referenced r
            inner join pg_catalog.pg_rewrite rule on r.classid = 'pg_catalog.pg_rewrite'::regclass and rule.oid = r.objid
            inner join pg_catalog.pg_class cls on cls.oid = rule.ev_class
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where rule.ev_class <> r.refobjid and cls.relkind in ('v', 'm') and rule.rulename = '_RETURN'
            union
            select 'rule', ns.nspname, cls.relname, rule.rulename, r.referenced_schema, r.referenced_name, r.referenced_column
            from referenced r
            inner join pg_catalog.pg_rewrite rule on r.classid = 'pg_catalog.pg_rewrite'::regclass and rule.oid = r.objid
            inner join pg_catalog.pg_class cls on cls.oid = rule.ev_class
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where rule.rulename <> '_RETURN' and rule.oid >= 16384
            union
            select 'policy', ns.nspname, cls.relname, pol.polname, r.referenced_schema, r.referenced_name, r.referenced_column
            from referenced r
            inner join pg_catalog.pg_policy pol on r.classid = 'pg_catalog.pg_policy'::regclass and pol.oid = r.objid
            inner join pg_catalog.pg_class cls on cls.oid = pol.polrelid
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            union
            select 'trigger', ns.nspname, cls.relname, trg.tgname, r.referenced_schema, r.referenced_name, r.referenced_column
            from referenced r
            inner join pg_catalog.pg_trigger trg on r.classid = 'pg_catalog.pg_trigger'::regclass and trg.oid = r.objid
            inner join pg_catalog.pg_class cls on cls.oid = trg.tgrelid
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where not trg.tgisinternal
            union
            select 'generated column', ns.nspname, cls.relname, gen.attname, r.referenced_schema, r.referenced_name, r.referenced_column
            from referenced r
            inner join pg_catalog.pg_attrdef def on r.classid = 'pg_catalog.pg_attrdef'::regclass and def.oid = r.objid
            inner join pg_catalog.pg_attribute gen on gen.attrelid = def.adrelid and gen.attnum = def.adnum and gen.attgenerated <> ''
            inner join pg_catalog.pg_class cls on cls.oid = def.adrelid
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where not (r.refobjid = def.adrelid and r.refobjsubid = def.adnum)
            union
            select 'generated column', ns.nspname, cls.relname, gen.attname, r.referenced_schema, r.referenced_name, r.referenced_column
            from referenced r
            inner join pg_catalog.pg_attribute gen on r.classid = 'pg_catalog.pg_class'::regclass and gen.attrelid = r.objid and gen.attnum = r.objsubid and gen.attgenerated <> ''
            inner join pg_catalog.pg_class cls on cls.oid = gen.attrelid
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where not (r.refobjid = gen.attrelid and r.refobjsubid = gen.attnum)
            union
            select case proc.prokind when 'p' then 'procedure' else 'function' end, ns.nspname, proc.proname,
                   pg_catalog.oidvectortypes(proc.proargtypes), r.referenced_schema, r.referenced_name, r.referenced_column
            from referenced r
            inner join pg_catalog.pg_proc proc on r.classid = 'pg_catalog.pg_proc'::regclass and proc.oid = r.objid
            inner join pg_catalog.pg_namespace ns on ns.oid = proc.pronamespace;
            """;

        var dependencies = new List<LiveDependency>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = reader.GetString(0);
            var schema = reader.GetString(1);
            var name = reader.GetString(2);
            var detail = reader.IsDBNull(3) ? null : reader.GetString(3);
            var key = kind switch
            {
                "policy" => DatabaseModel.CreatePolicyKey(schema, name, detail!),
                "rule" => RuleKeys.Create(schema, name, detail!),
                "trigger" => DatabaseModel.CreateTriggerKey(schema, name, detail!),
                "generated column" => $"{DatabaseModel.CreateKey(schema, name)}.{detail}",
                "function" or "procedure" => DatabaseModel.CreateRoutineKey(schema, name, detail),
                _ => DatabaseModel.CreateKey(schema, name),
            };

            dependencies.Add(new LiveDependency(
                kind,
                key,
                DatabaseModel.CreateKey(reader.GetString(4), reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return dependencies;
    }

    /// <summary>
    /// The objects that use each function or procedure (G-105), from <c>pg_depend</c>: PostgreSQL won't drop a routine while any of them
    /// exists, so a routine that has to be dropped (its return type or arguments changed) first has them detached. A <c>LANGUAGE sql</c>
    /// string body is not tracked by PostgreSQL and never blocks a drop; a <c>BEGIN ATOMIC</c> one is. An operator that uses a routine is read
    /// as <c>operator</c> and is dropped and recreated around it; whatever else uses one (a cast, a type) is reported as <c>other</c>, which pgproj
    /// cannot recreate.
    /// </summary>
    private static async Task<IReadOnlyList<LiveDependency>> ReadRoutineDependenciesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql =
            """
            with referenced as (
                select dep.classid, dep.objid, dep.objsubid, rns.nspname as referenced_schema, ref.proname as referenced_name,
                       pg_catalog.oidvectortypes(ref.proargtypes) as referenced_arguments, 'routine' as referenced_kind, null::text as referenced_right
                from pg_catalog.pg_depend dep
                inner join pg_catalog.pg_proc ref on dep.refclassid = 'pg_catalog.pg_proc'::regclass and ref.oid = dep.refobjid
                inner join pg_catalog.pg_namespace rns on rns.oid = ref.pronamespace
                where dep.deptype = 'n'
                  and rns.nspname not in ('pg_catalog', 'information_schema')
                union all
                select dep.classid, dep.objid, dep.objsubid, ons.nspname, opr.oprname,
                       case when opr.oprleft = 0 then '' else pg_catalog.format_type(opr.oprleft, null) end, 'operator',
                       case when opr.oprright = 0 then '' else pg_catalog.format_type(opr.oprright, null) end
                from pg_catalog.pg_depend dep
                inner join pg_catalog.pg_operator opr on dep.refclassid = 'pg_catalog.pg_operator'::regclass and opr.oid = dep.refobjid
                inner join pg_catalog.pg_namespace ons on ons.oid = opr.oprnamespace
                where dep.deptype = 'n'
                  and ons.nspname not in ('pg_catalog', 'information_schema')
            )
            select case cls.relkind when 'm' then 'materialized view' else 'view' end, ns.nspname, cls.relname, null::text, r.referenced_schema, r.referenced_name, r.referenced_arguments, r.referenced_kind, r.referenced_right
            from referenced r
            inner join pg_catalog.pg_rewrite rule on r.classid = 'pg_catalog.pg_rewrite'::regclass and rule.oid = r.objid
            inner join pg_catalog.pg_class cls on cls.oid = rule.ev_class
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where cls.relkind in ('v', 'm') and rule.rulename = '_RETURN'
            union
            select 'rule', ns.nspname, cls.relname, rule.rulename, r.referenced_schema, r.referenced_name, r.referenced_arguments, r.referenced_kind, r.referenced_right
            from referenced r
            inner join pg_catalog.pg_rewrite rule on r.classid = 'pg_catalog.pg_rewrite'::regclass and rule.oid = r.objid
            inner join pg_catalog.pg_class cls on cls.oid = rule.ev_class
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where rule.rulename <> '_RETURN' and rule.oid >= 16384
            union
            select 'policy', ns.nspname, cls.relname, pol.polname, r.referenced_schema, r.referenced_name, r.referenced_arguments, r.referenced_kind, r.referenced_right
            from referenced r
            inner join pg_catalog.pg_policy pol on r.classid = 'pg_catalog.pg_policy'::regclass and pol.oid = r.objid
            inner join pg_catalog.pg_class cls on cls.oid = pol.polrelid
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            union
            select 'trigger', ns.nspname, cls.relname, trg.tgname, r.referenced_schema, r.referenced_name, r.referenced_arguments, r.referenced_kind, r.referenced_right
            from referenced r
            inner join pg_catalog.pg_trigger trg on r.classid = 'pg_catalog.pg_trigger'::regclass and trg.oid = r.objid
            inner join pg_catalog.pg_class cls on cls.oid = trg.tgrelid
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where not trg.tgisinternal
            union
            select case when col.attgenerated <> '' then 'generated column' else 'default' end, ns.nspname, cls.relname, col.attname,
                   r.referenced_schema, r.referenced_name, r.referenced_arguments, r.referenced_kind, r.referenced_right
            from referenced r
            inner join pg_catalog.pg_attrdef def on r.classid = 'pg_catalog.pg_attrdef'::regclass and def.oid = r.objid
            inner join pg_catalog.pg_attribute col on col.attrelid = def.adrelid and col.attnum = def.adnum
            inner join pg_catalog.pg_class cls on cls.oid = def.adrelid
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            union
            select case when con.contypid <> 0 then 'domain check' else 'check' end, ns.nspname, coalesce(cls.relname, typ.typname), con.conname,
                   r.referenced_schema, r.referenced_name, r.referenced_arguments, r.referenced_kind, r.referenced_right
            from referenced r
            inner join pg_catalog.pg_constraint con on r.classid = 'pg_catalog.pg_constraint'::regclass and con.oid = r.objid
            left join pg_catalog.pg_class cls on cls.oid = con.conrelid
            left join pg_catalog.pg_type typ on typ.oid = con.contypid
            inner join pg_catalog.pg_namespace ns on ns.oid = coalesce(cls.relnamespace, typ.typnamespace)
            union
            select 'index', ns.nspname, cls.relname, null::text, r.referenced_schema, r.referenced_name, r.referenced_arguments, r.referenced_kind, r.referenced_right
            from referenced r
            inner join pg_catalog.pg_class cls on r.classid = 'pg_catalog.pg_class'::regclass and cls.oid = r.objid and cls.relkind in ('i', 'I')
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            union
            select case proc.prokind when 'p' then 'procedure' else 'function' end, ns.nspname, proc.proname, pg_catalog.oidvectortypes(proc.proargtypes),
                   r.referenced_schema, r.referenced_name, r.referenced_arguments, r.referenced_kind, r.referenced_right
            from referenced r
            inner join pg_catalog.pg_proc proc on r.classid = 'pg_catalog.pg_proc'::regclass and proc.oid = r.objid
            inner join pg_catalog.pg_namespace ns on ns.oid = proc.pronamespace
            union
            select 'operator', ns.nspname, opr.oprname,
                   case when opr.oprleft = 0 then '' else pg_catalog.format_type(opr.oprleft, null) end || '|' || case when opr.oprright = 0 then '' else pg_catalog.format_type(opr.oprright, null) end,
                   r.referenced_schema, r.referenced_name, r.referenced_arguments, r.referenced_kind, r.referenced_right
            from referenced r
            inner join pg_catalog.pg_operator opr on r.classid = 'pg_catalog.pg_operator'::regclass and opr.oid = r.objid
            inner join pg_catalog.pg_namespace ns on ns.oid = opr.oprnamespace
            union
            select 'other', '', pg_catalog.pg_describe_object(r.classid, r.objid, r.objsubid), null::text, r.referenced_schema, r.referenced_name, r.referenced_arguments, r.referenced_kind, r.referenced_right
            from referenced r
            where r.classid not in ('pg_catalog.pg_rewrite'::regclass, 'pg_catalog.pg_policy'::regclass, 'pg_catalog.pg_trigger'::regclass,
                                    'pg_catalog.pg_attrdef'::regclass, 'pg_catalog.pg_constraint'::regclass, 'pg_catalog.pg_class'::regclass,
                                    'pg_catalog.pg_proc'::regclass, 'pg_catalog.pg_operator'::regclass);
            """;

        var dependencies = new List<LiveDependency>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = reader.GetString(0);
            var schema = reader.GetString(1);
            var name = reader.GetString(2);
            var detail = reader.IsDBNull(3) ? null : reader.GetString(3);
            var key = kind switch
            {
                "policy" => DatabaseModel.CreatePolicyKey(schema, name, detail!),
                "rule" => RuleKeys.Create(schema, name, detail!),
                "trigger" => DatabaseModel.CreateTriggerKey(schema, name, detail!),
                "check" => DatabaseModel.CreateConstraintKey(schema, name, detail),
                "generated column" or "default" or "domain check" => $"{DatabaseModel.CreateKey(schema, name)}.{detail}",
                "function" or "procedure" => DatabaseModel.CreateRoutineKey(schema, name, detail),
                "operator" => OperatorKey(schema, name, detail),
                "other" => name,
                _ => DatabaseModel.CreateKey(schema, name),
            };

            var referencedIsOperator = reader.GetString(7) == "operator";
            dependencies.Add(new LiveDependency(
                kind,
                key,
                referencedIsOperator
                    ? OperatorNames.CreateKey(reader.GetString(4), reader.GetString(5), NullIfEmpty(reader.GetString(6)), NullIfEmpty(reader.IsDBNull(8) ? null : reader.GetString(8)))
                    : DatabaseModel.CreateRoutineKey(reader.GetString(4), reader.GetString(5), reader.GetString(6)),
                ReferencedColumn: null,
                ReferencedKind: referencedIsOperator ? "operator" : "routine"));
        }

        return dependencies;
    }

    /// <summary>An operator's model key from its schema, name, and the <c>left|right</c> operand types a dependency query gives (empty for a missing side) (G-138).</summary>
    private static string OperatorKey(string schema, string name, string? operands)
    {
        var separator = operands?.IndexOf('|', StringComparison.Ordinal) ?? -1;
        return separator < 0
            ? OperatorNames.CreateKey(schema, name, null, null)
            : OperatorNames.CreateKey(schema, name, NullIfEmpty(operands![..separator]), NullIfEmpty(operands[(separator + 1)..]));
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>
    /// Each plain view's output columns, <c>name type</c> in order (G-104), read under the pinned path as a project view's are rendered, so
    /// the two lists compare. <c>CREATE OR REPLACE VIEW</c> can only append to this list.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadViewOutputColumnsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql =
            """
            select ns.nspname, cls.relname,
                   string_agg(pg_catalog.quote_ident(att.attname) || ' ' || pg_catalog.format_type(att.atttypid, att.atttypmod), chr(31) order by att.attnum)
            from pg_catalog.pg_class cls
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            inner join pg_catalog.pg_attribute att on att.attrelid = cls.oid and att.attnum > 0 and not att.attisdropped
            where cls.relkind = 'v'
              and ns.nspname not in ('pg_catalog', 'information_schema')
            group by ns.nspname, cls.relname;
            """;

        var columns = new Dictionary<string, IReadOnlyList<string>>(PgProj.Parser.SqlIdentifiers.NameComparer);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns[DatabaseModel.CreateKey(reader.GetString(0), reader.GetString(1))] = ServerViewRenderer.SplitColumns(reader.GetString(2));
        }

        return columns;
    }

    /// <summary>The owner of every view, materialized view, function, and procedure, by model key (G-103).</summary>
    private static async Task<IReadOnlyDictionary<string, string>> ReadViewOwnersAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql =
            """
            select ns.nspname, cls.relname, null::text, pg_catalog.pg_get_userbyid(cls.relowner)
            from pg_catalog.pg_class cls
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where cls.relkind in ('v', 'm')
              and ns.nspname not in ('pg_catalog', 'information_schema')
            union all
            select ns.nspname, proc.proname, pg_catalog.oidvectortypes(proc.proargtypes), pg_catalog.pg_get_userbyid(proc.proowner)
            from pg_catalog.pg_proc proc
            inner join pg_catalog.pg_namespace ns on ns.oid = proc.pronamespace
            where proc.prokind in ('f', 'p')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and not exists (select 1 from pg_catalog.pg_depend ext where ext.classid = 'pg_catalog.pg_proc'::regclass and ext.objid = proc.oid and ext.deptype = 'e');
            """;

        var owners = new Dictionary<string, string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.IsDBNull(2)
                ? DatabaseModel.CreateKey(reader.GetString(0), reader.GetString(1))
                : DatabaseModel.CreateRoutineKey(reader.GetString(0), reader.GetString(1), reader.GetString(2));
            owners[key] = reader.GetString(3);
        }

        return owners;
    }
}
