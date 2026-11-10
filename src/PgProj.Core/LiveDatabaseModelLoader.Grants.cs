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
        grants.AddRange(await ReadPublicSchemaRevokedDefaultsAsync(connection, scopeSchemas, cancellationToken));
        grants.AddRange(await ReadObjectGrantsAsync(connection, scopeSchemas, cancellationToken));
        grants.AddRange(await ReadColumnGrantsAsync(connection, scopeSchemas, cancellationToken));
        grants.AddRange(await ReadDefaultPrivilegesGrantsAsync(connection, scopeSchemas, cancellationToken));
        grants.AddRange(await ReadTypeGrantsAsync(connection, scopeSchemas, cancellationToken));
        grants.AddRange(await ReadRoutineRevokedDefaultsAsync(connection, scopeSchemas, cancellationToken));
        grants.AddRange(await ReadDefaultPrivilegeRevokedDefaultsAsync(connection, cancellationToken));
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
    /// The <c>public</c> schema without a privilege PostgreSQL gives <c>PUBLIC</c> on it (G-341), as <c>REVOKE USAGE|CREATE ON SCHEMA public FROM PUBLIC</c>, the form
    /// <c>pg_dump</c> writes: <c>USAGE</c> on every version, <c>CREATE</c> by default only before PostgreSQL 15 (on 15 and later every database reads as without it,
    /// which a project that does not state it does not compare). A null list is the owner's alone (<c>acldefault</c>): a <c>public</c> schema created again by hand
    /// has neither.
    /// </summary>
    private static async Task<IReadOnlyList<LiveGrantSnapshot>> ReadPublicSchemaRevokedDefaultsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string publicSchemaSql =
            """
            select exists (select 1 from pg_catalog.aclexplode(coalesce(ns.nspacl, pg_catalog.acldefault('n', ns.nspowner))) acl where acl.grantee = 0 and acl.privilege_type = 'USAGE') as has_usage,
                   exists (select 1 from pg_catalog.aclexplode(coalesce(ns.nspacl, pg_catalog.acldefault('n', ns.nspowner))) acl where acl.grantee = 0 and acl.privilege_type = 'CREATE') as has_create
            from pg_catalog.pg_namespace ns
            where ns.nspname = 'public'
              and (@schemas is null or ns.nspname = any(@schemas));
            """;

        var grants = new List<LiveGrantSnapshot>();
        await using var command = CreateCommand(publicSchemaSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            foreach (var (privilege, has) in new[] { ("USAGE", reader.GetBoolean(0)), ("CREATE", reader.GetBoolean(1)) })
            {
                if (!has)
                {
                    grants.Add(new LiveGrantSnapshot("public", "schema:public", "PUBLIC", $"REVOKE {privilege} ON SCHEMA {RenderIdentifier("public")} FROM PUBLIC"));
                }
            }
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

    /// <summary>
    /// Privileges on enum, composite, range, and base types (G-267) and on domains (G-337), read as a project states them. A type whose list is null has PostgreSQL's default
    /// (the owner and <c>PUBLIC</c> have <c>USAGE</c>) and is not reported. Once a grant or revoke has written the list out, each role but the owner is reported with
    /// a <c>GRANT</c>, and a list without <c>PUBLIC</c> as <c>REVOKE USAGE ... FROM PUBLIC</c>, the one revoke pgproj reads (option (B)). An array type has no
    /// privileges of its own (PostgreSQL refuses a grant on one), a table's row type is the table's, and a multirange type is made with its range type: none is read.
    /// </summary>
    private static async Task<IReadOnlyList<LiveGrantSnapshot>> ReadTypeGrantsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string typeGrantSql =
            """
            select ns.nspname as schema_name,
                   typ.typname as type_name,
                   typ.typtype = 'd' as is_domain,
                   case when acl.grantee = 0 then 'PUBLIC' else pg_catalog.pg_get_userbyid(acl.grantee) end as grantee,
                   acl.is_grantable
            from pg_catalog.pg_type typ
            inner join pg_catalog.pg_namespace ns on ns.oid = typ.typnamespace
            cross join lateral pg_catalog.aclexplode(typ.typacl) acl
            where typ.typacl is not null
              and acl.privilege_type = 'USAGE'
              and acl.grantee <> typ.typowner
              and (typ.typtype in ('e', 'r', 'd')
                   or (typ.typtype = 'c' and exists (select 1 from pg_catalog.pg_class rc where rc.oid = typ.typrelid and rc.relkind = 'c'))
                   -- A base type (G-267), not the array type PostgreSQL made for one (which has no privileges of its own).
                   or (typ.typtype = 'b' and typ.typisdefined and not exists (select 1 from pg_catalog.pg_type element_of where element_of.typarray = typ.oid)))
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_type'::regclass and ext_dep.objid = typ.oid and ext_dep.deptype = 'e')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            union all
            -- A list that has been written out but names no one but the owner: PUBLIC's default was revoked, and nothing else was granted.
            select ns.nspname, typ.typname, typ.typtype = 'd', null, false
            from pg_catalog.pg_type typ
            inner join pg_catalog.pg_namespace ns on ns.oid = typ.typnamespace
            where typ.typacl is not null
              and not exists (select 1 from pg_catalog.aclexplode(typ.typacl) acl where acl.grantee <> typ.typowner and acl.privilege_type = 'USAGE')
              and (typ.typtype in ('e', 'r', 'd')
                   or (typ.typtype = 'c' and exists (select 1 from pg_catalog.pg_class rc where rc.oid = typ.typrelid and rc.relkind = 'c'))
                   -- A base type (G-267), not the array type PostgreSQL made for one (which has no privileges of its own).
                   or (typ.typtype = 'b' and typ.typisdefined and not exists (select 1 from pg_catalog.pg_type element_of where element_of.typarray = typ.oid)))
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_type'::regclass and ext_dep.objid = typ.oid and ext_dep.deptype = 'e')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas));
            """;

        var granteesByType = new SortedDictionary<(string SchemaName, string TypeName), (bool IsDomain, List<(string Grantee, bool GrantOption)> Grantees)>();
        await using (var command = CreateCommand(typeGrantSql, connection, scopeSchemas))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!granteesByType.TryGetValue(key, out var entry))
                {
                    entry = (reader.GetBoolean(2), []);
                    granteesByType[key] = entry;
                }

                if (!reader.IsDBNull(3))
                {
                    entry.Grantees.Add((reader.GetString(3), reader.GetBoolean(4)));
                }
            }
        }

        var grants = new List<LiveGrantSnapshot>();
        foreach (var ((schemaName, typeName), (isDomain, grantees)) in granteesByType)
        {
            var objectKey = $"type:{schemaName}.{typeName}()";
            var target = $"{(isDomain ? "DOMAIN" : "TYPE")} {RenderIdentifier(schemaName)}.{RenderIdentifier(typeName)}";
            if (!grantees.Any(static grantee => grantee.Grantee == "PUBLIC"))
            {
                grants.Add(new LiveGrantSnapshot(schemaName, objectKey, "PUBLIC", $"REVOKE USAGE ON {target} FROM PUBLIC"));
            }

            foreach (var (grantee, grantOption) in grantees.Where(static grantee => grantee.Grantee != "PUBLIC").OrderBy(static grantee => grantee.Grantee, StringComparer.Ordinal))
            {
                grants.Add(new LiveGrantSnapshot(schemaName, objectKey, grantee, $"GRANT USAGE ON {target} TO {RenderGrantee(grantee)}{(grantOption ? WithGrantOption : string.Empty)}"));
            }
        }

        return grants;
    }

    /// <summary>
    /// Functions and procedures without <c>PUBLIC</c>'s <c>EXECUTE</c> (G-338), as <c>REVOKE ALL ON FUNCTION|PROCEDURE ... FROM PUBLIC</c>, the form <c>pg_dump</c>
    /// writes. A routine whose list is null has PostgreSQL's default; once a grant or revoke has written the list out, one without <c>PUBLIC</c> was revoked. The
    /// other entries are read with the other objects' privileges. The same routines are read as there: not an extension's, a range type's constructors, or a base
    /// type's support functions; aggregates are not read. The key names the routine as <c>init</c> files it (by its identity arguments), and the statement by its
    /// argument types.
    /// </summary>
    private static async Task<IReadOnlyList<LiveGrantSnapshot>> ReadRoutineRevokedDefaultsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string routineRevokeSql =
            """
            select ns.nspname as schema_name,
                   proc.proname as routine_name,
                   proc.prokind = 'p' as is_procedure,
                   pg_catalog.oidvectortypes(proc.proargtypes) as argument_types,
                   pg_catalog.pg_get_function_identity_arguments(proc.oid) as identity_arguments
            from pg_catalog.pg_proc proc
            inner join pg_catalog.pg_namespace ns on ns.oid = proc.pronamespace
            where proc.prokind in ('f', 'w', 'p')
              and proc.proacl is not null
              and not exists (select 1 from pg_catalog.aclexplode(proc.proacl) acl where acl.grantee = 0 and acl.privilege_type = 'EXECUTE')
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_proc'::regclass and ext_dep.objid = proc.oid and ext_dep.deptype = 'e')
              and not exists (select 1 from pg_catalog.pg_depend int_dep where int_dep.classid = 'pg_catalog.pg_proc'::regclass and int_dep.objid = proc.oid and int_dep.deptype = 'i' and int_dep.refclassid = 'pg_catalog.pg_type'::regclass)
              and not exists (select 1 from pg_catalog.pg_type base_type where base_type.typtype = 'b' and proc.oid in (base_type.typinput::oid, base_type.typoutput::oid, base_type.typreceive::oid, base_type.typsend::oid, base_type.typmodin::oid, base_type.typmodout::oid, base_type.typanalyze::oid, base_type.typsubscript::oid))
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            order by schema_name, routine_name, argument_types;
            """;

        var grants = new List<LiveGrantSnapshot>();
        await using var command = CreateCommand(routineRevokeSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaName = reader.GetString(0);
            var name = reader.GetString(1);
            var keyword = reader.GetBoolean(2) ? "PROCEDURE" : "FUNCTION";
            grants.Add(new LiveGrantSnapshot(
                schemaName,
                $"{keyword.ToLowerInvariant()}:{schemaName}.{name}({reader.GetString(4)})",
                "PUBLIC",
                $"REVOKE ALL ON {keyword} {RenderIdentifier(schemaName)}.{RenderIdentifier(name)}({reader.GetString(3)}) FROM PUBLIC"));
        }

        return grants;
    }

    /// <summary>
    /// Rules that new types or functions are created without <c>PUBLIC</c>'s default (G-338, part (i)): a list with no <c>IN SCHEMA</c> on types or functions
    /// without <c>PUBLIC</c>'s entry, which PostgreSQL stores once a rule exists and keeps the default in unless it was revoked (G-340), read as <c>ALTER DEFAULT
    /// PRIVILEGES [FOR ROLE r] REVOKE ALL ON TYPES|FUNCTIONS FROM PUBLIC</c>. A list in one schema only adds to the global one, so it says nothing of the default.
    /// </summary>
    private static async Task<IReadOnlyList<LiveGrantSnapshot>> ReadDefaultPrivilegeRevokedDefaultsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string revokedDefaultSql =
            """
            select case defacl.defaclobjtype when 'T' then 'types' else 'functions' end as object_kind,
                   case when pg_catalog.pg_get_userbyid(defacl.defaclrole) = current_user then null else pg_catalog.pg_get_userbyid(defacl.defaclrole) end as for_role
            from pg_catalog.pg_default_acl defacl
            where defacl.defaclnamespace = 0
              and defacl.defaclobjtype in ('T', 'f')
              and not exists (select 1 from pg_catalog.aclexplode(defacl.defaclacl) acl where acl.grantee = 0)
            order by object_kind, for_role nulls first;
            """;

        var grants = new List<LiveGrantSnapshot>();
        await using var command = new NpgsqlCommand(revokedDefaultSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var objectKind = reader.GetString(0);
            var roleClause = reader.IsDBNull(1) ? string.Empty : $" FOR ROLE {RenderIdentifier(reader.GetString(1))}";
            grants.Add(new LiveGrantSnapshot(string.Empty, $"default {objectKind}", "PUBLIC", $"ALTER DEFAULT PRIVILEGES{roleClause} REVOKE ALL ON {objectKind.ToUpperInvariant()} FROM PUBLIC"));
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
    /// <c>PUBLIC</c>'s <c>USAGE</c> in a list with no <c>IN SCHEMA</c> on types, and its <c>EXECUTE</c> in one on functions, are left out too (G-340): PostgreSQL
    /// stores the whole default list for the kind once such a rule exists, and those entries are its built-in defaults, not a rule the project states.
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
              -- A rule with no IN SCHEMA on types or functions stores the kind's whole list, PUBLIC's built-in USAGE or EXECUTE included:
              -- PostgreSQL's default, not a rule (G-340).
              and not (acl.grantee = 0 and defacl.defaclnamespace = 0
                       and ((defacl.defaclobjtype = 'T' and acl.privilege_type = 'USAGE') or (defacl.defaclobjtype = 'f' and acl.privilege_type = 'EXECUTE')))
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
