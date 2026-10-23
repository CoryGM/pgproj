using System.Text;
using Npgsql;

namespace PgProj.Core;

/// <summary>One file <c>pgproj init</c> writes, with a path relative to the project's <c>source</c> directory.</summary>
public sealed record ScaffoldedFile(string RelativePath, string Content);

public sealed partial class LiveDatabaseModelLoader
{
    /// <summary>
    /// Reads a live database the same way <see cref="LoadAsync"/> does, but instead of merging everything into one
    /// re-parsed <see cref="DatabaseModel"/> for diffing, groups each object's own already-rendered SQL into one
    /// file per object (G-82), following the layout <c>samples/MultiSchema</c> — this repository's own hand-authored sample
    /// project — already uses: <c>Schema/&lt;schema&gt;/&lt;schema&gt;.sql</c> (the schema plus its schema-level
    /// and default-privilege grants), <c>Schema/&lt;schema&gt;/Table/&lt;table&gt;.sql</c> (a table plus its own
    /// constraints, indexes, triggers, row-level security policies, and grants together), a matching <c>View</c>/<c>Function</c>/
    /// <c>Procedure</c>/<c>Sequence</c>/<c>Type</c> folder per schema, and top-level <c>Extension/&lt;name&gt;.sql</c>
    /// for objects that do not belong to one schema. Reuses the same per-object statement builders
    /// (<see cref="BuildCreateTableStatement"/>, etc.) <see cref="GenerateSnapshotSql"/> uses, so a scaffolded
    /// project is textually built from the same source of truth as the live-model re-parse path; this method does
    /// not call or change <see cref="GenerateSnapshotSql"/> itself. <paramref name="excludeGrants"/>/<paramref
    /// name="excludeRoleMemberships"/> (G-87) drop the excluded category before scaffolding, so a project written
    /// this way starts with no grant/role-membership files for that category at all.
    /// </summary>
    public async Task<IReadOnlyList<ScaffoldedFile>> LoadForScaffoldAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken,
        bool excludeGrants = false,
        bool excludeRoleMemberships = false,
        bool excludeOwners = false)
        => (await ScaffoldAsync(connection, cancellationToken, new ScaffoldOptions(null, null, excludeGrants, excludeRoleMemberships, excludeOwners))).Files;

    /// <summary>
    /// <see cref="LoadForScaffoldAsync"/> with a choice of schemas and a full account of the result (G-157): the files, the schemas and objects modeled, what the database has
    /// that pgproj cannot read, the schemas left out, what is cluster-wide, and the references from the scaffold to a schema left out. With neither list in
    /// <paramref name="options"/> the whole database is read, as before. A name in <see cref="ScaffoldOptions.IncludeSchemas"/> that the database lacks is an error, and so is a
    /// choice that leaves no schema.
    /// </summary>
    public async Task<ScaffoldResult> ScaffoldAsync(NpgsqlConnection connection, CancellationToken cancellationToken, ScaffoldOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        if (options.IncludeSchemas is not null && options.ExcludeSchemas is not null)
        {
            throw new InvalidOperationException("--schemas and --exclude-schemas cannot be used together.");
        }

        // Read as LoadAsync reads, under the pinned path (G-93), so what init writes is what the live model reads back.
        var previousSearchPath = await LiveReadSearchPath.PinAsync(connection, null, cancellationToken);
        string? previousJit = null;
        try
        {
            previousJit = await LiveReadJit.PinAsync(connection, cancellationToken);
            // Every schema the database has (the system ones are never returned), so a choice can be checked and what it leaves out can be named.
            var allSchemaNames = (await ReadSchemasAsync(connection, [], cancellationToken)).Select(static schema => schema.SchemaName).ToArray();
            var included = ChooseSchemas(allSchemaNames, options);
            var excludedSchemas = allSchemaNames.Where(name => !included.Contains(name, StringComparer.Ordinal)).ToArray();

            // Without a choice the whole database is read (an empty scope), including what belongs to no schema, as before; with one, the readers scope by schema.
            IReadOnlyList<string> scope = options.IsScoped ? included : [];

            var schemas = await ReadSchemasAsync(connection, scope, cancellationToken);
            var types = await ReadTypesAsync(connection, scope, cancellationToken);
            var (sequences, tables) = AttachIdentitySequences(
                await ReadSequencesAsync(connection, scope, cancellationToken),
                await ReadTablesAsync(connection, scope, cancellationToken));
            var constraints = await ReadConstraintsAsync(connection, scope, cancellationToken);
            // An INVALID index, left by a failed CREATE INDEX CONCURRENTLY, is left out as pg_dump leaves it out, and reported (G-271).
            var allIndexes = await ReadIndexesAsync(connection, scope, cancellationToken);
            var indexes = allIndexes.Where(static index => index.IsValid).ToArray();
            var views = await ReadViewsAsync(connection, scope, cancellationToken);
            var functions = await ReadFunctionsAsync(connection, scope, cancellationToken);
            var triggers = await ReadTriggersAsync(connection, scope, cancellationToken);
            var policies = await ReadPoliciesAsync(connection, scope, cancellationToken);
            var eventTriggers = await ReadEventTriggersAsync(connection, scope, cancellationToken);
            var casts = await ReadCastsAsync(connection, scope, cancellationToken);
            var operators = await ReadOperatorsAsync(connection, scope, cancellationToken);
            var rules = await ReadRulesAsync(connection, scope, cancellationToken);
            var aggregates = await ReadAggregatesAsync(connection, scope, cancellationToken);
            var collations = await ReadCollationsAsync(connection, scope, cancellationToken);
            var statistics = await ReadStatisticsAsync(connection, scope, cancellationToken);
            var textSearch = await ReadTextSearchAsync(connection, scope, cancellationToken);
            var publications = await ReadPublicationsAsync(connection, scope, cancellationToken);
            var allGrants = await ReadGrantsAsync(connection, scope, cancellationToken);
            var grants = FilterGrantsForScaffold(allGrants, options.ExcludeGrants, options.ExcludeRoleMemberships);

            // Owners are always read, for the report (who owns what was modeled, and how many an option left out); they go into the files unless excluded.
            var allOwners = BuildOwnerSnapshots(await ReadOwnerRowsAsync(connection, cancellationToken), schemas, tables, views, sequences, functions, types, operators, aggregates, eventTriggers, publications, collations, statistics, textSearch);
            var owners = options.ExcludeOwners ? [] : allOwners;

            var modeled = new List<ScaffoldedObject>();
            var files = BuildScaffoldFiles(schemas, tables, constraints, indexes, views, functions, types, grants, sequences, triggers, policies, eventTriggers, casts, operators, rules, aggregates, publications, owners, collations, statistics, textSearch, modeled, options.CaseSensitivePaths);

            // A file pgproj cannot parse would give the new project a build that fails, or leave the object out (G-121): refuse instead.
            var unparseable = FindUnparseableFiles(files);
            if (unparseable.Count > 0)
            {
                throw CreateUnparseableException("scaffolded", unparseable, []);
            }

            var unsupported = (await ReadUnsupportedObjectsAsync(connection, scope, cancellationToken))
                .Concat(allIndexes.Where(static index => !index.IsValid).Select(static index => new UnsupportedObject(index.SchemaName, UnsupportedObject.InvalidIndex, index.IndexName)))
                .Concat(tables.Where(static table => table.InternalTriggerStatesAreMixed).Select(static table => new UnsupportedObject(table.SchemaName, UnsupportedObject.MixedInternalTriggerStates, table.TableName)))
                .ToArray();
            var clusterWide = await ReadClusterWideObjectsAsync(connection, allOwners, allGrants, tables, cancellationToken);
            return new ScaffoldResult(
                files,
                [.. schemas.Select(static schema => schema.SchemaName)],
                modeled,
                unsupported,
                excludedSchemas,
                clusterWide,
                FindReferencesToExcludedSchemas(files, excludedSchemas),
                options.ExcludeOwners ? allOwners.Count : 0,
                options.ExcludeGrants ? allGrants.Count(static grant => !grant.ObjectKey.StartsWith("role:", StringComparison.OrdinalIgnoreCase)) : 0,
                options.ExcludeRoleMemberships ? allGrants.Count(static grant => grant.ObjectKey.StartsWith("role:", StringComparison.OrdinalIgnoreCase)) : 0,
                FindNeededSearchPath(files));
        }
        finally
        {
            await LiveReadJit.RestoreAsync(connection, previousJit, CancellationToken.None);
            await LiveReadSearchPath.RestoreAsync(connection, null, previousSearchPath, CancellationToken.None);
        }
    }

    /// <summary>
    /// The schemas a scaffold covers: the ones <see cref="ScaffoldOptions.IncludeSchemas"/> names, or all but the ones <see cref="ScaffoldOptions.ExcludeSchemas"/> names, or all.
    /// A name is read as PostgreSQL reads one (folded to lower case unless double-quoted, G-206) and returned as the database spells it. A name to include that the database lacks, and a choice that leaves nothing, throw.
    /// </summary>
    internal static IReadOnlyList<string> ChooseSchemas(IReadOnlyList<string> allSchemaNames, ScaffoldOptions options)
    {
        // The names are typed by the user: folded as PostgreSQL folds a name, a quoted one kept as written (G-206).
        if (options.IncludeSchemas?.Select(PgProj.Parser.SqlIdentifiers.FoldName).ToArray() is { } include)
        {
            var missing = include.Where(name => !allSchemaNames.Contains(name, PgProj.Parser.SqlIdentifiers.NameComparer)).Distinct(PgProj.Parser.SqlIdentifiers.NameComparer).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException($"--schemas names {(missing.Length == 1 ? "a schema" : "schemas")} the database does not have: {string.Join(", ", missing)}.");
            }

            return [.. allSchemaNames.Where(name => include.Contains(name, PgProj.Parser.SqlIdentifiers.NameComparer))];
        }

        if (options.ExcludeSchemas?.Select(PgProj.Parser.SqlIdentifiers.FoldName).ToArray() is { } exclude)
        {
            var remaining = allSchemaNames.Where(name => !exclude.Contains(name, PgProj.Parser.SqlIdentifiers.NameComparer)).ToArray();
            if (remaining.Length == 0)
            {
                throw new InvalidOperationException("--exclude-schemas leaves no schema to scaffold.");
            }

            return remaining;
        }

        return allSchemaNames;
    }

    /// <summary>
    /// What the database has in the scaffolded schemas that pgproj cannot read (a foreign table, a text search parser or template, an operator class or family, a conversion,
    /// a base or shell type),
    /// and what belongs to no schema and is not read either (a foreign data wrapper, server, or user mapping, a language, a transform, an access method). The system's own
    /// (OIDs below 16384) and an extension's are left out.
    /// </summary>
    private static async Task<IReadOnlyList<UnsupportedObject>> ReadUnsupportedObjectsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string unsupportedSql =
            """
            select n.nspname, 'Foreign table', c.relname
            from pg_catalog.pg_class c
            inner join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where c.relkind = 'f' and (@schemas is null or n.nspname = any(@schemas))
              and not exists (select 1 from pg_catalog.pg_depend d where d.classid = 'pg_catalog.pg_class'::regclass and d.objid = c.oid and d.deptype = 'e')
            union all
            select n.nspname, 'Text search parser', p.prsname
            from pg_catalog.pg_ts_parser p
            inner join pg_catalog.pg_namespace n on n.oid = p.prsnamespace
            where p.oid >= 16384 and (@schemas is null or n.nspname = any(@schemas))
              and not exists (select 1 from pg_catalog.pg_depend d where d.classid = 'pg_catalog.pg_ts_parser'::regclass and d.objid = p.oid and d.deptype = 'e')
            union all
            select n.nspname, 'Text search template', t.tmplname
            from pg_catalog.pg_ts_template t
            inner join pg_catalog.pg_namespace n on n.oid = t.tmplnamespace
            where t.oid >= 16384 and (@schemas is null or n.nspname = any(@schemas))
              and not exists (select 1 from pg_catalog.pg_depend d where d.classid = 'pg_catalog.pg_ts_template'::regclass and d.objid = t.oid and d.deptype = 'e')
            union all
            select n.nspname, 'Operator class', o.opcname
            from pg_catalog.pg_opclass o
            inner join pg_catalog.pg_namespace n on n.oid = o.opcnamespace
            where o.oid >= 16384 and (@schemas is null or n.nspname = any(@schemas))
              and not exists (select 1 from pg_catalog.pg_depend d where d.classid = 'pg_catalog.pg_opclass'::regclass and d.objid = o.oid and d.deptype = 'e')
            union all
            select n.nspname, 'Operator family', o.opfname
            from pg_catalog.pg_opfamily o
            inner join pg_catalog.pg_namespace n on n.oid = o.opfnamespace
            where o.oid >= 16384 and (@schemas is null or n.nspname = any(@schemas))
              and not exists (select 1 from pg_catalog.pg_depend d where d.classid = 'pg_catalog.pg_opfamily'::regclass and d.objid = o.oid and d.deptype = 'e')
            union all
            select n.nspname, 'Conversion', c.conname
            from pg_catalog.pg_conversion c
            inner join pg_catalog.pg_namespace n on n.oid = c.connamespace
            where c.oid >= 16384 and (@schemas is null or n.nspname = any(@schemas))
              and not exists (select 1 from pg_catalog.pg_depend d where d.classid = 'pg_catalog.pg_conversion'::regclass and d.objid = c.oid and d.deptype = 'e')
            union all
            -- A base type, or a shell type left without a definition (G-241); an array type PostgreSQL made for one is not listed.
            select n.nspname, case t.typtype when 'b' then 'Base type' else 'Shell type' end, t.typname
            from pg_catalog.pg_type t
            inner join pg_catalog.pg_namespace n on n.oid = t.typnamespace
            where t.oid >= 16384 and (t.typtype = 'b' or (t.typtype = 'p' and not t.typisdefined))
              and not exists (select 1 from pg_catalog.pg_type element where element.typarray = t.oid)
              and (@schemas is null or n.nspname = any(@schemas))
              and not exists (select 1 from pg_catalog.pg_depend d where d.classid = 'pg_catalog.pg_type'::regclass and d.objid = t.oid and d.deptype = 'e')
            union all
            select null, 'Foreign data wrapper', w.fdwname
            from pg_catalog.pg_foreign_data_wrapper w
            where not exists (select 1 from pg_catalog.pg_depend d where d.classid = 'pg_catalog.pg_foreign_data_wrapper'::regclass and d.objid = w.oid and d.deptype = 'e')
            union all
            select null, 'Foreign server', s.srvname
            from pg_catalog.pg_foreign_server s
            union all
            select null, 'User mapping', m.srvname || ' for ' || m.usename
            from pg_catalog.pg_user_mappings m
            union all
            select null, 'Language', l.lanname
            from pg_catalog.pg_language l
            where l.oid >= 16384
              and not exists (select 1 from pg_catalog.pg_depend d where d.classid = 'pg_catalog.pg_language'::regclass and d.objid = l.oid and d.deptype = 'e')
            union all
            select null, 'Transform', pg_catalog.format_type(t.trftype, null) || ' language ' || l.lanname
            from pg_catalog.pg_transform t
            inner join pg_catalog.pg_language l on l.oid = t.trflang
            union all
            select null, 'Access method', a.amname
            from pg_catalog.pg_am a
            where a.oid >= 16384
              and not exists (select 1 from pg_catalog.pg_depend d where d.classid = 'pg_catalog.pg_am'::regclass and d.objid = a.oid and d.deptype = 'e')
            order by 1 nulls last, 2, 3;
            """;

        var objects = new List<UnsupportedObject>();
        await using var command = CreateCommand(unsupportedSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add(new UnsupportedObject(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return objects;
    }

    /// <summary>
    /// What belongs to the whole server and touches what was modeled: the roles that own a modeled object or hold a privilege or membership on one, the tablespaces a modeled table
    /// names, and the subscriptions of this database. pgproj reports these and does not model them (they are shared by every database on the server).
    /// </summary>
    private static async Task<IReadOnlyList<ClusterWideObject>> ReadClusterWideObjectsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<LiveOwnerSnapshot> owners,
        IReadOnlyList<LiveGrantSnapshot> grants,
        IReadOnlyList<LiveTableSnapshot> tables,
        CancellationToken cancellationToken)
    {
        var objects = new List<ClusterWideObject>();
        var roles = owners.Select(static owner => owner.Owner)
            .Concat(grants.Select(static grant => grant.Grantee))
            .Where(static role => !string.IsNullOrWhiteSpace(role) && !string.Equals(role, "PUBLIC", StringComparison.OrdinalIgnoreCase) && !role.StartsWith("pg_", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        objects.AddRange(roles.Select(static role => new ClusterWideObject("Role", role)));
        objects.AddRange(tables.Select(static table => table.TablespaceName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(static name => new ClusterWideObject("Tablespace", name!)));

        try
        {
            await using var command = new NpgsqlCommand(
                "select s.subname from pg_catalog.pg_subscription s where s.subdbid = (select d.oid from pg_catalog.pg_database d where d.datname = current_database()) order by s.subname",
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                objects.Add(new ClusterWideObject("Subscription", reader.GetString(0)));
            }
        }
        catch (PostgresException)
        {
            // A role that may not read pg_subscription has nothing to report here; the others above still stand.
        }

        return objects;
    }

    /// <summary>
    /// The scaffolded files that name a schema the scaffold left out (<c>excluded.name</c> or <c>"excluded"."name"</c>), counted per file and schema. It is a text search of the
    /// generated SQL, which is schema-qualified except for <c>public</c> (read under a path of <c>public</c>), so a reference into an excluded <c>public</c> is not found.
    /// </summary>
    internal static IReadOnlyList<DanglingReference> FindReferencesToExcludedSchemas(IReadOnlyList<ScaffoldedFile> files, IReadOnlyList<string> excludedSchemas)
    {
        var references = new List<DanglingReference>();
        foreach (var schema in excludedSchemas)
        {
            var pattern = new System.Text.RegularExpressions.Regex(
                $"(?<![\\w.\"]){System.Text.RegularExpressions.Regex.Escape(schema)}\\.|(?<![\\w.\"])\"{System.Text.RegularExpressions.Regex.Escape(schema.Replace("\"", "\"\""))}\"\\.",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            foreach (var file in files)
            {
                var count = pattern.Matches(file.Content).Count;
                if (count > 0)
                {
                    references.Add(new DanglingReference(file.RelativePath, schema, count));
                }
            }
        }

        return references;
    }

    /// <summary>
    /// G-87: applied after the read (not by skipping the query above) so the read stays identical to
    /// <see cref="LoadAsync"/>'s, and the exclusion rule — a <c>role:</c>-prefixed <see
    /// cref="LiveGrantSnapshot.ObjectKey"/> is role membership, everything else is a grant — lives in one place per
    /// side rather than duplicated into the query layer.
    /// </summary>
    internal static IReadOnlyList<LiveGrantSnapshot> FilterGrantsForScaffold(
        IReadOnlyList<LiveGrantSnapshot> grants,
        bool excludeGrants,
        bool excludeRoleMemberships)
        => grants
            .Where(grant => !(grant.ObjectKey.StartsWith("role:", StringComparison.OrdinalIgnoreCase) ? excludeRoleMemberships : excludeGrants))
            .ToArray();

    internal static IReadOnlyList<ScaffoldedFile> BuildScaffoldFiles(
        IReadOnlyList<LiveSchemaSnapshot> schemas,
        IReadOnlyList<LiveTableSnapshot> tables,
        IReadOnlyList<LiveConstraintSnapshot> constraints,
        IReadOnlyList<LiveIndexSnapshot> indexes,
        IReadOnlyList<LiveViewSnapshot> views,
        IReadOnlyList<LiveFunctionSnapshot> functions,
        IReadOnlyList<LiveTypeSnapshot> types,
        IReadOnlyList<LiveGrantSnapshot> grants,
        IReadOnlyList<LiveSequenceSnapshot> sequences,
        IReadOnlyList<LiveTriggerSnapshot> triggers,
        IReadOnlyList<LivePolicySnapshot> policies,
        IReadOnlyList<LiveEventTriggerSnapshot>? eventTriggers = null,
        IReadOnlyList<LiveCastSnapshot>? casts = null,
        IReadOnlyList<LiveOperatorSnapshot>? operators = null,
        IReadOnlyList<LiveRuleSnapshot>? rules = null,
        IReadOnlyList<LiveAggregateSnapshot>? aggregates = null,
        IReadOnlyList<LivePublicationSnapshot>? publications = null,
        IReadOnlyList<LiveOwnerSnapshot>? owners = null,
        IReadOnlyList<LiveCollationSnapshot>? collations = null,
        IReadOnlyList<LiveStatisticsSnapshot>? statistics = null,
        LiveTextSearchSnapshot? textSearch = null,
        List<ScaffoldedObject>? modeled = null,
        bool caseSensitivePaths = false)
    {
        // Two paths are one file when the file system the files are written to says so (G-212).
        var pathComparer = caseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var usedPaths = new HashSet<string>(pathComparer);
        void Record(string? schema, string type, string name, string path) => modeled?.Add(new ScaffoldedObject(schema, type, name, path));
        // Where each object's own file is, so its owner is written beside it (G-146): "kind|schema|name|arguments".
        var pathByOwnerKey = new Dictionary<string, string>(StringComparer.Ordinal);
        static string OwnerKey(string kind, string? schema, string name, string arguments) => $"{kind}|{schema}|{name}|{arguments}";
        var contentByPath = new Dictionary<string, StringBuilder>(pathComparer);
        // Exact-match lookups from a grant's ObjectKey (built the same way LiveDatabaseModelLoader.Grants.cs builds
        // it: "{kind}:{schema}.{name}({args})", empty parentheses for a table/view/sequence) to the file that owns
        // that object, so a per-object grant lands beside the object it grants, not just in the schema file.
        var pathByObjectKey = new Dictionary<string, string>(StringComparer.Ordinal);

        StringBuilder GetOrCreateFile(string relativePath)
        {
            if (!contentByPath.TryGetValue(relativePath, out var builder))
            {
                builder = new StringBuilder();
                contentByPath[relativePath] = builder;
            }

            return builder;
        }

        string SchemaFilePath(string schemaName)
            => MakeUniquePath($"Schema/{SanitizeSegment(schemaName)}/{SanitizeSegment(schemaName)}.sql", usedPaths, reserveOnly: true);

        // Every schema gets its own file up front (even "public", which already exists live — CREATE SCHEMA IF NOT
        // EXISTS makes that harmless) so schema-level and default-privilege grants always have somewhere to land,
        // even for a schema with no tables/views/etc. of its own.
        var schemaFilePaths = new Dictionary<string, string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var schema in schemas.OrderBy(static schema => schema.SchemaName, StringComparer.OrdinalIgnoreCase))
        {
            var path = SchemaFilePath(schema.SchemaName);
            schemaFilePaths[schema.SchemaName] = path;
            var schemaFile = GetOrCreateFile(path);
            schemaFile.Append("CREATE SCHEMA IF NOT EXISTS ").Append(RenderIdentifier(schema.SchemaName)).AppendLine(";");
            if (schema.StatedComment is { } schemaComment)
            {
                schemaFile.Append("COMMENT ON SCHEMA ").Append(RenderIdentifier(schema.SchemaName)).Append(" IS ").Append(QuoteLiteral(schemaComment)).AppendLine(";");
            }
        }

        string GetSchemaFilePath(string schemaName)
        {
            if (!schemaFilePaths.TryGetValue(schemaName, out var path))
            {
                // A schema referenced by an object but not itself returned by ReadSchemasAsync (should not happen
                // against a real server, but keeps this method total rather than throwing on unexpected input).
                path = SchemaFilePath(schemaName);
                schemaFilePaths[schemaName] = path;
                GetOrCreateFile(path).Append("CREATE SCHEMA IF NOT EXISTS ").Append(RenderIdentifier(schemaName)).AppendLine(";");
            }

            return path;
        }

        // An extension is created in the order the project declares it, and the project reads its files in path order (G-213). One that requires others is
        // written after them, in the file of the one that comes last, so it is declared after all of them; any other has a file of its own. Its version is
        // left out, so any will do, and its comment is written only when it is not the one the extension comes with.
        var extensionPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var extension in LiveTypeSnapshot.OrderExtensions(types.Where(static type => type.IsExtension)))
        {
            var relativePath = extension.Requires.Where(extensionPaths.ContainsKey).Select(required => extensionPaths[required]).Max(StringComparer.OrdinalIgnoreCase)
                ?? MakeUniquePath($"Extension/{SanitizeSegment(extension.TypeName)}.sql", usedPaths);
            extensionPaths[extension.TypeName] = relativePath;

            var builder = GetOrCreateFile(relativePath);
            builder.Append(extension.Statement.Trim().TrimEnd(';')).AppendLine(";");
            Record(extension.SchemaName, "Extension", extension.TypeName, relativePath);
            if (extension.ExtensionCommentStatement is { } extensionComment)
            {
                builder.AppendLine(extensionComment);
            }
        }

        foreach (var type in types.Where(static type => !type.IsExtension).OrderBy(static type => type.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static type => type.TypeName, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = MakeUniquePath($"Schema/{SanitizeSegment(type.SchemaName)}/Type/{SanitizeSegment(type.TypeName)}.sql", usedPaths);

            var builder = GetOrCreateFile(relativePath);
            builder.Append(type.Statement.Trim().TrimEnd(';')).AppendLine(";");
            pathByOwnerKey[OwnerKey("type", type.SchemaName, type.TypeName, string.Empty)] = relativePath;
            pathByOwnerKey[OwnerKey("domain", type.SchemaName, type.TypeName, string.Empty)] = relativePath;
            Record(type.SchemaName, string.Equals(type.TypeKind, "domain", StringComparison.OrdinalIgnoreCase) ? "Domain" : "Type", type.TypeName, relativePath);
            if (!string.IsNullOrEmpty(type.Comment))
            {
                builder.Append("COMMENT ON TYPE ").Append(QualifyIdentifier(type.SchemaName, type.TypeName)).Append(" IS ").Append(QuoteLiteral(type.Comment)).AppendLine(";");
            }

            foreach (var columnComment in type.ColumnCommentStatements)
            {
                builder.Append(columnComment).AppendLine(";");
            }
        }

        foreach (var sequence in sequences.OrderBy(static sequence => sequence.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static sequence => sequence.SequenceName, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = MakeUniquePath($"Schema/{SanitizeSegment(sequence.SchemaName)}/Sequence/{SanitizeSegment(sequence.SequenceName)}.sql", usedPaths);
            GetOrCreateFile(relativePath).AppendLine(BuildCreateSequenceStatement(sequence));
            pathByObjectKey[$"sequence:{sequence.SchemaName}.{sequence.SequenceName}()"] = relativePath;
            pathByOwnerKey[OwnerKey("sequence", sequence.SchemaName, sequence.SequenceName, string.Empty)] = relativePath;
            Record(sequence.SchemaName, "Sequence", sequence.SequenceName, relativePath);
        }

        foreach (var table in tables.OrderBy(static table => table.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static table => table.TableName, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = MakeUniquePath($"Schema/{SanitizeSegment(table.SchemaName)}/Table/{SanitizeSegment(table.TableName)}.sql", usedPaths);
            GetOrCreateFile(relativePath).AppendLine(BuildCreateTableStatement(table, scaffold: true));
            pathByObjectKey[$"table:{table.SchemaName}.{table.TableName}()"] = relativePath;
            pathByOwnerKey[OwnerKey("table", table.SchemaName, table.TableName, string.Empty)] = relativePath;
            Record(table.SchemaName, "Table", table.TableName, relativePath);
        }

        foreach (var constraint in constraints.OrderBy(static constraint => constraint.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static constraint => constraint.TableName, StringComparer.OrdinalIgnoreCase).ThenBy(static constraint => constraint.ConstraintName, StringComparer.OrdinalIgnoreCase))
        {
            if (!pathByObjectKey.TryGetValue($"table:{constraint.SchemaName}.{constraint.TableName}()", out var tablePath))
            {
                continue;
            }

            Record(constraint.SchemaName, "Constraint", $"{constraint.TableName}.{constraint.ConstraintName}", tablePath);
            GetOrCreateFile(tablePath)
                .Append("ALTER TABLE ").Append(QualifyIdentifier(constraint.SchemaName, constraint.TableName))
                .Append(" ADD CONSTRAINT ").Append(QuoteIdentifier(constraint.ConstraintName)).Append(' ')
                .Append(WithIndexOptions(constraint.ConstraintDefinition.Trim().TrimEnd(';'), constraint.IndexOptions)).AppendLine(";");
            GetOrCreateFile(tablePath).Append(IndexFlagStatements(constraint.SchemaName, constraint.TableName, constraint.ConstraintName, constraint.IsClustered, constraint.IsReplicaIdentity));
            if (!string.IsNullOrEmpty(constraint.Comment))
            {
                GetOrCreateFile(tablePath)
                    .Append("COMMENT ON CONSTRAINT ").Append(QuoteIdentifier(constraint.ConstraintName)).Append(" ON ").Append(QualifyIdentifier(constraint.SchemaName, constraint.TableName))
                    .Append(" IS ").Append(QuoteLiteral(constraint.Comment)).AppendLine(";");
            }

            if (BuildConstraintIndexCommentStatement(constraint) is { Length: > 0 } indexComment)
            {
                GetOrCreateFile(tablePath).AppendLine(indexComment);
            }
        }

        foreach (var index in indexes.OrderBy(static index => index.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static index => index.TableName, StringComparer.OrdinalIgnoreCase).ThenBy(static index => index.IndexName, StringComparer.OrdinalIgnoreCase))
        {
            if (!pathByObjectKey.TryGetValue($"table:{index.SchemaName}.{index.TableName}()", out var tablePath))
            {
                continue;
            }

            var builder = GetOrCreateFile(tablePath);
            builder.Append(index.IndexDefinition.Trim().TrimEnd(';')).AppendLine(";");
            builder.Append(IndexFlagStatements(index.SchemaName, index.TableName, index.IndexName, index.IsClustered, index.IsReplicaIdentity));
            Record(index.SchemaName, "Index", index.IndexName, tablePath);
            if (!string.IsNullOrEmpty(index.Comment))
            {
                builder.Append("COMMENT ON INDEX ").Append(QualifyIdentifier(index.SchemaName, index.IndexName)).Append(" IS ").Append(QuoteLiteral(index.Comment)).AppendLine(";");
            }
        }

        // A trigger goes in its table's file; one on a view waits until the views are written below, and goes in the view's file (G-203).
        var viewKeys = new HashSet<string>(StringComparer.Ordinal);
        var orderedTriggers = triggers.OrderBy(static trigger => trigger.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static trigger => trigger.TableName, StringComparer.OrdinalIgnoreCase).ThenBy(static trigger => trigger.TriggerName, StringComparer.OrdinalIgnoreCase).ToArray();
        void WriteTriggers(bool onViews)
        {
            foreach (var trigger in orderedTriggers)
            {
                var key = $"table:{trigger.SchemaName}.{trigger.TableName}()";
                if (!pathByObjectKey.TryGetValue(key, out var relationPath) || viewKeys.Contains(key) != onViews)
                {
                    continue;
                }

                GetOrCreateFile(relationPath).AppendLine(BuildCreateTriggerStatement(trigger));
                if (!string.IsNullOrEmpty(trigger.Comment))
                {
                    GetOrCreateFile(relationPath).AppendLine(BuildTriggerCommentStatement(trigger));
                }

                Record(trigger.SchemaName, "Trigger", $"{trigger.TableName}.{trigger.TriggerName}", relationPath);
            }
        }

        WriteTriggers(onViews: false);

        // The disabled internal (foreign-key) triggers of a table (G-261), after its own triggers.
        foreach (var table in tables.Where(static table => table.InternalTriggerState is not null))
        {
            if (pathByObjectKey.TryGetValue($"table:{table.SchemaName}.{table.TableName}()", out var internalTriggerPath))
            {
                GetOrCreateFile(internalTriggerPath).Append(InternalTriggerStatements(table, triggers));
            }
        }

        foreach (var policy in policies.OrderBy(static policy => policy.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static policy => policy.TableName, StringComparer.OrdinalIgnoreCase).ThenBy(static policy => policy.PolicyName, StringComparer.OrdinalIgnoreCase))
        {
            if (!pathByObjectKey.TryGetValue($"table:{policy.SchemaName}.{policy.TableName}()", out var tablePath))
            {
                continue;
            }

            GetOrCreateFile(tablePath).AppendLine(BuildCreatePolicyStatement(policy));
            if (!string.IsNullOrEmpty(policy.Comment))
            {
                GetOrCreateFile(tablePath).AppendLine(BuildPolicyCommentStatement(policy));
            }

            Record(policy.SchemaName, "Policy", $"{policy.TableName}.{policy.PolicyName}", tablePath);
        }

        // An event trigger belongs to no schema and no table, so each has a file of its own at the top, like an extension (G-127).
        foreach (var eventTrigger in (eventTriggers ?? []).OrderBy(static eventTrigger => eventTrigger.Name, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = MakeUniquePath($"EventTrigger/{SanitizeSegment(eventTrigger.Name)}.sql", usedPaths);
            GetOrCreateFile(relativePath).AppendLine(BuildCreateEventTriggerStatement(eventTrigger));
            pathByOwnerKey[OwnerKey("event trigger", null, eventTrigger.Name, string.Empty)] = relativePath;
            Record(null, "Event trigger", eventTrigger.Name, relativePath);
        }

        // An operator is in a schema: a file of its own under the schema's OPERATOR folder, named by its symbols spelled out and its operand types (G-129).
        foreach (var operatorSnapshot in (operators ?? []).OrderBy(static o => o.OperatorSchema, StringComparer.OrdinalIgnoreCase).ThenBy(static o => o.OperatorName, StringComparer.Ordinal).ThenBy(static o => o.LeftType, StringComparer.OrdinalIgnoreCase).ThenBy(static o => o.RightType, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = $"{SpellOperator(operatorSnapshot.OperatorName)}__{operatorSnapshot.LeftType ?? "none"}__{operatorSnapshot.RightType ?? "none"}";
            var relativePath = MakeUniquePath($"Schema/{SanitizeSegment(operatorSnapshot.OperatorSchema)}/Operator/{SanitizeSegment(fileName)}.sql", usedPaths);
            GetOrCreateFile(relativePath).AppendLine(BuildCreateOperatorStatement(operatorSnapshot));
            pathByOwnerKey.TryAdd(OwnerKey("operator", operatorSnapshot.OperatorSchema, operatorSnapshot.OperatorName, string.Empty), relativePath);
            Record(operatorSnapshot.OperatorSchema, "Operator", $"{operatorSnapshot.OperatorName} ({operatorSnapshot.LeftType ?? "none"}, {operatorSnapshot.RightType ?? "none"})", relativePath);
        }

        // A cast belongs to no schema either: a file of its own at the top, named by the two types (G-131).
        foreach (var cast in (casts ?? []).OrderBy(static cast => cast.SourceType, StringComparer.OrdinalIgnoreCase).ThenBy(static cast => cast.TargetType, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = MakeUniquePath($"Cast/{SanitizeSegment(cast.SourceType)}_to_{SanitizeSegment(cast.TargetType)}.sql", usedPaths);
            GetOrCreateFile(relativePath).AppendLine(BuildCreateCastStatement(cast));
            Record(null, "Cast", $"{cast.SourceType} AS {cast.TargetType}", relativePath);
        }

        foreach (var view in views.OrderBy(static view => view.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static view => view.ViewName, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = MakeUniquePath($"Schema/{SanitizeSegment(view.SchemaName)}/View/{SanitizeSegment(view.ViewName)}.sql", usedPaths);
            var builder = GetOrCreateFile(relativePath);
            var createKeyword = view.IsMaterialized ? "CREATE MATERIALIZED VIEW " : "CREATE OR REPLACE VIEW ";
            builder.Append(view.PlaceholderStatement).Append(createKeyword).Append(QualifyIdentifier(view.SchemaName, view.ViewName)).Append(view.HeaderClause(scaffold: true)).AppendLine(" AS")
                .Append(view.ScriptedQuery).AppendLine(";");
            if (!string.IsNullOrEmpty(view.Comment))
            {
                var viewKeyword = view.IsMaterialized ? "MATERIALIZED VIEW" : "VIEW";
                builder.Append("COMMENT ON ").Append(viewKeyword).Append(' ').Append(QualifyIdentifier(view.SchemaName, view.ViewName)).Append(" IS ").Append(QuoteLiteral(view.Comment)).AppendLine(";");
            }

            foreach (var columnComment in view.ColumnCommentStatements)
            {
                builder.Append(columnComment).AppendLine(";");
            }

            foreach (var columnDefault in view.ColumnDefaultStatements)
            {
                builder.Append(columnDefault).AppendLine(";");
            }

            // A view and a table cannot share a name in the same schema (PostgreSQL treats both as relations), so
            // this is safe to add alongside the table lookup above without a collision risk in practice.
            if (pathByObjectKey.TryAdd($"table:{view.SchemaName}.{view.ViewName}()", relativePath))
            {
                viewKeys.Add($"table:{view.SchemaName}.{view.ViewName}()");
            }

            pathByOwnerKey[OwnerKey(view.IsMaterialized ? "materialized view" : "view", view.SchemaName, view.ViewName, string.Empty)] = relativePath;
            Record(view.SchemaName, view.IsMaterialized ? "Materialized view" : "View", view.ViewName, relativePath);
        }

        WriteTriggers(onViews: true);

        // A materialized view owns indexes too: they go in its file, after the view (G-171). The indexes of tables were written above.
        var materializedViews = views
            .Where(static view => view.IsMaterialized)
            .Select(static view => $"{view.SchemaName}.{view.ViewName}")
            .ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var index in indexes
            .Where(index => materializedViews.Contains($"{index.SchemaName}.{index.TableName}"))
            .OrderBy(static index => index.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static index => index.TableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static index => index.IndexName, StringComparer.OrdinalIgnoreCase))
        {
            if (!pathByObjectKey.TryGetValue($"table:{index.SchemaName}.{index.TableName}()", out var viewPath))
            {
                continue;
            }

            var viewFile = GetOrCreateFile(viewPath);
            viewFile.Append(index.IndexDefinition.Trim().TrimEnd(';')).AppendLine(";");
            viewFile.Append(IndexFlagStatements(index.SchemaName, index.TableName, index.IndexName, index.IsClustered, index.IsReplicaIdentity));
            Record(index.SchemaName, "Index", index.IndexName, viewPath);
            if (!string.IsNullOrEmpty(index.Comment))
            {
                viewFile.Append("COMMENT ON INDEX ").Append(QualifyIdentifier(index.SchemaName, index.IndexName)).Append(" IS ").Append(QuoteLiteral(index.Comment)).AppendLine(";");
            }
        }

        // A rule is on a table or a view: it goes in that relation's file, after the relation (G-128).
        foreach (var rule in (rules ?? []).OrderBy(static rule => rule.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static rule => rule.TableName, StringComparer.OrdinalIgnoreCase).ThenBy(static rule => rule.RuleName, StringComparer.OrdinalIgnoreCase))
        {
            if (!pathByObjectKey.TryGetValue($"table:{rule.SchemaName}.{rule.TableName}()", out var relationPath))
            {
                continue;
            }

            GetOrCreateFile(relationPath).AppendLine(BuildCreateRuleStatement(rule));
            Record(rule.SchemaName, "Rule", $"{rule.TableName}.{rule.RuleName}", relationPath);
        }

        // Text search dictionaries and configurations are in a schema: a file each under the schema's TextSearchDictionary and TextSearchConfiguration folders (G-149).
        foreach (var dictionary in (textSearch ?? LiveTextSearchSnapshot.Empty).Dictionaries.OrderBy(static item => item.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.Name, StringComparer.Ordinal))
        {
            var relativePath = MakeUniquePath($"Schema/{SanitizeSegment(dictionary.SchemaName)}/TextSearchDictionary/{SanitizeSegment(dictionary.Name)}.sql", usedPaths);
            var dictionaryFile = GetOrCreateFile(relativePath);
            foreach (var statement in BuildTextSearchStatements(new LiveTextSearchSnapshot([dictionary], [])))
            {
                dictionaryFile.AppendLine(statement);
            }

            pathByOwnerKey.TryAdd(OwnerKey("text search dictionary", dictionary.SchemaName, dictionary.Name, string.Empty), relativePath);
            Record(dictionary.SchemaName, "Text search dictionary", dictionary.Name, relativePath);
        }

        foreach (var configuration in (textSearch ?? LiveTextSearchSnapshot.Empty).Configurations.OrderBy(static item => item.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.Name, StringComparer.Ordinal))
        {
            var relativePath = MakeUniquePath($"Schema/{SanitizeSegment(configuration.SchemaName)}/TextSearchConfiguration/{SanitizeSegment(configuration.Name)}.sql", usedPaths);
            var file = GetOrCreateFile(relativePath);
            foreach (var statement in BuildTextSearchStatements(new LiveTextSearchSnapshot([], [configuration])))
            {
                file.AppendLine(statement);
            }

            pathByOwnerKey.TryAdd(OwnerKey("text search configuration", configuration.SchemaName, configuration.Name, string.Empty), relativePath);
            Record(configuration.SchemaName, "Text search configuration", configuration.Name, relativePath);
        }

        // Extended statistics are about one table: they go in that table's file, after the table (G-150).
        foreach (var item in (statistics ?? []).OrderBy(static item => item.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.Name, StringComparer.Ordinal))
        {
            if (!pathByObjectKey.TryGetValue($"table:{item.TableSchemaName}.{item.TableName}()", out var tablePath))
            {
                continue;
            }

            var tableFile = GetOrCreateFile(tablePath);
            tableFile.AppendLine(item.Definition.TrimEnd().TrimEnd(';') + ";");
            if (item.Comment is not null)
            {
                tableFile.AppendLine(BuildObjectCommentStatement("STATISTICS", item.SchemaName, item.Name, item.Comment));
            }

            pathByOwnerKey.TryAdd(OwnerKey("statistics", item.SchemaName, item.Name, string.Empty), tablePath);
            Record(item.SchemaName, "Statistics", item.Name, tablePath);
        }

        // A collation is in a schema: a file of its own under the schema's COLLATION folder (G-148).
        foreach (var collation in (collations ?? []).OrderBy(static collation => collation.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static collation => collation.Name, StringComparer.Ordinal))
        {
            var relativePath = MakeUniquePath($"Schema/{SanitizeSegment(collation.SchemaName)}/Collation/{SanitizeSegment(collation.Name)}.sql", usedPaths);
            var collationFile = GetOrCreateFile(relativePath);
            collationFile.AppendLine(BuildCreateCollationStatement(collation));
            if (collation.Comment is not null)
            {
                collationFile.AppendLine(BuildObjectCommentStatement("COLLATION", collation.SchemaName, collation.Name, collation.Comment));
            }

            pathByOwnerKey.TryAdd(OwnerKey("collation", collation.SchemaName, collation.Name, string.Empty), relativePath);
            Record(collation.SchemaName, "Collation", collation.Name, relativePath);
        }

        // An aggregate is in a schema: a file of its own under the schema's AGGREGATE folder; overloads get a number after the name (G-130).
        foreach (var aggregate in (aggregates ?? []).OrderBy(static aggregate => aggregate.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static aggregate => aggregate.Name, StringComparer.Ordinal).ThenBy(static aggregate => aggregate.Arguments, StringComparer.Ordinal))
        {
            var relativePath = MakeUniquePath($"Schema/{SanitizeSegment(aggregate.SchemaName)}/Aggregate/{SanitizeSegment(aggregate.Name)}.sql", usedPaths);
            GetOrCreateFile(relativePath).AppendLine(BuildCreateAggregateStatement(aggregate));
            pathByOwnerKey.TryAdd(OwnerKey("aggregate", aggregate.SchemaName, aggregate.Name, string.Empty), relativePath);
            Record(aggregate.SchemaName, "Aggregate", $"{aggregate.Name}({aggregate.Arguments})", relativePath);
        }

        // A publication belongs to no schema, so each has a file of its own at the top, like an event trigger (G-134).
        foreach (var publication in (publications ?? []).OrderBy(static publication => publication.Name, StringComparer.Ordinal))
        {
            var relativePath = MakeUniquePath($"Publication/{SanitizeSegment(publication.Name)}.sql", usedPaths);
            GetOrCreateFile(relativePath).AppendLine(BuildCreatePublicationStatement(publication));
            pathByOwnerKey[OwnerKey("publication", null, publication.Name, string.Empty)] = relativePath;
            Record(null, "Publication", publication.Name, relativePath);
        }

        foreach (var function in functions.OrderBy(static function => function.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static function => function.FunctionName, StringComparer.OrdinalIgnoreCase).ThenBy(static function => function.IdentityArguments, StringComparer.OrdinalIgnoreCase))
        {
            var kindFolder = function.IsProcedure ? "Procedure" : "Function";
            var relativePath = MakeUniquePath($"Schema/{SanitizeSegment(function.SchemaName)}/{kindFolder}/{SanitizeSegment(function.FunctionName)}.sql", usedPaths);
            var builder = GetOrCreateFile(relativePath);
            builder.Append(function.FunctionDefinition.Trim().TrimEnd(';')).AppendLine(";");
            if (!string.IsNullOrEmpty(function.Comment))
            {
                var routineKeyword = function.IsProcedure ? "Procedure" : "Function";
                builder.Append("COMMENT ON ").Append(routineKeyword).Append(' ').Append(QualifyIdentifier(function.SchemaName, function.FunctionName))
                    .Append('(').Append(function.IdentityArguments).Append(')').Append(" IS ").Append(QuoteLiteral(function.Comment)).AppendLine(";");
            }

            var keyKind = function.IsProcedure ? "procedure" : "function";
            pathByObjectKey[$"{keyKind}:{function.SchemaName}.{function.FunctionName}({function.IdentityArguments})"] = relativePath;
            pathByOwnerKey[OwnerKey(keyKind, function.SchemaName, function.FunctionName, function.IdentityArguments ?? string.Empty)] = relativePath;
            Record(function.SchemaName, function.IsProcedure ? "Procedure" : "Function", $"{function.FunctionName}({function.IdentityArguments})", relativePath);
        }

        // Grants: route to the specific object's file when one was found above (a per-table/view/sequence/
        // function/procedure grant); otherwise to the grant's own schema file (schema-level, ALL ... IN SCHEMA,
        // and IN SCHEMA default-privilege grants all carry a SchemaName); otherwise (role membership, a global
        // default-privilege grant) to a top-level catch-all file. This degrades gracefully: a grant that doesn't
        // match its object's exact key still lands on the right schema, never dropped.
        var roleFilePath = MakeUniquePath("Role/role-memberships.sql", usedPaths, reserveOnly: true);
        var miscGrantsFilePath = MakeUniquePath("Grants/other.sql", usedPaths, reserveOnly: true);
        foreach (var grant in grants
            .OrderBy(static grant => grant.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static grant => grant.ObjectKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static grant => grant.Grantee, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static grant => grant.Statement, StringComparer.Ordinal))
        {
            string targetPath;
            if (pathByObjectKey.TryGetValue(grant.ObjectKey, out var objectPath))
            {
                targetPath = objectPath;
            }
            else if (!string.IsNullOrWhiteSpace(grant.SchemaName))
            {
                targetPath = GetSchemaFilePath(grant.SchemaName);
            }
            else if (grant.ObjectKey.StartsWith("role:", StringComparison.Ordinal))
            {
                targetPath = roleFilePath;
            }
            else
            {
                targetPath = miscGrantsFilePath;
            }

            GetOrCreateFile(targetPath).Append(grant.Statement.Trim().TrimEnd(';')).AppendLine(";");
        }

        // Owners (G-146): each beside the object it is the owner of, or in the object's schema file when it has no file of its own. A sequence a column owns
        // has no owner of its own to set (PostgreSQL refuses it: it follows its table), so it is left out.
        var ownedSequences = sequences
            .Where(static sequence => sequence.OwnedByTable is not null)
            .Select(static sequence => (sequence.SchemaName, sequence.SequenceName))
            .ToHashSet();
        foreach (var owner in owners ?? [])
        {
            if (owner.Kind == "sequence" && ownedSequences.Contains((owner.SchemaName ?? string.Empty, owner.Name)))
            {
                continue;
            }

            string? ownerPath = null;
            if (owner.Kind == "schema")
            {
                ownerPath = GetSchemaFilePath(owner.Name);
            }
            else if (!pathByOwnerKey.TryGetValue(OwnerKey(owner.Kind, owner.SchemaName, owner.Name, owner.Kind is "aggregate" or "operator" ? string.Empty : owner.Arguments), out ownerPath)
                && owner.SchemaName is not null)
            {
                ownerPath = GetSchemaFilePath(owner.SchemaName);
            }

            if (ownerPath is not null)
            {
                GetOrCreateFile(ownerPath).AppendLine(owner.Statement);
            }
        }

        return contentByPath
            .Where(static entry => entry.Value.Length > 0)
            .OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static entry => new ScaffoldedFile(entry.Key, entry.Value.ToString()))
            .ToArray();
    }

    /// <summary>Replaces characters that are invalid in a Windows or Unix file name with <c>_</c>.</summary>
    private static string SanitizeSegment(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.IsNullOrEmpty(name)
            ? "_"
            : new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    /// <summary>An operator's symbols as words, so its file name says what it is (<c>===</c> is <c>eq_eq_eq</c>); a file name cannot hold most of them.</summary>
    private static string SpellOperator(string name)
        => string.Join('_', name.Select(static symbol => symbol switch
        {
            '+' => "plus",
            '-' => "minus",
            '*' => "star",
            '/' => "slash",
            '<' => "lt",
            '>' => "gt",
            '=' => "eq",
            '~' => "tilde",
            '!' => "bang",
            '@' => "at",
            '#' => "hash",
            '%' => "percent",
            '^' => "caret",
            '&' => "amp",
            '|' => "pipe",
            '`' => "tick",
            '?' => "question",
            _ => symbol.ToString(),
        }));

    /// <summary>
    /// Returns <paramref name="relativePath"/> unchanged if it is not already in <paramref name="usedPaths"/>
    /// (case-insensitively, since Windows file systems are); otherwise appends <c>~2</c>, <c>~3</c>, ... before the
    /// extension until it is unique. The only realistic collision is two overloaded functions/procedures sharing a
    /// name. <paramref name="reserveOnly"/> is used for a path computed ahead of the object that owns it (a
    /// schema's own file), where the caller already knows the path is otherwise unique.
    /// </summary>
    private static string MakeUniquePath(string relativePath, HashSet<string> usedPaths, bool reserveOnly = false)
    {
        if (reserveOnly)
        {
            usedPaths.Add(relativePath);
            return relativePath;
        }

        if (usedPaths.Add(relativePath))
        {
            return relativePath;
        }

        var directory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(relativePath);
        var extension = Path.GetExtension(relativePath);

        for (var suffix = 2; ; suffix++)
        {
            var candidate = string.IsNullOrEmpty(directory)
                ? $"{fileNameWithoutExtension}~{suffix}{extension}"
                : $"{directory}/{fileNameWithoutExtension}~{suffix}{extension}";
            if (usedPaths.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
