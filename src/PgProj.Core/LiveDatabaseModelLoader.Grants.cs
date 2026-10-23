using Npgsql;

namespace PgProj.Core;

/// <summary>
/// Reads the privileges PostgreSQL recorded and renders them as the <c>GRANT</c> statements that would recreate them, which the
/// snapshot script then carries through the same parser as a project's own SQL. PostgreSQL records privileges per object, so a
/// project that grants over <c>ALL TABLES IN SCHEMA</c>, on one table, or on a list of tables has to read back as the same thing
/// (G-61, G-65): the comparison expands both sides per object, and this reader reports each privilege at whichever granularity is
/// shortest, <c>ALL ... IN SCHEMA</c> where a grantee holds the same privilege on every object of a kind and one statement per
/// object for the rest.
/// </summary>
public sealed partial class LiveDatabaseModelLoader
{
    private const string WithGrantOption = " WITH GRANT OPTION";

    private static async Task<IReadOnlyList<LiveGrantSnapshot>> ReadGrantsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        var grants = new List<LiveGrantSnapshot>();
        grants.AddRange(await ReadSchemaGrantsAsync(connection, scopeSchemas, cancellationToken));
        grants.AddRange(await ReadObjectGrantsAsync(connection, scopeSchemas, cancellationToken));
        grants.AddRange(await ReadColumnGrantsAsync(connection, scopeSchemas, cancellationToken));
        grants.AddRange(await ReadDefaultPrivilegesGrantsAsync(connection, scopeSchemas, cancellationToken));
        grants.AddRange(await ReadRoleMembershipsAsync(connection, cancellationToken));
        return grants;
    }

    private static async Task<IReadOnlyList<LiveGrantSnapshot>> ReadSchemaGrantsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string schemaGrantSql =
            """
            select ns.nspname as schema_name,
                   case when acl.grantee = 0 then 'PUBLIC' else pg_catalog.pg_get_userbyid(acl.grantee) end as grantee,
                   acl.is_grantable,
                   array_agg(distinct upper(acl.privilege_type) order by upper(acl.privilege_type)) as privileges
            from pg_catalog.pg_namespace ns
            cross join lateral pg_catalog.aclexplode(ns.nspacl) acl
            where ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and acl.grantee <> ns.nspowner
              -- Before PostgreSQL 15 the public schema grants CREATE to PUBLIC by default, as well as USAGE (G-269); like USAGE (G-86), it is the
              -- database's own default, not a grant a project states or removes.
              and not (pg_catalog.current_setting('server_version_num')::int < 150000
                       and ns.nspname = 'public' and acl.grantee = 0 and acl.privilege_type = 'CREATE' and not acl.is_grantable)
              and (@schemas is null or ns.nspname = any(@schemas))
            group by ns.nspname, grantee, acl.is_grantable
            order by ns.nspname, grantee, acl.is_grantable;
            """;

        var grants = new List<LiveGrantSnapshot>();

        await using var command = CreateCommand(schemaGrantSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaName = reader.GetString(0);
            var grantee = reader.GetString(1);
            var grantOption = reader.GetBoolean(2) ? WithGrantOption : string.Empty;
            var privileges = NormalizePrivilegeArray((string[])reader.GetValue(3), fullPrivilegeSet: ["USAGE", "CREATE"]);
            grants.Add(new LiveGrantSnapshot(
                schemaName,
                $"schema:{schemaName}",
                grantee,
                $"GRANT {privileges} ON SCHEMA {RenderIdentifier(schemaName)} TO {RenderGrantee(grantee)}{grantOption}"));
        }

        return grants;
    }

    /// <summary>
    /// Privileges on tables (views, materialized views, and foreign tables included), sequences, functions, and procedures. Where
    /// a grantee holds a privilege on every object of a kind in a schema it is reported once as <c>ALL ... IN SCHEMA</c>; whatever
    /// is left, and every privilege on a schema that is not covered throughout, is reported per object. (A schema where the
    /// grantee holds one privilege on some objects and another on the rest used to lose the grant altogether.)
    /// </summary>
    private static async Task<IReadOnlyList<LiveGrantSnapshot>> ReadObjectGrantsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string objectCountSql =
            """
            select ns.nspname as schema_name,
                   case cls.relkind when 'S' then 'sequences' else 'tables' end as object_kind,
                   count(*) as object_count
            from pg_catalog.pg_class cls
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where cls.relkind in ('r', 'p', 'v', 'm', 'f', 'S')
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = cls.oid and ext_dep.deptype = 'e')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            group by ns.nspname, object_kind
            union all
            select ns.nspname as schema_name,
                   'functions' as object_kind,
                   count(*) as object_count
            from pg_catalog.pg_proc proc
            inner join pg_catalog.pg_namespace ns on ns.oid = proc.pronamespace
            where proc.prokind in ('f', 'w')
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_proc'::regclass and ext_dep.objid = proc.oid and ext_dep.deptype = 'e')
              -- A range type's constructor functions and its multirange's are created with the type (G-69).
              and not exists (select 1 from pg_catalog.pg_depend int_dep where int_dep.classid = 'pg_catalog.pg_proc'::regclass and int_dep.objid = proc.oid and int_dep.deptype = 'i' and int_dep.refclassid = 'pg_catalog.pg_type'::regclass)
              -- A base type's support functions belong to the type, which is not modeled (G-241).
              and not exists (select 1 from pg_catalog.pg_type base_type where base_type.typtype = 'b' and proc.oid in (base_type.typinput::oid, base_type.typoutput::oid, base_type.typreceive::oid, base_type.typsend::oid, base_type.typmodin::oid, base_type.typmodout::oid, base_type.typanalyze::oid, base_type.typsubscript::oid))
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            group by ns.nspname;
            """;

        // object_arguments is the argument types of a routine as PostgreSQL writes them ("integer, text[]"), null for the rest.
        const string objectGrantSql =
            """
            select ns.nspname as schema_name,
                   case when cls.relkind = 'S' then 'sequences' else 'tables' end as object_kind,
                   cls.relname as object_name,
                   null::text as object_arguments,
                   cls.oid::text as object_id,
                   case when acl.grantee = 0 then 'PUBLIC' else pg_catalog.pg_get_userbyid(acl.grantee) end as grantee,
                   upper(acl.privilege_type) as privilege_type,
                   acl.is_grantable
            from pg_catalog.pg_class cls
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            cross join lateral pg_catalog.aclexplode(cls.relacl) acl
            where cls.relkind in ('r', 'p', 'v', 'm', 'f', 'S')
              and acl.grantee <> cls.relowner
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = cls.oid and ext_dep.deptype = 'e')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            union all
            select ns.nspname as schema_name,
                   case when proc.prokind = 'p' then 'procedures' else 'functions' end as object_kind,
                   proc.proname as object_name,
                   pg_catalog.oidvectortypes(proc.proargtypes) as object_arguments,
                   proc.oid::text as object_id,
                   case when acl.grantee = 0 then 'PUBLIC' else pg_catalog.pg_get_userbyid(acl.grantee) end as grantee,
                   upper(acl.privilege_type) as privilege_type,
                   acl.is_grantable
            from pg_catalog.pg_proc proc
            inner join pg_catalog.pg_namespace ns on ns.oid = proc.pronamespace
            cross join lateral pg_catalog.aclexplode(proc.proacl) acl
            where proc.prokind in ('f', 'w', 'p')
              and acl.grantee <> proc.proowner
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_proc'::regclass and ext_dep.objid = proc.oid and ext_dep.deptype = 'e')
              -- A range type's constructor functions and its multirange's are created with the type (G-69).
              and not exists (select 1 from pg_catalog.pg_depend int_dep where int_dep.classid = 'pg_catalog.pg_proc'::regclass and int_dep.objid = proc.oid and int_dep.deptype = 'i' and int_dep.refclassid = 'pg_catalog.pg_type'::regclass)
              -- A base type's support functions belong to the type, which is not modeled (G-241).
              and not exists (select 1 from pg_catalog.pg_type base_type where base_type.typtype = 'b' and proc.oid in (base_type.typinput::oid, base_type.typoutput::oid, base_type.typreceive::oid, base_type.typsend::oid, base_type.typmodin::oid, base_type.typmodout::oid, base_type.typanalyze::oid, base_type.typsubscript::oid))
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas));
            """;

        var objectCounts = new Dictionary<(string SchemaName, string ObjectKind), int>();
        await using (var countCommand = CreateCommand(objectCountSql, connection, scopeSchemas))
        await using (var reader = await countCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                objectCounts[(reader.GetString(0), reader.GetString(1))] = reader.GetInt32(2);
            }
        }

        // One entry per grantee, kind, and grant option in a schema, holding the privileges it has on each object.
        var privilegesByObject = new Dictionary<(string SchemaName, string ObjectKind, string Grantee, bool GrantOption), Dictionary<string, (string Name, string? Arguments, HashSet<string> Privileges)>>();
        await using (var grantCommand = CreateCommand(objectGrantSql, connection, scopeSchemas))
        await using (var reader = await grantCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetString(0), reader.GetString(1), reader.GetString(5), reader.GetBoolean(7));
                if (!privilegesByObject.TryGetValue(key, out var objects))
                {
                    objects = new Dictionary<string, (string, string?, HashSet<string>)>(StringComparer.Ordinal);
                    privilegesByObject[key] = objects;
                }

                var objectId = reader.GetString(4);
                if (!objects.TryGetValue(objectId, out var entry))
                {
                    entry = (reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    objects[objectId] = entry;
                }

                entry.Privileges.Add(reader.GetString(6));
            }
        }

        var grants = new List<LiveGrantSnapshot>();
        foreach (var ((schemaName, objectKind, grantee, isGrantable), objects) in privilegesByObject
            .OrderBy(static item => item.Key.SchemaName, StringComparer.Ordinal)
            .ThenBy(static item => item.Key.ObjectKind, StringComparer.Ordinal)
            .ThenBy(static item => item.Key.Grantee, StringComparer.Ordinal)
            .ThenBy(static item => item.Key.GrantOption))
        {
            var grantOption = isGrantable ? WithGrantOption : string.Empty;
            var fullSet = GetFullPrivilegeSet(objectKind);

            // Procedures have no ALL ... IN SCHEMA form here, so they are always reported one by one.
            var covered = objectCounts.TryGetValue((schemaName, objectKind), out var totalObjects)
                && totalObjects > 0
                && objects.Count == totalObjects;

            var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (covered)
            {
                common.UnionWith(objects.Values.First().Privileges);
                foreach (var entry in objects.Values)
                {
                    common.IntersectWith(entry.Privileges);
                }
            }

            if (common.Count > 0)
            {
                grants.Add(new LiveGrantSnapshot(
                    schemaName,
                    $"all {objectKind} in schema:{schemaName}",
                    grantee,
                    $"GRANT {NormalizePrivilegeArray(common, fullSet)} ON ALL {objectKind.ToUpperInvariant()} IN SCHEMA {RenderIdentifier(schemaName)} TO {RenderGrantee(grantee)}{grantOption}"));
            }

            foreach (var entry in objects.Values.OrderBy(static entry => entry.Name, StringComparer.Ordinal).ThenBy(static entry => entry.Arguments, StringComparer.Ordinal))
            {
                var remaining = entry.Privileges.Where(privilege => !common.Contains(privilege)).ToArray();
                if (remaining.Length == 0)
                {
                    continue;
                }

                var (keyKind, target) = objectKind switch
                {
                    "sequences" => ("sequence", $"SEQUENCE {RenderIdentifier(schemaName)}.{RenderIdentifier(entry.Name)}"),
                    "functions" => ("function", $"FUNCTION {RenderIdentifier(schemaName)}.{RenderIdentifier(entry.Name)}({entry.Arguments})"),
                    "procedures" => ("procedure", $"PROCEDURE {RenderIdentifier(schemaName)}.{RenderIdentifier(entry.Name)}({entry.Arguments})"),
                    _ => ("table", $"TABLE {RenderIdentifier(schemaName)}.{RenderIdentifier(entry.Name)}"),
                };

                grants.Add(new LiveGrantSnapshot(
                    schemaName,
                    $"{keyKind}:{schemaName}.{entry.Name}({entry.Arguments})",
                    grantee,
                    $"GRANT {NormalizePrivilegeArray(remaining, fullSet)} ON {target} TO {RenderGrantee(grantee)}{grantOption}"));
            }
        }

        return grants;
    }

    /// <summary>Privileges limited to columns, one statement per table, grantee, privilege, and grant option, listing the columns.</summary>
    private static async Task<IReadOnlyList<LiveGrantSnapshot>> ReadColumnGrantsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string columnGrantSql =
            """
            select ns.nspname as schema_name,
                   cls.relname as table_name,
                   att.attname as column_name,
                   case when acl.grantee = 0 then 'PUBLIC' else pg_catalog.pg_get_userbyid(acl.grantee) end as grantee,
                   upper(acl.privilege_type) as privilege_type,
                   acl.is_grantable
            from pg_catalog.pg_attribute att
            inner join pg_catalog.pg_class cls on cls.oid = att.attrelid
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            cross join lateral pg_catalog.aclexplode(att.attacl) acl
            where att.attacl is not null
              and att.attnum > 0
              and not att.attisdropped
              and cls.relkind in ('r', 'p', 'v', 'm', 'f')
              and acl.grantee <> cls.relowner
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = cls.oid and ext_dep.deptype = 'e')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas));
            """;

        var columnsByGrant = new SortedDictionary<(string SchemaName, string TableName, string Grantee, string Privilege, bool GrantOption), SortedSet<string>>(
            Comparer<(string SchemaName, string TableName, string Grantee, string Privilege, bool GrantOption)>.Create(static (left, right) =>
            {
                var compared = string.CompareOrdinal(left.SchemaName, right.SchemaName);
                compared = compared != 0 ? compared : string.CompareOrdinal(left.TableName, right.TableName);
                compared = compared != 0 ? compared : string.CompareOrdinal(left.Grantee, right.Grantee);
                compared = compared != 0 ? compared : string.CompareOrdinal(left.Privilege, right.Privilege);
                return compared != 0 ? compared : left.GrantOption.CompareTo(right.GrantOption);
            }));

        await using var command = CreateCommand(columnGrantSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetString(0), reader.GetString(1), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5));
            if (!columnsByGrant.TryGetValue(key, out var columns))
            {
                columns = new SortedSet<string>(StringComparer.Ordinal);
                columnsByGrant[key] = columns;
            }

            columns.Add(reader.GetString(2));
        }

        return columnsByGrant
            .Select(static item => new LiveGrantSnapshot(
                item.Key.SchemaName,
                $"table:{item.Key.SchemaName}.{item.Key.TableName}",
                item.Key.Grantee,
                $"GRANT {item.Key.Privilege} ({string.Join(", ", item.Value.Select(RenderIdentifier))}) ON TABLE {RenderIdentifier(item.Key.SchemaName)}.{RenderIdentifier(item.Key.TableName)} TO {RenderGrantee(item.Key.Grantee)}{(item.Key.GrantOption ? WithGrantOption : string.Empty)}"))
            .ToArray();
    }

    /// <summary>
    /// Default privileges. Those of the role that is connected are reported as they are written (no <c>FOR ROLE</c>); those of any
    /// other role say <c>FOR ROLE</c>, which is how a project states them. The role's own entry in its list is left out, as for
    /// every other kind of privilege: it is implicit, and reporting it would have the project revoke the role's own defaults.
    /// </summary>
    private static async Task<IReadOnlyList<LiveGrantSnapshot>> ReadDefaultPrivilegesGrantsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string defaultPrivilegesSql =
            """
            select coalesce(ns.nspname, '') as schema_name,
                   case defacl.defaclobjtype
                       when 'r' then 'tables'
                       when 'S' then 'sequences'
                       when 'f' then 'functions'
                       when 'T' then 'types'
                       when 'n' then 'schemas'
                       else null
                   end as object_kind,
                   case when pg_catalog.pg_get_userbyid(defacl.defaclrole) = current_user then null else pg_catalog.pg_get_userbyid(defacl.defaclrole) end as for_role,
                   case when acl.grantee = 0 then 'PUBLIC' else pg_catalog.pg_get_userbyid(acl.grantee) end as grantee,
                   acl.is_grantable,
                   array_agg(distinct upper(acl.privilege_type) order by upper(acl.privilege_type)) as privileges
            from pg_catalog.pg_default_acl defacl
            left join pg_catalog.pg_namespace ns on ns.oid = defacl.defaclnamespace
            cross join lateral pg_catalog.aclexplode(defacl.defaclacl) acl
            where defacl.defaclobjtype in ('r', 'S', 'f', 'T', 'n')
              and acl.grantee <> defacl.defaclrole
              -- A default privilege with no IN SCHEMA (ns.nspname is null) applies database-wide, not to one
              -- schema, so schema-scoping never excludes it (G-85).
              and (@schemas is null or ns.nspname is null or ns.nspname = any(@schemas))
            group by schema_name, object_kind, for_role, grantee, acl.is_grantable
            order by schema_name, object_kind, for_role nulls first, grantee, acl.is_grantable;
            """;

        var grants = new List<LiveGrantSnapshot>();
        await using var command = CreateCommand(defaultPrivilegesSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaName = reader.GetString(0);
            var objectKind = reader.GetString(1);
            var forRole = reader.IsDBNull(2) ? null : reader.GetString(2);
            var grantee = reader.GetString(3);
            var grantOption = reader.GetBoolean(4) ? WithGrantOption : string.Empty;
            var privileges = NormalizePrivilegeArray((string[])reader.GetValue(5), GetFullPrivilegeSet(objectKind));
            var roleClause = forRole is null ? string.Empty : $" FOR ROLE {RenderIdentifier(forRole)}";
            var schemaClause = string.IsNullOrWhiteSpace(schemaName)
                ? string.Empty
                : $" IN SCHEMA {RenderIdentifier(schemaName)}";

            grants.Add(new LiveGrantSnapshot(
                schemaName,
                string.IsNullOrWhiteSpace(schemaName) ? $"default {objectKind}" : $"default {objectKind} in schema:{schemaName}",
                grantee,
                $"ALTER DEFAULT PRIVILEGES{roleClause}{schemaClause} GRANT {privileges} ON {objectKind.ToUpperInvariant()} TO {RenderGrantee(grantee)}{grantOption}"));
        }

        return grants;
    }

    /// <summary>
    /// Role membership (<c>GRANT role TO member</c>), so that memberships a project declares read back. Those of the predefined
    /// <c>pg_*</c> roles are left out. A membership the project does not declare is never removed by a publish, whatever is read here.
    /// </summary>
    private static async Task<IReadOnlyList<LiveGrantSnapshot>> ReadRoleMembershipsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string membershipSql =
            """
            select pg_catalog.pg_get_userbyid(membership.roleid) as role_name,
                   pg_catalog.pg_get_userbyid(membership.member) as member_name,
                   bool_or(membership.admin_option) as admin_option
            from pg_catalog.pg_auth_members membership
            where pg_catalog.pg_get_userbyid(membership.roleid) not like 'pg\_%'
            group by membership.roleid, membership.member
            order by role_name, member_name;
            """;

        var grants = new List<LiveGrantSnapshot>();
        await using var command = new NpgsqlCommand(membershipSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var role = reader.GetString(0);
            var member = reader.GetString(1);
            var adminOption = reader.GetBoolean(2) ? " WITH ADMIN OPTION" : string.Empty;
            grants.Add(new LiveGrantSnapshot(
                string.Empty,
                $"role:{role}",
                member,
                $"GRANT {RenderIdentifier(role)} TO {RenderGrantee(member)}{adminOption}"));
        }

        return grants;
    }

    private static string NormalizePrivilegeArray(IEnumerable<string> privileges, IReadOnlyCollection<string> fullPrivilegeSet)
    {
        var privilegeList = privileges
            .Where(static privilege => !string.IsNullOrWhiteSpace(privilege))
            .Select(static privilege => privilege.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static privilege => privilege, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return privilegeList.Length > 0 && fullPrivilegeSet.Count > 0 && fullPrivilegeSet.All(privilege => privilegeList.Contains(privilege, StringComparer.OrdinalIgnoreCase))
            ? "ALL PRIVILEGES"
            : string.Join(", ", privilegeList);
    }

    private static IReadOnlyCollection<string> GetFullPrivilegeSet(string objectKind)
        => objectKind switch
        {
            "tables" => ["DELETE", "INSERT", "REFERENCES", "SELECT", "TRIGGER", "TRUNCATE", "UPDATE", "MAINTAIN"],
            "sequences" => ["SELECT", "UPDATE", "USAGE"],
            "functions" or "procedures" => ["EXECUTE"],
            "types" => ["USAGE"],
            "schemas" => ["CREATE", "USAGE"],
            _ => []
        };
}
