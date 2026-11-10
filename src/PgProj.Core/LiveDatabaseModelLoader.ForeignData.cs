using System.Text;
using Npgsql;

namespace PgProj.Core;

/// <summary>
/// Foreign data wrappers, servers, and user mappings (G-132, the user's design of 2026-11-10). None is in a schema, so <c>schemas</c> does not scope them, as for
/// publications. A wrapper an extension installs is the extension's and is not read; the servers on it are. A user mapping's password is never read: the query
/// writes <c>*****</c> in its place, so it cannot reach the model, a message, or a file. A mapping's options are read with the condition <c>pg_user_mappings</c>
/// shows them under (the mapped role, the server's owner, or a superuser); for any other role they are marked unreadable, and not compared.
/// </summary>
public sealed partial class LiveDatabaseModelLoader
{
    /// <summary>What a password is read as: never its value.</summary>
    internal const string MaskedPassword = "*****";

    private static async Task<LiveForeignDataSnapshot> ReadForeignDataAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string wrapperSql =
            """
            select w.fdwname,
                   (select format('%I.%I', n.nspname, p.proname) from pg_catalog.pg_proc p join pg_catalog.pg_namespace n on n.oid = p.pronamespace where p.oid = w.fdwhandler),
                   (select format('%I.%I', n.nspname, p.proname) from pg_catalog.pg_proc p join pg_catalog.pg_namespace n on n.oid = p.pronamespace where p.oid = w.fdwvalidator),
                   array(select o.option_name from pg_catalog.pg_options_to_table(w.fdwoptions) o),
                   array(select o.option_value from pg_catalog.pg_options_to_table(w.fdwoptions) o),
                   pg_catalog.obj_description(w.oid, 'pg_foreign_data_wrapper')
            from pg_catalog.pg_foreign_data_wrapper w
            where not exists (select 1 from pg_catalog.pg_depend d where d.classid = 'pg_catalog.pg_foreign_data_wrapper'::regclass and d.objid = w.oid and d.deptype = 'e')
            order by w.fdwname;
            """;

        const string serverSql =
            """
            select s.srvname, w.fdwname, s.srvtype, s.srvversion,
                   array(select o.option_name from pg_catalog.pg_options_to_table(s.srvoptions) o),
                   array(select o.option_value from pg_catalog.pg_options_to_table(s.srvoptions) o),
                   pg_catalog.obj_description(s.oid, 'pg_foreign_server')
            from pg_catalog.pg_foreign_server s
            inner join pg_catalog.pg_foreign_data_wrapper w on w.oid = s.srvfdw
            where not exists (select 1 from pg_catalog.pg_depend d where d.classid = 'pg_catalog.pg_foreign_server'::regclass and d.objid = s.oid and d.deptype = 'e')
            order by s.srvname;
            """;

        // pg_user_mappings shows a mapping's options only to the mapped role (with USAGE on the server), the server's owner, or a superuser, and null to anyone else,
        // which is also what a mapping with no options shows; so whether they can be read is worked out with the view's own condition.
        const string mappingSql =
            """
            select m.srvname,
                   case when m.umuser = 0 then 'PUBLIC' else m.usename end,
                   (select r.rolsuper from pg_catalog.pg_roles r where r.rolname = current_user)
                       or pg_catalog.pg_has_role(s.srvowner, 'USAGE')
                       or (m.umuser <> 0 and m.usename = current_user and pg_catalog.has_server_privilege(m.srvid, 'USAGE')) as readable,
                   array(select o.option_name from pg_catalog.pg_options_to_table(m.umoptions) o),
                   array(select case when o.option_name = 'password' then '*****' else o.option_value end from pg_catalog.pg_options_to_table(m.umoptions) o)
            from pg_catalog.pg_user_mappings m
            inner join pg_catalog.pg_foreign_server s on s.oid = m.srvid
            order by m.srvname, 2;
            """;

        var wrappers = new List<LiveForeignDataWrapperSnapshot>();
        await using (var command = new NpgsqlCommand(wrapperSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                wrappers.Add(new LiveForeignDataWrapperSnapshot(reader.GetString(0), NullableString(reader, 1), NullableString(reader, 2), Options(reader, 3), NullableString(reader, 5)));
            }
        }

        var servers = new List<LiveForeignServerSnapshot>();
        await using (var command = new NpgsqlCommand(serverSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                servers.Add(new LiveForeignServerSnapshot(reader.GetString(0), reader.GetString(1), NullableString(reader, 2), NullableString(reader, 3), Options(reader, 4), NullableString(reader, 6)));
            }
        }

        var mappings = new List<LiveUserMappingSnapshot>();
        await using (var command = new NpgsqlCommand(mappingSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var readable = !reader.IsDBNull(2) && reader.GetBoolean(2);
                mappings.Add(new LiveUserMappingSnapshot(reader.GetString(1), reader.GetString(0), readable ? Options(reader, 3) : [], OptionsUnreadable: !readable));
            }
        }

        return new LiveForeignDataSnapshot(wrappers, servers, mappings);
    }

    /// <summary>
    /// <c>USAGE</c> on foreign data wrappers (an extension's included: a project grants it so a role can create servers on it) and on servers (G-132), each grantee
    /// but the owner. Neither has a default for <c>PUBLIC</c>.
    /// </summary>
    private static async Task<IReadOnlyList<LiveGrantSnapshot>> ReadForeignDataGrantsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string grantSql =
            """
            select 'FOREIGN DATA WRAPPER', w.fdwname, case when acl.grantee = 0 then 'PUBLIC' else pg_catalog.pg_get_userbyid(acl.grantee) end, acl.is_grantable
            from pg_catalog.pg_foreign_data_wrapper w
            cross join lateral pg_catalog.aclexplode(w.fdwacl) acl
            where acl.grantee <> w.fdwowner and acl.privilege_type = 'USAGE'
            union all
            select 'FOREIGN SERVER', s.srvname, case when acl.grantee = 0 then 'PUBLIC' else pg_catalog.pg_get_userbyid(acl.grantee) end, acl.is_grantable
            from pg_catalog.pg_foreign_server s
            cross join lateral pg_catalog.aclexplode(s.srvacl) acl
            where acl.grantee <> s.srvowner and acl.privilege_type = 'USAGE'
            order by 1, 2, 3;
            """;

        var grants = new List<LiveGrantSnapshot>();
        await using var command = new NpgsqlCommand(grantSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = reader.GetString(0);
            var name = reader.GetString(1);
            var grantee = reader.GetString(2);
            var objectKey = kind == "FOREIGN SERVER" ? PgProj.Semantics.ForeignDataKeys.Server(name) : PgProj.Semantics.ForeignDataKeys.Wrapper(name);
            grants.Add(new LiveGrantSnapshot(string.Empty, objectKey, grantee, $"GRANT USAGE ON {kind} {RenderIdentifier(name)} TO {RenderGrantee(grantee)}{(reader.GetBoolean(3) ? WithGrantOption : string.Empty)}"));
        }

        return grants;
    }

    /// <summary>The live model's user mappings, those whose options the connected role cannot read marked so (they read back from the snapshot with none).</summary>
    private static IReadOnlyDictionary<string, PgProj.Semantics.UserMappingDefinition> WithUnreadableOptions(IReadOnlyDictionary<string, PgProj.Semantics.UserMappingDefinition> mappings, LiveForeignDataSnapshot foreignData)
    {
        var unreadable = foreignData.Mappings.Where(static mapping => mapping.OptionsUnreadable).Select(static mapping => PgProj.Semantics.ForeignDataKeys.UserMapping(mapping.Role, mapping.Server)).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
        return unreadable.Count == 0
            ? mappings
            : mappings.ToDictionary(static pair => pair.Key, pair => unreadable.Contains(pair.Key) ? pair.Value with { OptionsUnreadable = true } : pair.Value, PgProj.Parser.SqlIdentifiers.NameComparer);
    }

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static IReadOnlyList<KeyValuePair<string, string>> Options(NpgsqlDataReader reader, int namesOrdinal)
    {
        var names = reader.IsDBNull(namesOrdinal) ? [] : reader.GetFieldValue<string[]>(namesOrdinal);
        var values = reader.IsDBNull(namesOrdinal + 1) ? [] : reader.GetFieldValue<string[]>(namesOrdinal + 1);
        return names.Zip(values, static (name, value) => new KeyValuePair<string, string>(name, value)).ToArray();
    }

    /// <summary>The statements the snapshot (and <c>init</c>, with <paramref name="scaffold"/>) writes for them, wrappers, then servers, then mappings.</summary>
    internal static string BuildForeignDataStatements(LiveForeignDataSnapshot foreignData, bool scaffold)
    {
        var builder = new StringBuilder();
        foreach (var wrapper in foreignData.Wrappers)
        {
            builder.AppendLine(BuildCreateForeignDataWrapperStatement(wrapper));
        }

        foreach (var server in foreignData.Servers)
        {
            builder.AppendLine(BuildCreateServerStatement(server));
            foreach (var mapping in foreignData.Mappings.Where(mapping => mapping.Server == server.Name))
            {
                builder.AppendLine(BuildCreateUserMappingStatement(mapping, scaffold));
            }
        }

        return builder.ToString();
    }

    internal static string BuildCreateForeignDataWrapperStatement(LiveForeignDataWrapperSnapshot wrapper)
        => $"CREATE FOREIGN DATA WRAPPER {RenderIdentifier(wrapper.Name)}"
            + (wrapper.Handler is { } handler ? $" HANDLER {handler}" : string.Empty)
            + (wrapper.Validator is { } validator ? $" VALIDATOR {validator}" : string.Empty)
            + RenderOptions(wrapper.Options, null) + ";"
            + (wrapper.Comment is { } comment ? $"{Environment.NewLine}COMMENT ON FOREIGN DATA WRAPPER {RenderIdentifier(wrapper.Name)} IS {QuoteLiteral(comment)};" : string.Empty);

    internal static string BuildCreateServerStatement(LiveForeignServerSnapshot server)
        => $"CREATE SERVER {RenderIdentifier(server.Name)}"
            + (server.ServerType is { } type ? $" TYPE {QuoteLiteral(type)}" : string.Empty)
            + (server.Version is { } version ? $" VERSION {QuoteLiteral(version)}" : string.Empty)
            + $" FOREIGN DATA WRAPPER {RenderIdentifier(server.Wrapper)}" + RenderOptions(server.Options, null) + ";"
            + (server.Comment is { } comment ? $"{Environment.NewLine}COMMENT ON SERVER {RenderIdentifier(server.Name)} IS {QuoteLiteral(comment)};" : string.Empty);

    /// <summary>
    /// A user mapping. For <c>init</c> (<paramref name="scaffold"/>) its password is a variable, <c>:'&lt;server&gt;_&lt;role&gt;_password'</c> (<see cref="PasswordVariable"/>),
    /// and a mapping whose options cannot be read says so; for the snapshot, the masked password, which is never compared.
    /// </summary>
    internal static string BuildCreateUserMappingStatement(LiveUserMappingSnapshot mapping, bool scaffold)
    {
        var role = mapping.Role == "PUBLIC" ? "PUBLIC" : RenderIdentifier(mapping.Role);
        var statement = $"CREATE USER MAPPING FOR {role} SERVER {RenderIdentifier(mapping.Server)}" + RenderOptions(mapping.Options, scaffold ? PasswordVariable(mapping) : null) + ";";
        return scaffold && mapping.OptionsUnreadable
            ? $"-- pgproj: the role init connected as cannot read this mapping's options (only the mapped role, the server's owner, or a superuser can); add them, a password as :'{PasswordVariable(mapping)}'.{Environment.NewLine}{statement}"
            : statement;
    }

    /// <summary>The variable <c>init</c> writes for a user mapping's password: <c>&lt;server&gt;_&lt;role&gt;_password</c>, in letters, digits, and <c>_</c>.</summary>
    internal static string PasswordVariable(LiveUserMappingSnapshot mapping)
    {
        static string Clean(string text) => new([.. text.ToLowerInvariant().Select(static character => char.IsAsciiLetterOrDigit(character) ? character : '_')]);
        return $"{Clean(mapping.Server)}_{Clean(mapping.Role)}_password";
    }

    private static string RenderOptions(IReadOnlyList<KeyValuePair<string, string>> options, string? passwordVariable)
        => options.Count == 0
            ? string.Empty
            : $" OPTIONS ({string.Join(", ", options.Select(option => $"{RenderIdentifier(option.Key)} {(passwordVariable is not null && option.Key == "password" ? $":'{passwordVariable}'" : QuoteLiteral(option.Value))}"))})";
}

/// <summary>The foreign data a database has (G-132): its own wrappers, its servers, and its user mappings.</summary>
internal sealed record LiveForeignDataSnapshot(
    IReadOnlyList<LiveForeignDataWrapperSnapshot> Wrappers,
    IReadOnlyList<LiveForeignServerSnapshot> Servers,
    IReadOnlyList<LiveUserMappingSnapshot> Mappings)
{
    public static LiveForeignDataSnapshot Empty { get; } = new([], [], []);
}

internal sealed record LiveForeignDataWrapperSnapshot(string Name, string? Handler, string? Validator, IReadOnlyList<KeyValuePair<string, string>> Options, string? Comment);

internal sealed record LiveForeignServerSnapshot(string Name, string Wrapper, string? ServerType, string? Version, IReadOnlyList<KeyValuePair<string, string>> Options, string? Comment);

/// <summary>A user mapping: its role (<c>PUBLIC</c> for every role), server, and options, the password masked; none when <paramref name="OptionsUnreadable"/>.</summary>
internal sealed record LiveUserMappingSnapshot(string Role, string Server, IReadOnlyList<KeyValuePair<string, string>> Options, bool OptionsUnreadable);
