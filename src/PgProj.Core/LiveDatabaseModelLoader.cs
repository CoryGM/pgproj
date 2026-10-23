using System.Text;
using Npgsql;
using NpgsqlTypes;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core;

public sealed partial class LiveDatabaseModelLoader
{
    private readonly PgQueryParser _parser;
    private readonly SemanticBuilder _semanticBuilder;

    public LiveDatabaseModelLoader(PgQueryParser parser, SemanticBuilder semanticBuilder)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _semanticBuilder = semanticBuilder ?? throw new ArgumentNullException(nameof(semanticBuilder));
    }

    /// <summary>
    /// Reads a live database into a <see cref="DatabaseModel"/> for <c>diff</c>/<c>publish</c> to compare against a
    /// project's own model. <paramref name="scopeSchemas"/> is the project's <c>pgproj.json</c> <c>schemas</c> list,
    /// widened by the caller to also include every schema the project's own SQL actually declares an object in
    /// (see <c>CliContext.GetLiveScanSchemas</c>) — never narrowed here. When empty (the project never populates
    /// <c>schemas</c>, or passes none), every schema the database exposes is read, exactly as before G-85. When
    /// non-empty, every catalog query is scoped to just those schemas, so an object belonging to a schema another
    /// project owns is never read at all: not modeled, not diffed, never a candidate for removal.
    /// <paramref name="excludeGrants"/>/<paramref name="excludeRoleMemberships"/> (G-87) still read the live grant
    /// catalog (no query changes), but drop the excluded category before it becomes a <see cref="GrantDefinition"/>
    /// — the same <c>GrantDeclarations.Declare</c> gate the project side uses, reached here because this method
    /// re-parses a synthesized snapshot through the same <see cref="SemanticBuilder.Build"/> call. Passing the same
    /// resolved values the caller used for the project model is required: excluding on only one side would make
    /// every live grant look like a genuine removal (see <c>CliContext.LoadProjectModel</c>).
    /// </summary>
    public async Task<DatabaseModel> LoadAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? scopeSchemas = null,
        bool excludeGrants = false,
        bool excludeRoleMemberships = false,
        bool excludeOwners = false,
        IReadOnlyCollection<string>? scopeExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var schemaFilter = scopeSchemas ?? [];
        var previousSearchPath = await LiveReadSearchPath.PinAsync(connection, null, cancellationToken);
        string? previousJit = null;
        try
        {
            previousJit = await LiveReadJit.PinAsync(connection, cancellationToken);
            var schemas = await ReadSchemasAsync(connection, schemaFilter, cancellationToken);
            var types = await ReadTypesAsync(connection, schemaFilter, cancellationToken, scopeExtensions);
            var (sequences, tables) = AttachIdentitySequences(
                await ReadSequencesAsync(connection, schemaFilter, cancellationToken),
                await ReadTablesAsync(connection, schemaFilter, cancellationToken));
            var constraints = await ReadConstraintsAsync(connection, schemaFilter, cancellationToken);
            var indexes = await ReadIndexesAsync(connection, schemaFilter, cancellationToken);
            var views = await ReadViewsAsync(connection, schemaFilter, cancellationToken);
            var functions = await ReadFunctionsAsync(connection, schemaFilter, cancellationToken);
            var triggers = await ReadTriggersAsync(connection, schemaFilter, cancellationToken);
            var policies = await ReadPoliciesAsync(connection, schemaFilter, cancellationToken);
            var eventTriggers = await ReadEventTriggersAsync(connection, schemaFilter, cancellationToken);
            var casts = await ReadCastsAsync(connection, schemaFilter, cancellationToken);
            var operators = await ReadOperatorsAsync(connection, schemaFilter, cancellationToken);
            var rules = await ReadRulesAsync(connection, schemaFilter, cancellationToken);
            var aggregates = await ReadAggregatesAsync(connection, schemaFilter, cancellationToken);
            var collations = await ReadCollationsAsync(connection, schemaFilter, cancellationToken);
            var statistics = await ReadStatisticsAsync(connection, schemaFilter, cancellationToken);
            var textSearch = await ReadTextSearchAsync(connection, schemaFilter, cancellationToken);
            var publications = await ReadPublicationsAsync(connection, schemaFilter, cancellationToken);
            var grants = await ReadGrantsAsync(connection, schemaFilter, cancellationToken);

            // The owner of every object read above, unless the project leaves owners out (G-146); it is the same read on both sides of a diff, as grants are.
            var owners = excludeOwners
                ? []
                : BuildOwnerSnapshots(await ReadOwnerRowsAsync(connection, cancellationToken), schemas, tables, views, sequences, functions, types, operators, aggregates, eventTriggers, publications, collations, statistics, textSearch);

            var snapshotSql = GenerateSnapshotSql(schemas, tables, constraints, indexes, views, functions, types, grants, sequences, triggers, policies, eventTriggers, casts, operators, rules, aggregates, publications, owners, collations, statistics, textSearch);
            var model = BuildModelFromSnapshotSql(
                snapshotSql,
                excludeGrants,
                excludeRoleMemberships,
                () => BuildScaffoldFiles(schemas, tables, constraints, indexes, views, functions, types, grants, sequences, triggers, policies, eventTriggers, casts, operators, rules, aggregates, publications, owners, collations, statistics, textSearch),
                excludeOwners);
            if (excludeGrants)
            {
                model.UnmodeledPrivileges = ToUnmodeledPrivileges(grants);
            }

            // What depends on what, and who owns each view, so a view in the way of a column change can be dropped and recreated (G-103).
            model.LiveDependencies = [.. await ReadDependenciesAsync(connection, cancellationToken), .. await ReadRoutineDependenciesAsync(connection, cancellationToken)];
            model.LiveOwners = await ReadViewOwnersAsync(connection, cancellationToken);
            model.Types = WithLiveExtensionFacts(model.Types, types);
            model.Views = ServerViewRenderer.WithOutputColumns(model.Views, await ReadViewOutputColumnsAsync(connection, cancellationToken));

            ServerViewRenderer.AttachLiveRenderings(model, views);
            ServerPartitionBoundRenderer.AttachLiveRenderings(model, tables);
            ServerDefaultRenderer.AttachLiveRenderings(model, tables);
            ServerGeneratedExpressionRenderer.AttachLiveRenderings(model, tables);
            ServerIndexPredicateRenderer.AttachLiveRenderings(model, indexes);
            AttachInvalidIndexes(model, indexes);
            ServerCheckConstraintRenderer.AttachLiveRenderings(model, constraints);
            ServerRuleRenderer.AttachLiveRenderings(model, rules);
            ServerPolicyRenderer.AttachLiveRenderings(model, policies);
            ServerRoutineBodyRenderer.AttachLiveRenderings(model);

            await using var roleCommand = new NpgsqlCommand("select current_user", connection);
            model.ConnectedRole = (string?)await roleCommand.ExecuteScalarAsync(cancellationToken);

            // A table in the database's default tablespace is stored with none of its own (G-153), so the project's explicit clause naming it is read as unset.
            await using var tablespaceCommand = new NpgsqlCommand(
                "select ts.spcname from pg_catalog.pg_database d inner join pg_catalog.pg_tablespace ts on ts.oid = d.dattablespace where d.datname = current_database()",
                connection);
            model.DefaultTablespaceName = (string?)await tablespaceCommand.ExecuteScalarAsync(cancellationToken);
            return model;
        }
        finally
        {
            await LiveReadJit.RestoreAsync(connection, previousJit, CancellationToken.None);
            await LiveReadSearchPath.RestoreAsync(connection, null, previousSearchPath, CancellationToken.None);
        }
    }

    /// <summary>
    /// A parameter for a query's <c>and (@schemas is null or &lt;column&gt; = any(@schemas))</c> clause: <see
    /// langword="null"/> (no restriction, matching every row) when <paramref name="scopeSchemas"/> is empty,
    /// otherwise the schema list itself.
    /// </summary>
    private static NpgsqlParameter CreateSchemasParameter(IReadOnlyList<string> scopeSchemas)
        => new("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            // The NpgsqlDbType must be explicit: a bare DBNull.Value gives PostgreSQL no type to plan "@schemas is
            // null or ... = any(@schemas)" against (42P08).
            Value = scopeSchemas.Count > 0 ? scopeSchemas.ToArray() : (object)DBNull.Value,
        };

    /// <summary>A command for <paramref name="sql"/> with its <c>@schemas</c> parameter already attached (see <see cref="CreateSchemasParameter"/>).</summary>
    private static NpgsqlCommand CreateCommand(string sql, NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(CreateSchemasParameter(scopeSchemas));
        return command;
    }

    /// <summary>
    /// The object privileges among <paramref name="grants"/> (not schema grants, default privileges, or role memberships), for a model that
    /// leaves grants out (G-116). A snapshot's key is <c>kind:schema.name(arguments)</c> for one object and
    /// <c>all kinds in schema:schema</c> for a whole schema.
    /// </summary>
    internal static IReadOnlyList<UnmodeledPrivilege> ToUnmodeledPrivileges(IReadOnlyList<LiveGrantSnapshot> grants)
    {
        var privileges = new List<UnmodeledPrivilege>();
        foreach (var grant in grants)
        {
            var separator = grant.ObjectKey.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var kind = grant.ObjectKey[..separator];
            var target = grant.ObjectKey[(separator + 1)..];
            if (kind.StartsWith("all ", StringComparison.Ordinal) && kind.EndsWith(" in schema", StringComparison.Ordinal))
            {
                var plural = kind["all ".Length..^" in schema".Length];
                privileges.Add(new UnmodeledPrivilege(plural.TrimEnd('s'), grant.SchemaName, null, grant.Statement));
            }
            else if (kind is "table" or "sequence" or "function" or "procedure" && target.StartsWith(grant.SchemaName + ".", StringComparison.Ordinal))
            {
                var name = target[(grant.SchemaName.Length + 1)..];
                var arguments = name.IndexOf('(', StringComparison.Ordinal);
                privileges.Add(new UnmodeledPrivilege(kind, grant.SchemaName, arguments < 0 ? name : name[..arguments], grant.Statement));
            }
        }

        return privileges;
    }

    /// <summary>
    /// Parses the snapshot into the live model. A snapshot that does not parse throws (G-121): the parser returns no nodes at all for
    /// text with a syntax error, so continuing would read the database as empty. <paramref name="objectFiles"/>, when given, is the same
    /// read as one file per object, parsed one by one on that path only, to name the objects to blame.
    /// </summary>
    internal DatabaseModel BuildModelFromSnapshotSql(
        string snapshotSql,
        bool excludeGrants = false,
        bool excludeRoleMemberships = false,
        Func<IReadOnlyList<ScaffoldedFile>>? objectFiles = null,
        bool excludeOwners = false)
    {
        if (string.IsNullOrWhiteSpace(snapshotSql))
        {
            return DatabaseModel.Empty;
        }

        var syntaxTree = _parser.Parse(snapshotSql);
        if (syntaxTree.HasErrors)
        {
            var errors = syntaxTree.Diagnostics.Where(static diagnostic => diagnostic.Severity == SqlDiagnosticSeverity.Error).ToArray();
            throw CreateUnparseableException("read", objectFiles is null ? [] : FindUnparseableFiles(objectFiles()), errors);
        }

        var model = _semanticBuilder.Build([syntaxTree], excludeGrants: excludeGrants, excludeRoleMemberships: excludeRoleMemberships, excludeOwners: excludeOwners);

        // An owner statement of the snapshot always names an object the snapshot declares; a warning from resolving one would only be noise on the live side (G-146).
        model.Diagnostics = model.Diagnostics.Where(static diagnostic => diagnostic.Source != "OwnerDeclarations").ToArray();
        return model;
    }

    internal static string GenerateSnapshotSql(
        IReadOnlyList<LiveSchemaSnapshot> schemas,
        IReadOnlyList<LiveTableSnapshot> tables,
        IReadOnlyList<LiveConstraintSnapshot> constraints,
        IReadOnlyList<LiveIndexSnapshot> indexes,
        IReadOnlyList<LiveViewSnapshot>? views = null,
        IReadOnlyList<LiveFunctionSnapshot>? functions = null,
        IReadOnlyList<LiveTypeSnapshot>? types = null,
        IReadOnlyList<LiveGrantSnapshot>? grants = null,
        IReadOnlyList<LiveSequenceSnapshot>? sequences = null,
        IReadOnlyList<LiveTriggerSnapshot>? triggers = null,
        IReadOnlyList<LivePolicySnapshot>? policies = null,
        IReadOnlyList<LiveEventTriggerSnapshot>? eventTriggers = null,
        IReadOnlyList<LiveCastSnapshot>? casts = null,
        IReadOnlyList<LiveOperatorSnapshot>? operators = null,
        IReadOnlyList<LiveRuleSnapshot>? rules = null,
        IReadOnlyList<LiveAggregateSnapshot>? aggregates = null,
        IReadOnlyList<LivePublicationSnapshot>? publications = null,
        IReadOnlyList<LiveOwnerSnapshot>? owners = null,
        IReadOnlyList<LiveCollationSnapshot>? collations = null,
        IReadOnlyList<LiveStatisticsSnapshot>? statistics = null,
        LiveTextSearchSnapshot? textSearch = null)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentNullException.ThrowIfNull(indexes);
        views ??= [];
        functions ??= [];
        types ??= [];
        grants ??= [];
        sequences ??= [];
        triggers ??= [];
        policies ??= [];
        eventTriggers ??= [];
        casts ??= [];
        operators ??= [];
        rules ??= [];
        aggregates ??= [];
        publications ??= [];
        owners ??= [];
        collations ??= [];
        statistics ??= [];
        textSearch ??= LiveTextSearchSnapshot.Empty;

        var builder = new StringBuilder();

        foreach (var schema in schemas
            .Where(static schema => !string.Equals(schema.SchemaName, "public", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static schema => schema.SchemaName, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("CREATE SCHEMA IF NOT EXISTS ")
                .Append(RenderIdentifier(schema.SchemaName))
                .AppendLine(";");
        }

        // A schema's comment, as the statement that states it (G-160); the schemas it is stated for are the ones the model gets a definition for.
        foreach (var schema in schemas.Where(static schema => schema.StatedComment is not null).OrderBy(static schema => schema.SchemaName, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("COMMENT ON SCHEMA ").Append(RenderIdentifier(schema.SchemaName)).Append(" IS ").Append(QuoteLiteral(schema.StatedComment!)).AppendLine(";");
        }

        if (schemas.Count > 0)
        {
            builder.AppendLine();
        }

        // An extension is written with its version, and with the comment it has when that is not the one it comes with (G-213), after those it requires.
        foreach (var extension in LiveTypeSnapshot.OrderExtensions(types.Where(static type => type.IsExtension)))
        {
            builder.Append(extension.Statement.Trim().TrimEnd(';'));
            if (extension.ExtensionVersion is { } version)
            {
                builder.Append(" VERSION ").Append(QuoteLiteral(version));
            }

            builder.AppendLine(";");
            if (extension.ExtensionCommentStatement is { } extensionComment)
            {
                builder.AppendLine(extensionComment);
            }
        }

        foreach (var type in types
            .Where(static type => !type.IsExtension)
            .OrderBy(static type => type.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static type => type.TypeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static type => type.TypeKind, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(type.Statement.Trim().TrimEnd(';')).AppendLine(";");

            if (!string.IsNullOrEmpty(type.Comment))
            {
                builder
                    .Append("COMMENT ON TYPE ")
                    .Append(QualifyIdentifier(type.SchemaName, type.TypeName))
                    .Append(" IS ")
                    .Append(QuoteLiteral(type.Comment))
                    .AppendLine(";");
            }

            foreach (var columnComment in type.ColumnCommentStatements)
            {
                builder.Append(columnComment).AppendLine(";");
            }
        }

        if (types.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var sequence in sequences
            .OrderBy(static sequence => sequence.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static sequence => sequence.SequenceName, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(BuildCreateSequenceStatement(sequence)).AppendLine();
        }

        if (sequences.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var table in tables
            .OrderBy(static table => table.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static table => table.TableName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(BuildCreateTableStatement(table));
            builder.AppendLine();
        }

        foreach (var constraint in constraints
            .OrderBy(static constraint => constraint.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static constraint => constraint.TableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static constraint => constraint.ConstraintName, StringComparer.OrdinalIgnoreCase))
        {
            builder
                .Append("ALTER TABLE ")
                .Append(QualifyIdentifier(constraint.SchemaName, constraint.TableName))
                .Append(" ADD CONSTRAINT ")
                .Append(QuoteIdentifier(constraint.ConstraintName))
                .Append(' ')
                .Append(WithIndexOptions(constraint.ConstraintDefinition.Trim().TrimEnd(';'), constraint.IndexOptions))
                .AppendLine(";");
            builder.Append(IndexFlagStatements(constraint.SchemaName, constraint.TableName, constraint.ConstraintName, constraint.IsClustered, constraint.IsReplicaIdentity));

            // A constraint's comment, stated for the constraints that have one (G-161).
            if (!string.IsNullOrEmpty(constraint.Comment))
            {
                builder
                    .Append("COMMENT ON CONSTRAINT ")
                    .Append(QuoteIdentifier(constraint.ConstraintName))
                    .Append(" ON ")
                    .Append(QualifyIdentifier(constraint.SchemaName, constraint.TableName))
                    .Append(" IS ")
                    .Append(QuoteLiteral(constraint.Comment))
                    .AppendLine(";");
            }

            if (BuildConstraintIndexCommentStatement(constraint) is { Length: > 0 } indexComment)
            {
                builder.AppendLine(indexComment);
            }
        }

        if (constraints.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var index in indexes
            .OrderBy(static index => index.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static index => index.TableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static index => index.IndexName, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(index.IndexDefinition.Trim().TrimEnd(';')).AppendLine(";");
            builder.Append(IndexFlagStatements(index.SchemaName, index.TableName, index.IndexName, index.IsClustered, index.IsReplicaIdentity));
            if (!string.IsNullOrEmpty(index.Comment))
            {
                builder
                    .Append("COMMENT ON INDEX ")
                    .Append(QualifyIdentifier(index.SchemaName, index.IndexName))
                    .Append(" IS ")
                    .Append(QuoteLiteral(index.Comment))
                    .AppendLine(";");
            }
        }

        if (indexes.Count > 0 && (views.Count > 0 || functions.Count > 0))
        {
            builder.AppendLine();
        }

        foreach (var view in views
            .OrderBy(static view => view.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static view => view.ViewName, StringComparer.OrdinalIgnoreCase))
        {
            var createKeyword = view.IsMaterialized
                ? "CREATE MATERIALIZED VIEW "
                : "CREATE OR REPLACE VIEW ";
            builder
                .Append(view.PlaceholderStatement)
                .Append(createKeyword)
                .Append(QualifyIdentifier(view.SchemaName, view.ViewName))
                .Append(view.HeaderClause(scaffold: false))
                .AppendLine(" AS")
                .Append(view.ScriptedQuery)
                .AppendLine(";");

            if (!string.IsNullOrEmpty(view.Comment))
            {
                var viewKeyword = view.IsMaterialized ? "MATERIALIZED VIEW" : "VIEW";
                builder
                    .Append("COMMENT ON ").Append(viewKeyword).Append(' ')
                    .Append(QualifyIdentifier(view.SchemaName, view.ViewName))
                    .Append(" IS ")
                    .Append(QuoteLiteral(view.Comment))
                    .AppendLine(";");
            }

            foreach (var columnComment in view.ColumnCommentStatements)
            {
                builder.Append(columnComment).AppendLine(";");
            }

            foreach (var columnDefault in view.ColumnDefaultStatements)
            {
                builder.Append(columnDefault).AppendLine(";");
            }

            builder.AppendLine();
        }

        foreach (var function in functions
            .OrderBy(static function => function.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static function => function.FunctionName, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(function.FunctionDefinition.Trim().TrimEnd(';')).AppendLine(";");

            if (!string.IsNullOrEmpty(function.Comment))
            {
                var routineKeyword = function.IsProcedure ? "PROCEDURE" : "FUNCTION";
                builder
                    .Append("COMMENT ON ").Append(routineKeyword).Append(' ')
                    .Append(QualifyIdentifier(function.SchemaName, function.FunctionName))
                    .Append('(').Append(function.IdentityArguments).Append(')')
                    .Append(" IS ")
                    .Append(QuoteLiteral(function.Comment))
                    .AppendLine(";");
            }

            builder.AppendLine();
        }

        foreach (var trigger in triggers
            .OrderBy(static trigger => trigger.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static trigger => trigger.TableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static trigger => trigger.TriggerName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(BuildCreateTriggerStatement(trigger));
            if (!string.IsNullOrEmpty(trigger.Comment))
            {
                builder.AppendLine(BuildTriggerCommentStatement(trigger));
            }
        }

        foreach (var table in tables.Where(static table => table.InternalTriggerState is not null))
        {
            builder.Append(InternalTriggerStatements(table, triggers));
        }

        if (triggers.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var policy in policies
            .OrderBy(static policy => policy.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static policy => policy.TableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static policy => policy.PolicyName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(BuildCreatePolicyStatement(policy));
            if (!string.IsNullOrEmpty(policy.Comment))
            {
                builder.AppendLine(BuildPolicyCommentStatement(policy));
            }
        }

        if (policies.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var eventTrigger in eventTriggers.OrderBy(static eventTrigger => eventTrigger.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(BuildCreateEventTriggerStatement(eventTrigger));
        }

        if (eventTriggers.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var cast in casts.OrderBy(static cast => cast.SourceType, StringComparer.OrdinalIgnoreCase).ThenBy(static cast => cast.TargetType, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(BuildCreateCastStatement(cast));
        }

        if (casts.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var operatorSnapshot in operators.OrderBy(static o => o.OperatorSchema, StringComparer.OrdinalIgnoreCase).ThenBy(static o => o.OperatorName, StringComparer.Ordinal).ThenBy(static o => o.LeftType, StringComparer.OrdinalIgnoreCase).ThenBy(static o => o.RightType, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(BuildCreateOperatorStatement(operatorSnapshot));
        }

        if (operators.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var rule in rules.OrderBy(static rule => rule.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static rule => rule.TableName, StringComparer.OrdinalIgnoreCase).ThenBy(static rule => rule.RuleName, StringComparer.Ordinal))
        {
            builder.AppendLine(BuildCreateRuleStatement(rule));
        }

        if (rules.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var collation in collations.OrderBy(static collation => collation.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static collation => collation.Name, StringComparer.Ordinal))
        {
            builder.AppendLine(BuildCreateCollationStatement(collation));
            if (collation.Comment is not null)
            {
                builder.AppendLine(BuildObjectCommentStatement("COLLATION", collation.SchemaName, collation.Name, collation.Comment));
            }
        }

        if (collations.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var statement in BuildTextSearchStatements(textSearch))
        {
            builder.AppendLine(statement);
        }

        if (textSearch.Dictionaries.Count + textSearch.Configurations.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var item in statistics.OrderBy(static item => item.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.Name, StringComparer.Ordinal))
        {
            builder.AppendLine(item.Definition.TrimEnd().TrimEnd(';') + ";");
            if (item.Comment is not null)
            {
                builder.AppendLine(BuildObjectCommentStatement("STATISTICS", item.SchemaName, item.Name, item.Comment));
            }
        }

        if (statistics.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var aggregate in aggregates.OrderBy(static aggregate => aggregate.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static aggregate => aggregate.Name, StringComparer.Ordinal).ThenBy(static aggregate => aggregate.Arguments, StringComparer.Ordinal))
        {
            builder.AppendLine(BuildCreateAggregateStatement(aggregate));
        }

        if (aggregates.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var publication in publications.OrderBy(static publication => publication.Name, StringComparer.Ordinal))
        {
            builder.AppendLine(BuildCreatePublicationStatement(publication));
        }

        if (publications.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var grant in grants
            .OrderBy(static grant => grant.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static grant => grant.ObjectKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static grant => grant.Grantee, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static grant => grant.Statement, StringComparer.Ordinal))
        {
            builder.Append(grant.Statement.Trim().TrimEnd(';')).AppendLine(";");
        }

        foreach (var owner in owners)
        {
            builder.AppendLine(owner.Statement);
        }

        return builder.ToString();
    }

    private static async Task<IReadOnlyList<LiveSchemaSnapshot>> ReadSchemasAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string schemaSql =
            """
            select ns.nspname as schema_name,
                   pg_catalog.obj_description(ns.oid, 'pg_namespace') as comment
            from pg_catalog.pg_namespace ns
            where ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and ns.nspname not like 'pg_temp_%'
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname;
            """;

        var schemas = new List<LiveSchemaSnapshot>();
        await using var command = CreateCommand(schemaSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            schemas.Add(new LiveSchemaSnapshot(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return schemas;
    }

    /// <summary>
    /// An identity's sequence belongs to its column (G-214): its options are written on the column, and it is not written as a sequence of its own,
    /// which made a project with both an identity column and a <c>CREATE SEQUENCE ... OWNED BY</c> that column.
    /// </summary>
    internal static (IReadOnlyList<LiveSequenceSnapshot> Sequences, IReadOnlyList<LiveTableSnapshot> Tables) AttachIdentitySequences(
        IReadOnlyList<LiveSequenceSnapshot> sequences,
        IReadOnlyList<LiveTableSnapshot> tables)
    {
        var identities = sequences
            .Where(static sequence => sequence.IsIdentity && sequence.OwnedBySchema is not null && sequence.OwnedByTable is not null && sequence.OwnedByColumn is not null)
            .GroupBy(static sequence => (sequence.OwnedBySchema!, sequence.OwnedByTable!, sequence.OwnedByColumn!))
            .ToDictionary(static group => group.Key, static group => group.First());
        if (identities.Count == 0)
        {
            return (sequences, tables);
        }

        var attached = tables
            .Select(table => table with
            {
                Columns = [.. table.Columns.Select(column => identities.TryGetValue((table.SchemaName, table.TableName, column.ColumnName), out var sequence)
                    ? column with { IdentityOptions = sequence.IdentityOptionsTextFor(tableIsUnlogged: table.IsUnlogged) }
                    : column)],
            })
            .ToArray();
        return ([.. sequences.Where(static sequence => !sequence.IsIdentity)], attached);
    }

    private static async Task<IReadOnlyList<LiveSequenceSnapshot>> ReadSequencesAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string sequenceSql =
            """
            select seq.schemaname,
                   seq.sequencename,
                   seq.data_type::text,
                   seq.start_value,
                   seq.min_value,
                   seq.max_value,
                   seq.increment_by,
                   seq.cycle,
                   seq.cache_size,
                   owner_ns.nspname as owned_by_schema,
                   owner_cls.relname as owned_by_table,
                   owner_att.attname as owned_by_column,
                   pg_catalog.obj_description(cls.oid, 'pg_class') as sequence_comment,
                   coalesce(dep.deptype = 'i', false) as is_identity,
                   cls.relpersistence = 'u' as is_unlogged
            from pg_catalog.pg_sequences seq
            inner join pg_catalog.pg_class cls
                on cls.relname = seq.sequencename
               and cls.relnamespace = (select oid from pg_catalog.pg_namespace where nspname = seq.schemaname)
            left join pg_catalog.pg_depend dep
                on dep.objid = cls.oid
               and dep.deptype in ('a', 'i')
            left join pg_catalog.pg_class owner_cls on owner_cls.oid = dep.refobjid
            left join pg_catalog.pg_namespace owner_ns on owner_ns.oid = owner_cls.relnamespace
            left join pg_catalog.pg_attribute owner_att
                on owner_att.attrelid = dep.refobjid
               and owner_att.attnum = dep.refobjsubid
            where seq.schemaname not in ('pg_catalog', 'information_schema')
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = cls.oid and ext_dep.deptype = 'e')
              and seq.schemaname not like 'pg_toast%'
              and (@schemas is null or seq.schemaname = any(@schemas))
            order by seq.schemaname, seq.sequencename;
            """;

        var sequences = new List<LiveSequenceSnapshot>();
        await using var command = CreateCommand(sequenceSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sequences.Add(new LiveSequenceSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3).ToString(),
                reader.GetInt64(4).ToString(),
                reader.GetInt64(5).ToString(),
                reader.GetInt64(6).ToString(),
                reader.GetBoolean(7),
                reader.GetInt64(8).ToString(),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12))
            {
                IsIdentity = reader.GetBoolean(13),
                IsUnlogged = reader.GetBoolean(14),
            });
        }

        return sequences;
    }

    private static string BuildCreateSequenceStatement(LiveSequenceSnapshot sequence)
    {
        var builder = new StringBuilder()
            .Append(sequence.IsUnlogged ? "CREATE UNLOGGED SEQUENCE " : "CREATE SEQUENCE ")
            .Append(QualifyIdentifier(sequence.SchemaName, sequence.SequenceName));

        if (!string.IsNullOrWhiteSpace(sequence.DataType))
        {
            builder.Append(" AS ").Append(sequence.DataType);
        }

        builder
            .Append(" INCREMENT BY ").Append(sequence.IncrementBy)
            .Append(" MINVALUE ").Append(sequence.MinValue)
            .Append(" MAXVALUE ").Append(sequence.MaxValue)
            .Append(" START WITH ").Append(sequence.StartValue)
            .Append(" CACHE ").Append(sequence.CacheSize);

        if (sequence.Cycle)
        {
            builder.Append(" CYCLE");
        }

        if (!string.IsNullOrWhiteSpace(sequence.OwnedBySchema)
            && !string.IsNullOrWhiteSpace(sequence.OwnedByTable)
            && !string.IsNullOrWhiteSpace(sequence.OwnedByColumn))
        {
            builder
                .Append(" OWNED BY ")
                .Append(QualifyIdentifier(sequence.OwnedBySchema, sequence.OwnedByTable))
                .Append('.')
                .Append(RenderIdentifier(sequence.OwnedByColumn));
        }

        builder.Append(';');

        if (!string.IsNullOrEmpty(sequence.Comment))
        {
            builder
                .Append(Environment.NewLine)
                .Append("COMMENT ON SEQUENCE ")
                .Append(QualifyIdentifier(sequence.SchemaName, sequence.SequenceName))
                .Append(" IS ")
                .Append(QuoteLiteral(sequence.Comment))
                .Append(';');
        }

        return builder.ToString();
    }

    /// <summary>
    /// A live extension's requirements are the ones the database records, not those read before it, so that one is kept only while another needs it
    /// (G-213). Its comment is the one it has, the default included, so a project that states the default sees no change; it counts as stated only when it
    /// is not the default, so a database compared against a project that states none sees none either.
    /// </summary>
    internal static IReadOnlyDictionary<string, TypeDefinition> WithLiveExtensionFacts(IReadOnlyDictionary<string, TypeDefinition> types, IReadOnlyList<LiveTypeSnapshot> snapshots)
    {
        var result = new Dictionary<string, TypeDefinition>(types, PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var snapshot in snapshots.Where(static snapshot => snapshot.IsExtension))
        {
            var key = ExtensionKeys.Create(snapshot.TypeName);
            if (result.TryGetValue(key, out var extension))
            {
                result[key] = extension with
                {
                    ExtensionRequires = [.. snapshot.Requires.Select(ExtensionKeys.Create)],
                    Comment = string.IsNullOrEmpty(snapshot.Comment) ? null : snapshot.Comment,
                    CommentStated = snapshot.IsCommentChanged,
                };
            }
        }

        return result;
    }

    /// <param name="scopeExtensions">
    /// The extensions the project names (G-213), read wherever they are when the read is scoped: an extension is named by its name alone, and one written
    /// without <c>SCHEMA</c> brings no schema into the scope.
    /// </param>
    private static async Task<IReadOnlyList<LiveTypeSnapshot>> ReadTypesAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> scopeSchemas,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? scopeExtensions = null)
    {
        var builtInExtensions = string.Join(", ", BuiltInExtensions.Names.Select(static name => $"'{name}'"));
        var namedExtensions = scopeExtensions is { Count: > 0 }
            ? " or ext.extname in (" + string.Join(", ", scopeExtensions.Select(QuoteLiteral)) + ")"
            : string.Empty;
        var extensionSql =
            $"""
            select ns.nspname as schema_name,
                   ext.extname as type_name,
                   'extension' as type_kind,
                   format('CREATE EXTENSION %I SCHEMA %I', ext.extname, ns.nspname) as statement,
                   pg_catalog.obj_description(ext.oid, 'pg_extension') as comment,
                   ext.extversion as extension_version,
                   (select av.comment from pg_catalog.pg_available_extensions av where av.name = ext.extname) as default_comment,
                   array(select required.extname
                         from pg_catalog.pg_depend dep
                         inner join pg_catalog.pg_extension required on required.oid = dep.refobjid
                         where dep.classid = 'pg_catalog.pg_extension'::regclass
                           and dep.objid = ext.oid
                           and dep.refclassid = 'pg_catalog.pg_extension'::regclass
                         order by required.extname) as requires
            from pg_catalog.pg_extension ext
            inner join pg_catalog.pg_namespace ns on ns.oid = ext.extnamespace
            where ns.nspname not like 'pg_toast%'
              and ext.extname not in ({builtInExtensions})
              and (@schemas is null or ns.nspname = any(@schemas){namedExtensions})
            order by ns.nspname, ext.extname;
            """;

        // A domain's DEFAULT/NOT NULL/CHECK constraints (G-77): the default is rendered by pg_get_expr over typ.typdefaultbin (G-244; typ.typdefault is
        // the text as it was written, which keeps a function's schema out when the creating session had it on its search path); typ.typnotnull is NOT NULL. A CHECK is a pg_constraint row with contypid = the domain's own
        // oid and contype = 'c' - a domain's NOT NULL is ALSO a pg_constraint row (contype = 'n', confirmed against
        // a real server), which the 'c' filter excludes so it is never double-counted as a CHECK.
        // pg_get_constraintdef already renders "CHECK (...)", so it is not re-wrapped.
        // A CHECK that is NOT VALID (convalidated = false) cannot be written in CREATE DOMAIN, so it follows as ALTER DOMAIN ... ADD CONSTRAINT ... NOT VALID,
        // which pg_get_constraintdef renders in full (G-233).
        const string domainSql =
            """
            select ns.nspname as schema_name,
                   typ.typname as type_name,
                   'domain' as type_kind,
                   format('CREATE DOMAIN %I.%I AS %s%s%s%s%s',
                       ns.nspname,
                       typ.typname,
                       pg_catalog.format_type(typ.typbasetype, typ.typtypmod),
                       -- The domain's collation only when it is not its base type's own (G-196), as for a table column.
                       (select format(' COLLATE %I.%I', coll_ns.nspname, coll.collname)
                        from pg_catalog.pg_collation coll
                        inner join pg_catalog.pg_namespace coll_ns on coll_ns.oid = coll.collnamespace
                        inner join pg_catalog.pg_type base_typ on base_typ.oid = typ.typbasetype
                        where coll.oid = typ.typcollation and typ.typcollation <> base_typ.typcollation),
                       -- pg_get_expr renders the stored tree under the read's pinned search path, like every other expression; typdefault is the text as it was written when the domain was created, so a function in another schema lost its schema (G-244).
                       case when typ.typdefaultbin is not null then format(' DEFAULT %s', pg_catalog.pg_get_expr(typ.typdefaultbin, 0)) else '' end,
                       coalesce((select string_agg(format(' CONSTRAINT %I %s', con.conname, pg_catalog.pg_get_constraintdef(con.oid)), '' order by con.oid)
                                 from pg_catalog.pg_constraint con
                                 where con.contypid = typ.oid and con.contype = 'c' and con.convalidated), ''),
                       case when typ.typnotnull then ' NOT NULL' else '' end)
                   || coalesce((select string_agg(';' || chr(10) || format('ALTER DOMAIN %I.%I ADD CONSTRAINT %I %s', ns.nspname, typ.typname, con.conname, pg_catalog.pg_get_constraintdef(con.oid)), '' order by con.oid)
                                from pg_catalog.pg_constraint con
                                where con.contypid = typ.oid and con.contype = 'c' and not con.convalidated), '') as statement,
                   -- A domain's comment, read as an enum's is (G-302); it was left out, so diff reported the project's comment as added after every publish.
                   pg_catalog.obj_description(typ.oid, 'pg_type') as type_comment
            from pg_catalog.pg_type typ
            inner join pg_catalog.pg_namespace ns on ns.oid = typ.typnamespace
            where typ.typtype = 'd'
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_type'::regclass and ext_dep.objid = typ.oid and ext_dep.deptype = 'e')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, typ.typname;
            """;

        const string enumSql =
            """
            select ns.nspname as schema_name,
                   typ.typname as type_name,
                   'user-defined' as type_kind,
                   format('CREATE TYPE %I.%I AS ENUM (%s)',
                       ns.nspname,
                       typ.typname,
                       string_agg(quote_literal(enum.enumlabel), ', ' order by enum.enumsortorder)) as statement,
                   pg_catalog.obj_description(typ.oid, 'pg_type') as type_comment
            from pg_catalog.pg_type typ
            inner join pg_catalog.pg_namespace ns on ns.oid = typ.typnamespace
            left join pg_catalog.pg_enum enum on enum.enumtypid = typ.oid -- an enum with no labels is still an enum (G-249); format writes the missing list as empty
            where typ.typtype = 'e'
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_type'::regclass and ext_dep.objid = typ.oid and ext_dep.deptype = 'e')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            group by ns.nspname, typ.typname, typ.oid
            order by ns.nspname, typ.typname;
            """;

        const string compositeSql =
            """
            select ns.nspname as schema_name,
                   typ.typname as type_name,
                   'user-defined' as type_kind,
                   format('CREATE TYPE %I.%I AS (%s)',
                       ns.nspname,
                       typ.typname,
                       -- A type with no fields has one row with no attribute (G-268): it aggregates to nothing, AS ().
                       coalesce(string_agg(case when att.attnum is not null then format('%I %s%s',
                           att.attname,
                           pg_catalog.format_type(att.atttypid, att.atttypmod),
                           -- A field's collation only when it is not its type's own (G-192), as for a table column.
                           case when att.attcollation <> att_typ.typcollation and att.attcollation <> 0 then format(' COLLATE %I.%I', coll_ns.nspname, coll.collname) else '' end) end,
                           ', ' order by att.attnum), '')) as statement,
                   pg_catalog.obj_description(typ.oid, 'pg_type') as type_comment,
                   array_remove(array_agg(case when att.attnum is not null and pg_catalog.col_description(cls.oid, att.attnum) is not null
                       then format('COMMENT ON COLUMN %I.%I.%I IS %L', ns.nspname, typ.typname, att.attname, pg_catalog.col_description(cls.oid, att.attnum)) end
                       order by att.attnum), null) as column_comments
            from pg_catalog.pg_type typ
            inner join pg_catalog.pg_namespace ns on ns.oid = typ.typnamespace
            inner join pg_catalog.pg_class cls on cls.oid = typ.typrelid
            -- A composite type with no fields has no attribute rows; it is read too (G-268).
            left join pg_catalog.pg_attribute att on att.attrelid = cls.oid and att.attnum > 0 and not att.attisdropped
            left join pg_catalog.pg_type att_typ on att_typ.oid = att.atttypid
            left join pg_catalog.pg_collation coll on coll.oid = att.attcollation
            left join pg_catalog.pg_namespace coll_ns on coll_ns.oid = coll.collnamespace
            where typ.typtype = 'c'
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_type'::regclass and ext_dep.objid = typ.oid and ext_dep.deptype = 'e')
              and cls.relkind = 'c'
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            group by ns.nspname, typ.typname, typ.oid
            order by ns.nspname, typ.typname;
            """;

        // Every range option is read (G-115), each named with its schema, and each only when it is not what PostgreSQL chooses when the option
        // is left out: the subtype's default btree operator class, and its own collation (none when it is not collatable).
        const string rangeSql =
            """
            select ns.nspname as schema_name,
                   typ.typname as type_name,
                   'user-defined' as type_kind,
                   format('CREATE TYPE %I.%I AS RANGE (SUBTYPE = %s%s%s%s%s%s)',
                       ns.nspname,
                       typ.typname,
                       pg_catalog.format_type(rng.rngsubtype, null),
                       case when opc.opcdefault then '' else format(', SUBTYPE_OPCLASS = %I.%I', opc_ns.nspname, opc.opcname) end,
                       case when coll.oid is null or rng.rngcollation = sub.typcollation then '' else format(', COLLATION = %I.%I', coll_ns.nspname, coll.collname) end,
                       case when canonical.oid is null then '' else format(', CANONICAL = %I.%I', canonical_ns.nspname, canonical.proname) end,
                       case when subdiff.oid is null then '' else format(', SUBTYPE_DIFF = %I.%I', subdiff_ns.nspname, subdiff.proname) end,
                       -- The multirange's name only when it is not the one PostgreSQL gives by default (G-204).
                       case when multi.typnamespace = typ.typnamespace
                                 and multi.typname = case when position('range' in typ.typname) > 0
                                                          then overlay(typ.typname placing 'multirange' from position('range' in typ.typname) for 5)
                                                          else typ.typname || '_multirange' end
                            then '' else format(', MULTIRANGE_TYPE_NAME = %I.%I', multi_ns.nspname, multi.typname) end) as statement,
                   pg_catalog.obj_description(typ.oid, 'pg_type') as type_comment
            from pg_catalog.pg_type typ
            inner join pg_catalog.pg_namespace ns on ns.oid = typ.typnamespace
            inner join pg_catalog.pg_range rng on rng.rngtypid = typ.oid
            inner join pg_catalog.pg_type sub on sub.oid = rng.rngsubtype
            inner join pg_catalog.pg_opclass opc on opc.oid = rng.rngsubopc
            inner join pg_catalog.pg_namespace opc_ns on opc_ns.oid = opc.opcnamespace
            left join pg_catalog.pg_collation coll on coll.oid = rng.rngcollation
            left join pg_catalog.pg_namespace coll_ns on coll_ns.oid = coll.collnamespace
            left join pg_catalog.pg_proc canonical on canonical.oid = rng.rngcanonical
            left join pg_catalog.pg_namespace canonical_ns on canonical_ns.oid = canonical.pronamespace
            left join pg_catalog.pg_proc subdiff on subdiff.oid = rng.rngsubdiff
            left join pg_catalog.pg_namespace subdiff_ns on subdiff_ns.oid = subdiff.pronamespace
            inner join pg_catalog.pg_type multi on multi.oid = rng.rngmultitypid
            inner join pg_catalog.pg_namespace multi_ns on multi_ns.oid = multi.typnamespace
            where typ.typtype = 'r'
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_type'::regclass and ext_dep.objid = typ.oid and ext_dep.deptype = 'e')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, typ.typname;
            """;

        var types = new List<LiveTypeSnapshot>();
        await ReadTypeStatementsAsync(connection, extensionSql, scopeSchemas, types, cancellationToken);
        await ReadTypeStatementsAsync(connection, domainSql, scopeSchemas, types, cancellationToken);
        await ReadTypeStatementsAsync(connection, enumSql, scopeSchemas, types, cancellationToken);
        await ReadTypeStatementsAsync(connection, compositeSql, scopeSchemas, types, cancellationToken);
        await ReadTypeStatementsAsync(connection, rangeSql, scopeSchemas, types, cancellationToken);
        return types;
    }

    private static async Task ReadTypeStatementsAsync(
        NpgsqlConnection connection,
        string sql,
        IReadOnlyList<string> scopeSchemas,
        ICollection<LiveTypeSnapshot> types,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = new LiveTypeSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.FieldCount > 4 && !reader.IsDBNull(4) ? reader.GetString(4) : null);

            // A composite type's fields' comments (G-216).
            if (reader.FieldCount == 6 && string.Equals(reader.GetName(5), "column_comments", StringComparison.Ordinal))
            {
                type = type with { ColumnCommentStatements = reader.GetFieldValue<string[]>(5) };
            }

            // An extension's version, the comment it comes with, and the extensions it requires (G-213).
            if (reader.FieldCount > 7)
            {
                type = type with
                {
                    ExtensionVersion = reader.IsDBNull(5) ? null : reader.GetString(5),
                    DefaultComment = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Requires = reader.IsDBNull(7) ? [] : reader.GetFieldValue<string[]>(7),
                };
            }

            types.Add(type);
        }
    }

    private static string BuildCreateTableStatement(LiveTableSnapshot table, bool scaffold = false)
    {
        var unlogged = table.IsUnlogged ? "UNLOGGED " : string.Empty;

        // The database read always states the access method, which the project's is compared with when it states one; a scaffold only when it is not the
        // server's default (G-223).
        var usingClause = table.AccessMethod is null || (scaffold && table.IsDefaultAccessMethod)
            ? string.Empty
            : $"{Environment.NewLine}USING {RenderIdentifier(table.AccessMethod)}";
        var withClause = table.StorageOptions is not { Count: > 0 }
            ? string.Empty
            : $"{Environment.NewLine}WITH ({string.Join(", ", table.StorageOptions)})";
        var tablespaceClause = string.IsNullOrEmpty(table.TablespaceName)
            ? string.Empty
            : $"{Environment.NewLine}TABLESPACE {RenderIdentifier(table.TablespaceName)}";

        // A partitioned parent's own key; a sub-partitioned partition (itself further partitioned) can have one too (G-24c).
        var partitionByClause = string.IsNullOrEmpty(table.PartitionKeyDef)
            ? string.Empty
            : $"{Environment.NewLine}PARTITION BY {table.PartitionKeyDef}";

        string createTable;
        if (!string.IsNullOrEmpty(table.PartitionOfTableName) && table.Columns.Any(static column => column.PartitionNotNullDiffers || column.PartitionOrderDiffers))
        {
            // A NOT NULL constraint named otherwise than the parent's would be renamed to it by PARTITION OF, so the partition is written as it was made: with its columns, then attached (G-264).
            // A partition's column is never an identity of its own; PostgreSQL refuses to attach a table that has one.
            var partitionColumns = table.Columns.Select(column => $"    {BuildColumnDefinition(column with { IdentityGeneration = null, IsLocal = true }, scaffold, table.TableName)}");
            var attachedParent = QualifyIdentifier(table.PartitionOfSchemaName, table.PartitionOfTableName);
            createTable = $"CREATE {unlogged}TABLE {QualifyIdentifier(table.SchemaName, table.TableName)} ({Environment.NewLine}{string.Join($",{Environment.NewLine}", partitionColumns)}{Environment.NewLine}){partitionByClause}{usingClause}{withClause}{tablespaceClause};"
                + $"{Environment.NewLine}ALTER TABLE {attachedParent} ATTACH PARTITION {QualifyIdentifier(table.SchemaName, table.TableName)} {table.PartitionBoundRaw};";
        }
        else if (!string.IsNullOrEmpty(table.PartitionOfTableName))
        {
            // A partition's own DDL never lists columns (PostgreSQL rejects one here); pg_get_expr already rendered its
            // FOR VALUES/DEFAULT bound exactly (G-24c).
            // A column it makes NOT NULL that its parent does not is in its element list (G-266); before PostgreSQL 18 nothing else says so.
            var parentName = QualifyIdentifier(table.PartitionOfSchemaName, table.PartitionOfTableName);
            var addedNotNulls = table.Columns.Where(static column => column.PartitionAddsNotNull).Select(column => $"{RenderIdentifier(column.ColumnName)} NOT NULL").ToArray();
            var elementList = addedNotNulls.Length == 0 ? string.Empty : $" ({string.Join(", ", addedNotNulls)})";
            createTable = $"CREATE {unlogged}TABLE {QualifyIdentifier(table.SchemaName, table.TableName)} PARTITION OF {parentName}{elementList} {table.PartitionBoundRaw}{partitionByClause}{usingClause}{withClause}{tablespaceClause};";
        }
        else if (!string.IsNullOrEmpty(table.OfTypeName))
        {
            // A typed table (G-136) lists only the columns it gives a default or NOT NULL, each as WITH OPTIONS; the others are its type's fields as they are.
            var typedColumns = table.Columns.Select(BuildTypedColumnOptions).OfType<string>().Select(static definition => $"    {definition}").ToList();
            var elements = typedColumns.Count == 0 ? string.Empty : $" ({Environment.NewLine}{string.Join($",{Environment.NewLine}", typedColumns)}{Environment.NewLine})";
            createTable = $"CREATE {unlogged}TABLE {QualifyIdentifier(table.SchemaName, table.TableName)} OF {QualifyIdentifier(table.OfTypeSchemaName, table.OfTypeName)}{elements}{partitionByClause}{usingClause}{withClause}{tablespaceClause};";
        }
        else
        {
            // A child of INHERITS lists only its own columns; the others are its parents' (G-194).
            var columnLines = table.Columns.Where(static column => column.IsLocal).Select(column => BuildColumnDefinition(column, scaffold, table.TableName)).Select(static definition => $"    {definition}");
            // A child's own NOT NULL constraint on a column it only inherits is a constraint among its elements (PostgreSQL 18; G-253).
            columnLines = columnLines.Concat(table.Columns
                .Where(static column => !column.IsLocal && !column.IsNullable && column.NotNullName is not null)
                .Select(column => $"    CONSTRAINT {RenderIdentifier(column.NotNullName!)} NOT NULL {RenderIdentifier(column.ColumnName)}{(column.NotNullNoInherit ? " NO INHERIT" : string.Empty)}"));
            var inheritsClause = table.InheritsFrom is { Count: > 0 } parents ? $"{Environment.NewLine}INHERITS ({string.Join(", ", parents)})" : string.Empty;
            createTable =
                $"CREATE {unlogged}TABLE {QualifyIdentifier(table.SchemaName, table.TableName)} ({Environment.NewLine}" +
                string.Join($",{Environment.NewLine}", columnLines) +
                $"{Environment.NewLine}){inheritsClause}{partitionByClause}{usingClause}{withClause}{tablespaceClause};";
        }

        var trailingStatements = new List<string>();

        // A scaffold's column storage, when it is not the type's (G-222), in the form every supported version accepts (G-269).
        if (scaffold)
        {
            trailingStatements.AddRange(table.Columns
                .Where(static column => column.Storage is not null && !column.IsDefaultStorage && column.IsLocal)
                .Select(column => $"ALTER TABLE {QualifyIdentifier(table.SchemaName, table.TableName)} ALTER COLUMN {RenderIdentifier(column.ColumnName)} SET STORAGE {column.Storage};"));
        }

        if (string.IsNullOrEmpty(table.PartitionOfTableName))
        {
            trailingStatements.AddRange(table.Columns
                .Where(static column => !column.IsNullable && column.NotValidNotNullName is not null && column.IsLocal)
                .Select(column => $"ALTER TABLE {QualifyIdentifier(table.SchemaName, table.TableName)} ADD CONSTRAINT {RenderIdentifier(column.NotValidNotNullName!)} NOT NULL {RenderIdentifier(column.ColumnName)} NOT VALID;"));
        }

        // A child's own default for a column it inherits (G-237).
        trailingStatements.AddRange(table.Columns
            .Where(static column => column.OverridesInheritedDefault)
            .Select(column => string.IsNullOrEmpty(column.DefaultExpression)
                ? $"ALTER TABLE ONLY {QualifyIdentifier(table.SchemaName, table.TableName)} ALTER COLUMN {RenderIdentifier(column.ColumnName)} DROP DEFAULT;"
                : $"ALTER TABLE ONLY {QualifyIdentifier(table.SchemaName, table.TableName)} ALTER COLUMN {RenderIdentifier(column.ColumnName)} SET DEFAULT {column.DefaultExpression};"));

        if (table.IsRowSecurityEnabled)
        {
            trailingStatements.Add($"ALTER TABLE {QualifyIdentifier(table.SchemaName, table.TableName)} ENABLE ROW LEVEL SECURITY;");
        }

        if (table.IsRowSecurityForced)
        {
            trailingStatements.Add($"ALTER TABLE {QualifyIdentifier(table.SchemaName, table.TableName)} FORCE ROW LEVEL SECURITY;");
        }

        if (!string.IsNullOrEmpty(table.Comment))
        {
            trailingStatements.Add($"COMMENT ON TABLE {QualifyIdentifier(table.SchemaName, table.TableName)} IS {QuoteLiteral(table.Comment)};");
        }

        trailingStatements.AddRange(table.Columns
            .Where(static column => !string.IsNullOrEmpty(column.Comment))
            .Select(column => $"COMMENT ON COLUMN {QualifyIdentifier(table.SchemaName, table.TableName)}.{RenderIdentifier(column.ColumnName)} IS {QuoteLiteral(column.Comment!)};"));

        // FULL and NOTHING are the table's own; USING INDEX is the index's statement (G-255).
        if (table.ReplicaIdentity is not null)
        {
            trailingStatements.Add($"ALTER TABLE {QualifyIdentifier(table.SchemaName, table.TableName)} REPLICA IDENTITY {table.ReplicaIdentity};");
        }

        // A comment on a column's NOT NULL constraint (G-235); the table's own columns only, since an inherited NOT NULL is its parent's.
        trailingStatements.AddRange(table.Columns
            .Where(static column => column is { IsLocal: true, IsNullable: false, NotNullName: not null } && !string.IsNullOrEmpty(column.NotNullComment) && column.NotValidNotNullName is null)
            .Select(column => $"COMMENT ON CONSTRAINT {RenderIdentifier(column.NotNullName!)} ON {QualifyIdentifier(table.SchemaName, table.TableName)} IS {QuoteLiteral(column.NotNullComment!)};"));

        return trailingStatements.Count == 0
            ? createTable
            : $"{createTable}{Environment.NewLine}{string.Join(Environment.NewLine, trailingStatements)}";
    }

    /// <summary>
    /// A typed table's column as <c>WITH OPTIONS</c> writes it (G-136), the same as <c>CreateOperation.BuildTypedColumnOptionsSql</c> writes a project's: what the table
    /// gives the column (<c>NOT NULL</c>, a default or generated expression, identity), its type being its field's; null when it gives it nothing.
    /// </summary>
    private static string? BuildTypedColumnOptions(LiveColumnSnapshot column)
    {
        var options = new List<string>();
        if (column.IdentityClause is { } identity)
        {
            options.Add(identity);
        }
        else if (column.IsGenerated && !string.IsNullOrWhiteSpace(column.GeneratedExpression))
        {
            options.Add($"GENERATED ALWAYS AS ({column.GeneratedExpression}){(column.IsVirtual ? " VIRTUAL" : " STORED")}");
        }
        else if (!string.IsNullOrWhiteSpace(column.DefaultExpression))
        {
            options.Add("DEFAULT " + column.DefaultExpression);
        }

        if (!column.IsNullable && column.NotValidNotNullName is null)
        {
            options.Add("NOT NULL");
        }

        return options.Count == 0 ? null : $"{RenderIdentifier(column.ColumnName)} WITH OPTIONS {string.Join(" ", options)}";
    }

    /// <summary>
    /// The statements that say a table is clustered on, or replicates by, an index or key constraint (G-255, G-256), each ending in a line break; empty when neither is so.
    /// </summary>
    /// <summary>
    /// The statements that state a table's disabled internal (foreign-key) triggers (G-261), written after its triggers: <c>DISABLE TRIGGER ALL</c> also reaches the triggers the project declares, so
    /// <c>ENABLE TRIGGER USER</c> follows when some of them are not disabled, and their own statements (<see cref="BuildCreateTriggerStatement"/>) say which are. Empty when the internal triggers are enabled.
    /// </summary>
    internal static string InternalTriggerStatements(LiveTableSnapshot table, IEnumerable<LiveTriggerSnapshot> triggers)
    {
        if (table.InternalTriggerState is null)
        {
            return string.Empty;
        }

        var qualified = QualifyIdentifier(table.SchemaName, table.TableName);
        var statements = $"ALTER TABLE {qualified} DISABLE TRIGGER ALL;{Environment.NewLine}";
        if (triggers.Any(trigger => string.Equals(trigger.SchemaName, table.SchemaName, StringComparison.Ordinal)
            && string.Equals(trigger.TableName, table.TableName, StringComparison.Ordinal)
            && !string.Equals(trigger.EnabledState, "DISABLE", StringComparison.Ordinal)))
        {
            statements += $"ALTER TABLE {qualified} ENABLE TRIGGER USER;{Environment.NewLine}";
        }

        return statements;
    }

    internal static string IndexFlagStatements(string schemaName, string tableName, string indexName, bool isClustered, bool isReplicaIdentity)
        => (isClustered ? $"ALTER TABLE {QualifyIdentifier(schemaName, tableName)} CLUSTER ON {QuoteIdentifier(indexName)};{Environment.NewLine}" : string.Empty)
            + (isReplicaIdentity ? $"ALTER TABLE {QualifyIdentifier(schemaName, tableName)} REPLICA IDENTITY USING INDEX {QuoteIdentifier(indexName)};{Environment.NewLine}" : string.Empty);

    private static string BuildColumnDefinition(LiveColumnSnapshot column, bool scaffold, string tableName)
    {
        var builder = new StringBuilder()
            .Append(RenderIdentifier(column.ColumnName))
            .Append(' ')
            .Append(column.DataType);

        // The database read always states the storage, which the project's is compared with when it states one; a scaffold only when it is not the type's
        // (G-222). The compression is written when the column has one of its own.
        // A scaffold writes it as ALTER TABLE ... SET STORAGE after the table instead (G-269): STORAGE inside CREATE TABLE needs PostgreSQL 16.
        if (column.Storage is not null && !scaffold)
        {
            builder.Append(" STORAGE ").Append(column.Storage);
        }

        if (column.Compression is not null)
        {
            builder.Append(" COMPRESSION ").Append(column.Compression);
        }

        if (!string.IsNullOrWhiteSpace(column.CollationName))
        {
            builder.Append(" COLLATE ").Append(column.CollationName);
        }

        if (column.IdentityClause is { } identity)
        {
            builder.Append(' ').Append(identity);
        }
        else if (column.IsGenerated && !string.IsNullOrWhiteSpace(column.GeneratedExpression))
        {
            builder
                .Append(" GENERATED ALWAYS AS (")
                .Append(column.GeneratedExpression)
                .Append(column.IsVirtual ? ") VIRTUAL" : ") STORED");
        }
        else if (!string.IsNullOrWhiteSpace(column.DefaultExpression))
        {
            builder.Append(" DEFAULT ").Append(column.DefaultExpression);
        }

        // A NOT VALID NOT NULL (PostgreSQL 18) is a separate ALTER TABLE after the table, since written on the column it would be valid (G-121).
        if (!column.IsNullable && column.NotValidNotNullName is null && column.PartitionNotNullName is not null)
        {
            // A partition written with its columns names every NOT NULL, so that the project states what the database has (G-264).
            builder.Append($" CONSTRAINT {RenderIdentifier(column.PartitionNotNullName)} NOT NULL");
        }
        else if (!column.IsNullable && column.NotValidNotNullName is null && !column.NotNullIsInheritedOnly)
        {
            // The constraint's name (G-235): always read for the comparison, and written in a scaffold only when it is not the one PostgreSQL gives by itself.
            var named = column.NotNullName is not null && !(scaffold && column.HasDefaultNotNullName(tableName) && column.NotNullComment is null);
            builder.Append(named ? $" CONSTRAINT {RenderIdentifier(column.NotNullName!)} NOT NULL" : " NOT NULL");
            if (column.NotNullNoInherit)
            {
                builder.Append(" NO INHERIT");
            }
        }

        return builder.ToString();
    }

    private static async Task<IReadOnlyList<LiveTableSnapshot>> ReadTablesAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string tableSql =
            """
            select ns.nspname as schema_name,
                   cls.relname as table_name,
                   pg_catalog.obj_description(cls.oid, 'pg_class') as table_comment,
                   cls.relpersistence = 'u' as is_unlogged,
                   spc.spcname as tablespace_name,
                   -- A toast. parameter lives on the TOAST table's reloptions, so it is read from there and given its prefix back (G-178).
                   pg_catalog.array_cat(cls.reloptions, (select pg_catalog.array_agg('toast.' || toast_opt) from pg_catalog.unnest(toast_cls.reloptions) as toast_opt)) as storage_options,
                   pg_catalog.pg_get_partkeydef(cls.oid) as partition_key_def,
                   case when cls.relispartition then parent_ns.nspname end as partition_of_schema,
                   case when cls.relispartition then parent_cls.relname end as partition_of_table,
                   case when cls.relispartition then pg_catalog.pg_get_expr(cls.relpartbound, cls.oid) end as partition_bound,
                   cls.relrowsecurity as row_security_enabled,
                   cls.relforcerowsecurity as row_security_forced,
                   of_ns.nspname as of_type_schema,
                   of_typ.typname as of_type_name,
                   -- The tables of INHERITS, in order (G-194); a partition's parent is read above instead.
                   (select pg_catalog.array_agg(format('%I.%I', parent_inh_ns.nspname, parent_inh_cls.relname) order by parent_inh.inhseqno)
                    from pg_catalog.pg_inherits parent_inh
                    inner join pg_catalog.pg_class parent_inh_cls on parent_inh_cls.oid = parent_inh.inhparent
                    inner join pg_catalog.pg_namespace parent_inh_ns on parent_inh_ns.oid = parent_inh_cls.relnamespace
                    where parent_inh.inhrelid = cls.oid and not cls.relispartition) as inherits_from,
                   am.amname as access_method,
                   am.amname is not distinct from pg_catalog.current_setting('default_table_access_method') as is_default_access_method,
                   case cls.relreplident when 'f' then 'FULL' when 'n' then 'NOTHING' end as replica_identity,
                   -- The firing modes of the table's internal (foreign-key) triggers (G-261).
                   (select pg_catalog.array_agg(internal_trg.tgenabled::text) from pg_catalog.pg_trigger internal_trg where internal_trg.tgrelid = cls.oid and internal_trg.tgisinternal and internal_trg.tgparentid = 0) as internal_trigger_states
            from pg_catalog.pg_class cls
            left join pg_catalog.pg_am am on am.oid = cls.relam
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            left join pg_catalog.pg_tablespace spc on spc.oid = cls.reltablespace
            left join pg_catalog.pg_class toast_cls on toast_cls.oid = cls.reltoastrelid and cls.reltoastrelid <> 0
            left join pg_catalog.pg_inherits inh on inh.inhrelid = cls.oid and cls.relispartition
            left join pg_catalog.pg_class parent_cls on parent_cls.oid = inh.inhparent
            left join pg_catalog.pg_namespace parent_ns on parent_ns.oid = parent_cls.relnamespace
            left join pg_catalog.pg_type of_typ on of_typ.oid = cls.reloftype and cls.reloftype <> 0
            left join pg_catalog.pg_namespace of_ns on of_ns.oid = of_typ.typnamespace
            where cls.relkind in ('r', 'p')
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = cls.oid and ext_dep.deptype = 'e')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, cls.relname;
            """;

        const string columnSql =
            """
            select ns.nspname as schema_name,
                   cls.relname as table_name,
                   att.attname as column_name,
                   pg_catalog.format_type(att.atttypid, att.atttypmod) as data_type,
                   not att.attnotnull as is_nullable,
                   case when att.attgenerated = '' then pg_catalog.pg_get_expr(def.adbin, def.adrelid) end as default_expression,
                   -- A child that lists a generated column it inherits is written plain when its expression is its parent's, as pg_dump writes it: before PostgreSQL 16 a child cannot repeat the expression (42611) (G-299).
                   (att.attgenerated <> '' and not (att.attinhcount > 0 and (pg_catalog.current_setting('server_version_num')::int < 160000 or parent_gen.expression is not distinct from pg_catalog.pg_get_expr(def.adbin, def.adrelid)))) as is_generated,
                   case when att.attgenerated <> '' and not (att.attinhcount > 0 and (pg_catalog.current_setting('server_version_num')::int < 160000 or parent_gen.expression is not distinct from pg_catalog.pg_get_expr(def.adbin, def.adrelid))) then pg_catalog.pg_get_expr(def.adbin, def.adrelid) end as generated_expression,
                   case
                       when att.attcollation <> typ.typcollation and att.attcollation <> 0 then format('%I.%I', coll_ns.nspname, coll.collname)
                       else null
                   end as collation_name,
                   att.attnum,
                   att.attidentity::text,
                   pg_catalog.col_description(att.attrelid, att.attnum) as column_comment,
                   att.attgenerated::text = 'v' as is_virtual,
                   (select con.conname from pg_catalog.pg_constraint con where con.conrelid = att.attrelid and con.contype = 'n' and not con.convalidated and con.conkey[1] = att.attnum limit 1) as not_valid_not_null,
                   -- A column only a parent of INHERITS gives the table is not the table's own (G-194).
                   att.attislocal as is_local,
                   -- The column's storage, and whether it is its type's; its compression, when it has one of its own (G-222).
                   case att.attstorage when 'p' then 'plain' when 'e' then 'external' when 'm' then 'main' when 'x' then 'extended' end as storage,
                   att.attstorage = typ.typstorage as is_default_storage,
                   case att.attcompression when 'p' then 'pglz' when 'l' then 'lz4' end as compression,
                   -- The column's own NOT NULL constraint, which PostgreSQL 18 stores with a name and a comment (G-235); none before 18, for a nullable column, or for one only inherited from a parent, whose name is the parent's.
                   (select con.conname from pg_catalog.pg_constraint con where con.conrelid = att.attrelid and con.contype = 'n' and con.conkey[1] = att.attnum and con.conislocal limit 1) as not_null_name,
                   (select pg_catalog.obj_description(con.oid, 'pg_constraint') from pg_catalog.pg_constraint con where con.conrelid = att.attrelid and con.contype = 'n' and con.conkey[1] = att.attnum and con.conislocal limit 1) as not_null_comment,
                   -- Whether that constraint is NO INHERIT (PostgreSQL 18; G-253).
                   coalesce((select con.connoinherit from pg_catalog.pg_constraint con where con.conrelid = att.attrelid and con.contype = 'n' and con.conkey[1] = att.attnum and con.conislocal limit 1), false) as not_null_no_inherit,
                   -- A NOT NULL the column only inherits (PostgreSQL 18): its constraint is a parent's, none is the table's own (G-253).
                   (exists (select 1 from pg_catalog.pg_constraint con where con.conrelid = att.attrelid and con.contype = 'n' and con.conkey[1] = att.attnum and not con.conislocal)
                    and not exists (select 1 from pg_catalog.pg_constraint con where con.conrelid = att.attrelid and con.contype = 'n' and con.conkey[1] = att.attnum and con.conislocal)) as not_null_inherited_only,
                   -- A child's own default for a column it only inherits: not the parent's, which a clone would give it (G-237).
                   (not att.attislocal and att.attgenerated = ''
                    and pg_catalog.pg_get_expr(def.adbin, def.adrelid) is distinct from
                        (select pg_catalog.pg_get_expr(parent_def.adbin, parent_def.adrelid)
                         from pg_catalog.pg_inherits parent_inh
                         inner join pg_catalog.pg_attribute parent_att on parent_att.attrelid = parent_inh.inhparent and parent_att.attname = att.attname and not parent_att.attisdropped
                         left join pg_catalog.pg_attrdef parent_def on parent_def.adrelid = parent_att.attrelid and parent_def.adnum = parent_att.attnum
                         where parent_inh.inhrelid = att.attrelid
                         order by parent_inh.inhseqno
                         limit 1)) as overrides_inherited_default,
                   -- A partition's NOT NULL constraint, whoever owns it, and whether its name is not the parent's (PostgreSQL 18): a partition made with PARTITION OF takes the parent's name, so one made first and attached is written with its columns (G-264).
                   (select con.conname from pg_catalog.pg_constraint con
                    where con.conrelid = att.attrelid and con.contype = 'n' and con.conkey[1] = att.attnum and cls.relispartition
                    limit 1) as partition_not_null_name,
                   exists (select 1 from pg_catalog.pg_constraint con
                           where con.conrelid = att.attrelid and con.contype = 'n' and con.conkey[1] = att.attnum and cls.relispartition
                             and con.conname is distinct from
                                 (select parent_con.conname
                                  from pg_catalog.pg_inherits parent_inh
                                  inner join pg_catalog.pg_attribute parent_att on parent_att.attrelid = parent_inh.inhparent and parent_att.attname = att.attname and not parent_att.attisdropped
                                  inner join pg_catalog.pg_constraint parent_con on parent_con.conrelid = parent_att.attrelid and parent_con.contype = 'n' and parent_con.conkey[1] = parent_att.attnum
                                  where parent_inh.inhrelid = att.attrelid
                                  limit 1)) as partition_not_null_differs,
                   -- A partition column that is NOT NULL while its parent's is not (G-266): PARTITION OF alone would make it nullable, so it is written in the partition's element list.
                   (cls.relispartition and att.attnotnull
                    and not exists (select 1
                                    from pg_catalog.pg_inherits parent_inh
                                    inner join pg_catalog.pg_attribute parent_att on parent_att.attrelid = parent_inh.inhparent and parent_att.attname = att.attname and not parent_att.attisdropped
                                    where parent_inh.inhrelid = att.attrelid and parent_att.attnotnull)) as partition_adds_not_null,
                   -- A partition whose columns are in another order than its parent's (G-297): PARTITION OF would give it the parent's, so it is written with its columns and attached.
                   (cls.relispartition
                    and (select pg_catalog.array_agg(own_att.attname order by own_att.attnum) from pg_catalog.pg_attribute own_att where own_att.attrelid = att.attrelid and own_att.attnum > 0 and not own_att.attisdropped)
                        is distinct from
                        (select pg_catalog.array_agg(parent_att.attname order by parent_att.attnum)
                         from pg_catalog.pg_inherits parent_inh
                         inner join pg_catalog.pg_attribute parent_att on parent_att.attrelid = parent_inh.inhparent and parent_att.attnum > 0 and not parent_att.attisdropped
                         where parent_inh.inhrelid = att.attrelid)) as partition_order_differs
            from pg_catalog.pg_attribute att
            inner join pg_catalog.pg_class cls on cls.oid = att.attrelid
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            inner join pg_catalog.pg_type typ on typ.oid = att.atttypid
            left join pg_catalog.pg_attrdef def on def.adrelid = att.attrelid and def.adnum = att.attnum
            left join lateral (select pg_catalog.pg_get_expr(parent_def.adbin, parent_def.adrelid) as expression
                               from pg_catalog.pg_inherits parent_inh
                               inner join pg_catalog.pg_attribute parent_att on parent_att.attrelid = parent_inh.inhparent and parent_att.attname = att.attname and not parent_att.attisdropped and parent_att.attgenerated <> ''
                               inner join pg_catalog.pg_attrdef parent_def on parent_def.adrelid = parent_att.attrelid and parent_def.adnum = parent_att.attnum
                               where parent_inh.inhrelid = att.attrelid and att.attgenerated <> '' and att.attinhcount > 0
                               order by parent_inh.inhseqno
                               limit 1) parent_gen on true
            left join pg_catalog.pg_collation coll on coll.oid = att.attcollation
            left join pg_catalog.pg_namespace coll_ns on coll_ns.oid = coll.collnamespace
            where att.attnum > 0
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = cls.oid and ext_dep.deptype = 'e')
              and not att.attisdropped
              and cls.relkind in ('r', 'p')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, cls.relname, att.attnum;
            """;

        var tables = new Dictionary<string, LiveTableSnapshot>(PgProj.Parser.SqlIdentifiers.NameComparer);

        await using (var tableCommand = CreateCommand(tableSql, connection, scopeSchemas))
        await using (var reader = await tableCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schemaName = reader.GetString(0);
                var tableName = reader.GetString(1);
                var tableComment = reader.IsDBNull(2) ? null : reader.GetString(2);
                var isUnlogged = reader.GetBoolean(3);
                var tablespaceName = reader.IsDBNull(4) ? null : reader.GetString(4);
                var storageOptions = reader.IsDBNull(5) ? [] : reader.GetFieldValue<string[]>(5);
                var partitionKeyDef = reader.IsDBNull(6) ? null : reader.GetString(6);
                var partitionOfSchemaName = reader.IsDBNull(7) ? null : reader.GetString(7);
                var partitionOfTableName = reader.IsDBNull(8) ? null : reader.GetString(8);
                var partitionBoundRaw = reader.IsDBNull(9) ? null : reader.GetString(9);
                var rowSecurityEnabled = reader.GetBoolean(10);
                var rowSecurityForced = reader.GetBoolean(11);
                var ofTypeSchemaName = reader.IsDBNull(12) ? null : reader.GetString(12);
                var ofTypeName = reader.IsDBNull(13) ? null : reader.GetString(13);
                var inheritsFrom = reader.IsDBNull(14) ? null : reader.GetFieldValue<string[]>(14);
                tables[DatabaseModel.CreateKey(schemaName, tableName)] = new LiveTableSnapshot(
                    schemaName,
                    tableName,
                    [],
                    tableComment,
                    isUnlogged,
                    tablespaceName,
                    storageOptions,
                    partitionKeyDef,
                    partitionOfSchemaName,
                    partitionOfTableName,
                    partitionBoundRaw,
                    rowSecurityEnabled,
                    rowSecurityForced,
                    ofTypeSchemaName,
                    ofTypeName,
                    inheritsFrom)
                {
                    AccessMethod = reader.IsDBNull(15) ? null : reader.GetString(15),
                    IsDefaultAccessMethod = !reader.IsDBNull(16) && reader.GetBoolean(16),
                    ReplicaIdentity = reader.IsDBNull(17) ? null : reader.GetString(17),
                    InternalTriggerStates = reader.IsDBNull(18) ? [] : reader.GetFieldValue<string[]>(18),
                };
            }
        }

        var columnsByTable = new Dictionary<string, List<LiveColumnSnapshot>>(PgProj.Parser.SqlIdentifiers.NameComparer);

        await using (var columnCommand = CreateCommand(columnSql, connection, scopeSchemas))
        await using (var reader = await columnCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schemaName = reader.GetString(0);
                var tableName = reader.GetString(1);
                var tableKey = DatabaseModel.CreateKey(schemaName, tableName);
                if (!columnsByTable.TryGetValue(tableKey, out var columns))
                {
                    columns = [];
                    columnsByTable[tableKey] = columns;
                }

                columns.Add(new LiveColumnSnapshot(
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetBoolean(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(10) ? null : reader.GetString(10) switch
                    {
                        "a" => "ALWAYS",
                        "d" => "BY DEFAULT",
                        _ => null,
                    },
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetBoolean(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.GetBoolean(14))
                {
                    Storage = reader.IsDBNull(15) ? null : reader.GetString(15),
                    IsDefaultStorage = !reader.IsDBNull(16) && reader.GetBoolean(16),
                    Compression = reader.IsDBNull(17) ? null : reader.GetString(17),
                    NotNullName = reader.IsDBNull(18) ? null : reader.GetString(18),
                    NotNullComment = reader.IsDBNull(19) ? null : reader.GetString(19),
                    NotNullNoInherit = reader.GetBoolean(20),
                    NotNullIsInheritedOnly = reader.GetBoolean(21),
                    OverridesInheritedDefault = !reader.IsDBNull(22) && reader.GetBoolean(22),
                    PartitionNotNullName = reader.IsDBNull(23) ? null : reader.GetString(23),
                    PartitionNotNullDiffers = reader.GetBoolean(24),
                    PartitionAddsNotNull = reader.GetBoolean(25),
                    PartitionOrderDiffers = reader.GetBoolean(26),
                });
            }
        }

        return tables.Values
            .Select(table => table with
            {
                Columns = columnsByTable.TryGetValue(DatabaseModel.CreateKey(table.SchemaName, table.TableName), out var columns)
                    ? columns
                    : []
            })
            .ToArray();
    }

    private static async Task<IReadOnlyList<LiveConstraintSnapshot>> ReadConstraintsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        // A constraint PostgreSQL copied onto a partition or child table from its parent is the parent's, not the child's
        // own (G-101): a cloned primary/unique/foreign key has conparentid set, and an inherited CHECK is not conislocal.
        const string constraintSql =
            """
            select ns.nspname as schema_name,
                   cls.relname as table_name,
                   con.conname as constraint_name,
                   pg_catalog.pg_get_constraintdef(con.oid, true) as constraint_definition,
                   pg_catalog.obj_description(con.oid, 'pg_constraint') as comment,
                   case when con.contype = 'c' then pg_catalog.pg_get_expr(con.conbin, con.conrelid) end as check_expression,
                   -- pg_get_constraintdef leaves out a primary key's or unique constraint's WITH (...), which is on its index (G-195); an exclusion constraint's it prints.
                   case when con.contype in ('p', 'u') then pg_catalog.array_to_string(con_idx.reloptions, ', ') end as index_options,
                   -- The table is clustered on, or replicates by, this key's index (G-255, G-256).
                   coalesce(con_ind.indisclustered, false) as is_clustered,
                   coalesce(con_ind.indisreplident, false) as is_replica_identity,
                   -- The comment on a key's or exclusion constraint's index, which has the constraint's name (G-295).
                   case when con.contype in ('p', 'u', 'x') then pg_catalog.obj_description(con.conindid, 'pg_class') end as index_comment
            from pg_catalog.pg_constraint con
            left join pg_catalog.pg_index con_ind on con_ind.indexrelid = con.conindid and con.contype in ('p', 'u', 'x')
            inner join pg_catalog.pg_class cls on cls.oid = con.conrelid
            left join pg_catalog.pg_class con_idx on con_idx.oid = con.conindid
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where cls.relkind in ('r', 'p')
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = cls.oid and ext_dep.deptype = 'e')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and con.contype in ('p', 'u', 'f', 'c', 'x')
              -- A key cloned onto a partition is the parent's, unless it has a name other than the one PostgreSQL gives a clone: then it was made on the partition first and the
              -- parent's key adopted it, and it is kept (G-237).
              and (con.conparentid = 0
                   -- The same for an exclusion constraint (PostgreSQL 17 and later) over plain columns, whose generated name ends in _excl (G-296).
                   or (con.contype = 'x'
                       and pg_catalog.length(cls.relname) < 40
                       and not exists (select 1 from pg_catalog.unnest(pg_catalog.string_to_array(con_ind.indkey::text, ' ')::int2[]) k(attnum) where k.attnum = 0)
                       and pg_catalog.regexp_replace(con.conname, '[0-9]*$', '') is distinct from
                           (cls.relname || '_' || coalesce((select string_agg(a.attname::text, '_' order by k.ord)
                                                            from pg_catalog.unnest(pg_catalog.string_to_array(con_ind.indkey::text, ' ')::int2[]) with ordinality k(attnum, ord)
                                                            left join pg_catalog.pg_attribute a on a.attrelid = con.conrelid and a.attnum = k.attnum), '') || '_excl'))
                   or (con.contype in ('p', 'u')
                       and pg_catalog.length(cls.relname) < 40
                       and pg_catalog.regexp_replace(con.conname, '[0-9]*$', '') is distinct from
                           (cls.relname || '_' || case when con.contype = 'p' then 'pkey' else coalesce((select string_agg(coalesce(a.attname::text, 'expr'), '_' order by k.ord)
                                                                       from pg_catalog.unnest(pg_catalog.string_to_array(con_ind.indkey::text, ' ')::int2[]) with ordinality k(attnum, ord)
                                                                       left join pg_catalog.pg_attribute a on a.attrelid = con.conrelid and a.attnum = k.attnum), '') || '_key' end)))
              and (con.conislocal or con.conparentid <> 0)
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, cls.relname, con.conname;
            """;

        var constraints = new List<LiveConstraintSnapshot>();

        await using var command = CreateCommand(constraintSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            constraints.Add(new LiveConstraintSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8))
            {
                IndexComment = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }

        return constraints;
    }

    /// <summary>
    /// An index PostgreSQL marks <c>INVALID</c>, left by a failed <c>CREATE INDEX CONCURRENTLY</c>, is read with that state (G-271), so <c>diff</c> reports it and
    /// <c>publish</c> rebuilds it instead of taking it for finished (the next <c>CREATE INDEX</c> of that name would fail with "already exists").
    /// </summary>
    private static void AttachInvalidIndexes(DatabaseModel model, IReadOnlyList<LiveIndexSnapshot> indexes)
    {
        var invalid = indexes.Where(static index => !index.IsValid).Select(static index => (index.SchemaName, index.IndexName)).ToHashSet();
        if (invalid.Count == 0)
        {
            return;
        }

        model.Indexes = model.Indexes.ToDictionary(
            static pair => pair.Key,
            pair => invalid.Contains((pair.Value.SchemaName ?? "public", pair.Value.Name)) ? pair.Value with { IsValid = false } : pair.Value,
            PgProj.Parser.SqlIdentifiers.NameComparer);
    }

    private static async Task<IReadOnlyList<LiveIndexSnapshot>> ReadIndexesAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        // An index PostgreSQL created on a partition to match an index on its partitioned parent is attached to that
        // index (relispartition) and belongs to the parent's declaration, not the partition's own (G-101). A materialized
        // view ('m') owns indexes too (G-171).
        const string indexSql =
            """
            select ns.nspname as schema_name,
                   tbl.relname as table_name,
                   idx.relname as index_name,
                   pg_catalog.pg_get_indexdef(idx.oid, 0, true) as index_definition,
                   pg_catalog.obj_description(idx.oid, 'pg_class') as index_comment,
                   pg_catalog.pg_get_expr(ind.indpred, ind.indrelid) as index_predicate,
                   ind.indisclustered as is_clustered,
                   ind.indisreplident as is_replica_identity,
                   ind.indisvalid as is_valid
            from pg_catalog.pg_index ind
            inner join pg_catalog.pg_class idx on idx.oid = ind.indexrelid
            inner join pg_catalog.pg_class tbl on tbl.oid = ind.indrelid
            inner join pg_catalog.pg_namespace ns on ns.oid = tbl.relnamespace
            -- A foreign key records the index it references in conindid too; only a key or exclusion constraint owns its index (G-273).
            left join pg_catalog.pg_constraint con on con.conindid = ind.indexrelid and con.contype in ('p', 'u', 'x')
            where tbl.relkind in ('r', 'p', 'm')
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = tbl.oid and ext_dep.deptype = 'e')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and con.oid is null
              -- An index attached to a partitioned parent's is the parent's, unless it has a name other than the one PostgreSQL gives a clone: then it was made on the partition first
              -- and the parent's index adopted it, and it is kept (G-237).
              and (not idx.relispartition
                   or (pg_catalog.length(tbl.relname) < 40
                       and pg_catalog.regexp_replace(idx.relname, '[0-9]*$', '') is distinct from
                           (tbl.relname || '_' || coalesce((select string_agg(coalesce(a.attname::text, 'expr'), '_' order by k.ord)
                                                            from pg_catalog.unnest(pg_catalog.string_to_array(ind.indkey::text, ' ')::int2[]) with ordinality k(attnum, ord)
                                                            left join pg_catalog.pg_attribute a on a.attrelid = ind.indrelid and a.attnum = k.attnum), '') || '_idx')))
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, tbl.relname, idx.relname;
            """;

        var indexes = new List<LiveIndexSnapshot>();

        await using var command = CreateCommand(indexSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            indexes.Add(new LiveIndexSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8)));
        }

        return indexes;
    }

    /// <summary>
    /// Views that refer to each other, which <c>CREATE OR REPLACE VIEW</c> makes possible, cannot be created in any order (G-254). <c>pg_dump</c> breaks the cycle with a placeholder:
    /// <c>CREATE VIEW v AS SELECT NULL::bigint AS a</c>, the other views, then <c>CREATE OR REPLACE VIEW v</c> with the real body. One view of each cycle (the first by name) gets that
    /// placeholder here, so a project built from the database can be deployed.
    /// </summary>
    private static async Task<IReadOnlyList<LiveViewSnapshot>> AttachViewCyclePlaceholdersAsync(NpgsqlConnection connection, List<LiveViewSnapshot> views, CancellationToken cancellationToken)
    {
        const string dependencySql =
            """
            select distinct vns.nspname, vcls.relname, rns.nspname, rcls.relname
            from pg_catalog.pg_rewrite rw
            inner join pg_catalog.pg_class vcls on vcls.oid = rw.ev_class and vcls.relkind = 'v'
            inner join pg_catalog.pg_namespace vns on vns.oid = vcls.relnamespace
            inner join pg_catalog.pg_depend dep on dep.classid = 'pg_catalog.pg_rewrite'::regclass and dep.objid = rw.oid and dep.refclassid = 'pg_catalog.pg_class'::regclass and dep.deptype = 'n'
            inner join pg_catalog.pg_class rcls on rcls.oid = dep.refobjid and rcls.relkind = 'v' and rcls.oid <> vcls.oid
            inner join pg_catalog.pg_namespace rns on rns.oid = rcls.relnamespace
            where rw.rulename = '_RETURN' and vns.nspname not in ('pg_catalog', 'information_schema')
            """;

        var known = views.Where(static view => !view.IsMaterialized).Select(static view => ViewCycleKey(view.SchemaName, view.ViewName)).ToHashSet(StringComparer.Ordinal);
        if (known.Count < 2)
        {
            return views;
        }

        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(dependencySql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var from = ViewCycleKey(reader.GetString(0), reader.GetString(1));
                var to = ViewCycleKey(reader.GetString(2), reader.GetString(3));
                if (known.Contains(from) && known.Contains(to))
                {
                    if (!edges.TryGetValue(from, out var targets))
                    {
                        edges[from] = targets = [];
                    }

                    targets.Add(to);
                }
            }
        }

        var breakers = FindViewCycleBreakers(known, edges);
        if (breakers.Count == 0)
        {
            return views;
        }

        const string columnSql =
            """
            select ns.nspname, cls.relname, att.attname, pg_catalog.format_type(att.atttypid, att.atttypmod)
            from pg_catalog.pg_attribute att
            inner join pg_catalog.pg_class cls on cls.oid = att.attrelid and cls.relkind = 'v'
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where att.attnum > 0 and not att.attisdropped and (ns.nspname || '.' || cls.relname) = any(@keys)
            order by ns.nspname, cls.relname, att.attnum
            """;

        var columns = new Dictionary<string, List<(string Name, string Type)>>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(columnSql, connection))
        {
            command.Parameters.AddWithValue("keys", breakers.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = ViewCycleKey(reader.GetString(0), reader.GetString(1));
                if (!columns.TryGetValue(key, out var list))
                {
                    columns[key] = list = [];
                }

                list.Add((reader.GetString(2), reader.GetString(3)));
            }
        }

        return views
            .Select(view => !view.IsMaterialized && columns.TryGetValue(ViewCycleKey(view.SchemaName, view.ViewName), out var placeholder)
                ? view with { PlaceholderColumns = placeholder }
                : view)
            .ToArray();
    }

    private static string ViewCycleKey(string schemaName, string viewName) => $"{schemaName}.{viewName}";

    /// <summary>The first view by name of each cycle among the views (strongly connected components of more than one view), found with Tarjan's algorithm.</summary>
    private static List<string> FindViewCycleBreakers(IReadOnlySet<string> nodes, Dictionary<string, List<string>> edges)
    {
        var index = 0;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var breakers = new List<string>();

        void Connect(string node)
        {
            indexes[node] = lowLinks[node] = index++;
            stack.Push(node);
            onStack.Add(node);
            foreach (var target in edges.GetValueOrDefault(node) ?? [])
            {
                if (!indexes.ContainsKey(target))
                {
                    Connect(target);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[target]);
                }
                else if (onStack.Contains(target))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indexes[target]);
                }
            }

            if (lowLinks[node] == indexes[node])
            {
                var component = new List<string>();
                string member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    component.Add(member);
                }
                while (!string.Equals(member, node, StringComparison.Ordinal));

                if (component.Count > 1)
                {
                    breakers.Add(component.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).First());
                }
            }
        }

        foreach (var node in nodes.OrderBy(static name => name, StringComparer.Ordinal))
        {
            if (!indexes.ContainsKey(node))
            {
                Connect(node);
            }
        }

        return breakers;
    }

    private static async Task<IReadOnlyList<LiveViewSnapshot>> ReadViewsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
        => await AttachViewCyclePlaceholdersAsync(connection, await ReadViewRowsAsync(connection, scopeSchemas, cancellationToken), cancellationToken);

    private static async Task<List<LiveViewSnapshot>> ReadViewRowsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string viewSql =
            """
            select ns.nspname as schema_name,
                   cls.relname as view_name,
                   pg_catalog.pg_get_viewdef(cls.oid, true) as view_definition,
                   cls.relkind = 'm' as is_materialized,
                   pg_catalog.obj_description(cls.oid, 'pg_class') as view_comment,
                   case when cls.relkind = 'v' then cls.reloptions else null end as view_options,
                   array(select format('COMMENT ON COLUMN %I.%I.%I IS %L', ns.nspname, cls.relname, att.attname, pg_catalog.col_description(cls.oid, att.attnum))
                         from pg_catalog.pg_attribute att
                         where att.attrelid = cls.oid and att.attnum > 0 and not att.attisdropped and pg_catalog.col_description(cls.oid, att.attnum) is not null
                         order by att.attnum) as column_comments,
                   case when cls.relkind = 'm' then cls.reloptions else null end as storage_options,
                   cls.relispopulated as is_populated,
                   ts.spcname as tablespace_name,
                   case when cls.relkind = 'm' then am.amname end as access_method,
                   am.amname is not distinct from pg_catalog.current_setting('default_table_access_method') as is_default_access_method,
                   case when cls.relkind = 'v' then array(select format('ALTER VIEW %I.%I ALTER COLUMN %I SET DEFAULT %s', ns.nspname, cls.relname, att.attname, pg_catalog.pg_get_expr(def.adbin, def.adrelid))
                         from pg_catalog.pg_attrdef def
                         inner join pg_catalog.pg_attribute att on att.attrelid = def.adrelid and att.attnum = def.adnum
                         where def.adrelid = cls.oid and not att.attisdropped
                         order by att.attnum) else '{}'::text[] end as column_defaults
            from pg_catalog.pg_class cls
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            left join pg_catalog.pg_tablespace ts on ts.oid = cls.reltablespace
            left join pg_catalog.pg_am am on am.oid = cls.relam
            where cls.relkind in ('v', 'm')
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = cls.oid and ext_dep.deptype = 'e')
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, cls.relname;
            """;

        var views = new List<LiveViewSnapshot>();

        await using var command = CreateCommand(viewSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            views.Add(new LiveViewSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                IsMaterialized: reader.GetBoolean(3),
                Comment: reader.IsDBNull(4) ? null : reader.GetString(4),
                Options: reader.IsDBNull(5) ? null : ViewOptions.Canonicalize(reader.GetFieldValue<string[]>(5).Select(static entry => ViewOptions.Split(entry))).ToArray())
            {
                ColumnCommentStatements = reader.GetFieldValue<string[]>(6),
                StorageOptions = reader.IsDBNull(7) ? [] : reader.GetFieldValue<string[]>(7),
                IsPopulated = reader.GetBoolean(8),
                TablespaceName = reader.IsDBNull(9) ? null : reader.GetString(9),
                AccessMethod = reader.IsDBNull(10) ? null : reader.GetString(10),
                IsDefaultAccessMethod = !reader.IsDBNull(11) && reader.GetBoolean(11),
                ColumnDefaultStatements = reader.GetFieldValue<string[]>(12),
            });
        }

        return views;
    }

    private static async Task<IReadOnlyList<LiveFunctionSnapshot>> ReadFunctionsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string functionSql =
            """
            select ns.nspname as schema_name,
                   proc.proname as function_name,
                   pg_catalog.pg_get_functiondef(proc.oid) as function_definition,
                   pg_catalog.obj_description(proc.oid, 'pg_proc') as function_comment,
                   pg_catalog.pg_get_function_identity_arguments(proc.oid) as identity_arguments,
                   proc.prokind as prokind
            from pg_catalog.pg_proc proc
            inner join pg_catalog.pg_namespace ns on ns.oid = proc.pronamespace
            where proc.prokind in ('f', 'w', 'p')
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_proc'::regclass and ext_dep.objid = proc.oid and ext_dep.deptype = 'e')
              -- A range type's constructor functions and its multirange's are created with the type (G-69).
              and not exists (select 1 from pg_catalog.pg_depend int_dep where int_dep.classid = 'pg_catalog.pg_proc'::regclass and int_dep.objid = proc.oid and int_dep.deptype = 'i' and int_dep.refclassid = 'pg_catalog.pg_type'::regclass)
              -- A base type's support functions belong to the type, which is not modeled (G-241).
              and not exists (select 1 from pg_catalog.pg_type base_type where base_type.typtype = 'b' and proc.oid in (base_type.typinput::oid, base_type.typoutput::oid, base_type.typreceive::oid, base_type.typsend::oid, base_type.typmodin::oid, base_type.typmodout::oid, base_type.typanalyze::oid, base_type.typsubscript::oid))
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, proc.proname, proc.oid;
            """;

        var functions = new List<LiveFunctionSnapshot>();

        await using var command = CreateCommand(functionSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            functions.Add(new LiveFunctionSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetChar(5) == 'p'));
        }

        return functions;
    }

    private static async Task<IReadOnlyList<LiveTriggerSnapshot>> ReadTriggersAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        // Internal triggers (foreign-key enforcement) and triggers cloned onto partitions from a parent
        // (tgparentid <> 0) are implementation details; only user-declared triggers are modeled.
        const string triggerSql =
            """
            select ns.nspname as schema_name,
                   tbl.relname as table_name,
                   trg.tgname as trigger_name,
                   pg_catalog.pg_get_triggerdef(trg.oid) as trigger_definition,
                   fn_ns.nspname as function_schema,
                   fn.proname as function_name,
                   pg_catalog.obj_description(trg.oid, 'pg_trigger') as comment,
                   trg.tgenabled::text as enabled_code,
                   (
                       with recursive clones(oid, tgrelid, tgenabled, parent_enabled, depth) as (
                           select c.oid, c.tgrelid, c.tgenabled, trg.tgenabled, 1
                           from pg_catalog.pg_trigger c
                           where c.tgparentid = trg.oid
                           union all
                           select c.oid, c.tgrelid, c.tgenabled, k.tgenabled, k.depth + 1
                           from pg_catalog.pg_trigger c
                           inner join clones k on c.tgparentid = k.oid
                       )
                       select pg_catalog.json_agg(pg_catalog.json_build_object('schema', pns.nspname, 'table', ptbl.relname, 'enabled', k.tgenabled::text) order by k.depth, pns.nspname, ptbl.relname)::text
                       from clones k
                       inner join pg_catalog.pg_class ptbl on ptbl.oid = k.tgrelid
                       inner join pg_catalog.pg_namespace pns on pns.oid = ptbl.relnamespace
                       where k.tgenabled <> 'O' or k.tgenabled <> k.parent_enabled
                   ) as partition_states
            from pg_catalog.pg_trigger trg
            inner join pg_catalog.pg_class tbl on tbl.oid = trg.tgrelid
            inner join pg_catalog.pg_namespace ns on ns.oid = tbl.relnamespace
            inner join pg_catalog.pg_proc fn on fn.oid = trg.tgfoid
            inner join pg_catalog.pg_namespace fn_ns on fn_ns.oid = fn.pronamespace
            where not trg.tgisinternal
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = tbl.oid and ext_dep.deptype = 'e')
              and trg.tgparentid = 0
              and ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, tbl.relname, trg.tgname;
            """;

        var triggers = new List<LiveTriggerSnapshot>();

        await using var command = CreateCommand(triggerSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            triggers.Add(new LiveTriggerSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                EnabledStateOf(reader.GetString(7)))
            {
                PartitionStates = reader.IsDBNull(8) ? [] : ReadPartitionTriggerStates(reader.GetString(8)),
            });
        }

        return triggers;
    }

    /// <summary>
    /// Renders a trigger statement for the snapshot script. <c>pg_get_triggerdef</c> renders the
    /// executed function relative to the session <c>search_path</c>, so the function name is rewritten
    /// to its schema-qualified form to keep the live model independent of the connection's settings.
    /// </summary>
    /// <summary>The <c>COMMENT ON TRIGGER</c> that states a trigger's comment (G-163).</summary>
    internal static string BuildTriggerCommentStatement(LiveTriggerSnapshot trigger)
        => $"COMMENT ON TRIGGER {QuoteIdentifier(trigger.TriggerName)} ON {QualifyIdentifier(trigger.SchemaName, trigger.TableName)} IS {QuoteLiteral(trigger.Comment ?? string.Empty)};";

    /// <summary>The <c>COMMENT ON INDEX</c> for the index of a key or exclusion constraint, which has the constraint's name (G-295); an empty string when the index has no comment.</summary>
    internal static string BuildConstraintIndexCommentStatement(LiveConstraintSnapshot constraint)
        => string.IsNullOrEmpty(constraint.IndexComment) ? string.Empty : $"COMMENT ON INDEX {QualifyIdentifier(constraint.SchemaName, constraint.ConstraintName)} IS {QuoteLiteral(constraint.IndexComment)};";

    internal static string BuildCreateTriggerStatement(LiveTriggerSnapshot trigger)
    {
        var definition = trigger.TriggerDefinition.Trim().TrimEnd(';');
        var qualifiedFunction = QualifyIdentifier(trigger.FunctionSchema, trigger.FunctionName);

        var match = ExecuteFunctionClauseRegex.Match(definition);
        var rewritten = match.Success
            ? string.Concat(
                definition.AsSpan(0, match.Index),
                match.Groups["prefix"].Value,
                qualifiedFunction,
                definition.AsSpan(match.Index + match.Length - 1))
            : definition;

        // A trigger that is not enabled in the default way says so with a statement of its own, which CREATE TRIGGER has no clause for (G-257).
        var statement = trigger.EnabledState is null
            ? rewritten + ";"
            : rewritten + ";\n" + $"ALTER TABLE {QualifyIdentifier(trigger.SchemaName, trigger.TableName)} {trigger.EnabledState} TRIGGER {QuoteIdentifier(trigger.TriggerName)};";

        // The copies on partitions with a firing mode of their own (G-293).
        return statement + string.Concat(trigger.PartitionStates.Select(partition =>
            "\n" + $"ALTER TABLE {QualifyIdentifier(partition.SchemaName, partition.TableName)} {partition.State} TRIGGER {QuoteIdentifier(trigger.TriggerName)};"));
    }

    /// <summary>The partitions whose copy of a trigger is not enabled, or differs from its parent's copy, from the query's JSON (G-293): the partition and the statement's firing mode (<c>ENABLE</c> when only the parent's is not).</summary>
    private static IReadOnlyList<(string SchemaName, string TableName, string State)> ReadPartitionTriggerStates(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(static item => (
            item.GetProperty("schema").GetString()!,
            item.GetProperty("table").GetString()!,
            EnabledStateOf(item.GetProperty("enabled").GetString()!) ?? "ENABLE"))];
    }

    /// <summary>
    /// The firing mode a <c>tgenabled</c> or <c>ev_enabled</c> code stands for (G-257): <c>O</c> (enabled, the default) is none; <c>D</c> disabled, <c>R</c> replica, <c>A</c> always.
    /// </summary>
    internal static string? EnabledStateOf(string code)
        => code switch
        {
            "D" => "DISABLE",
            "R" => "ENABLE REPLICA",
            "A" => "ENABLE ALWAYS",
            _ => null,
        };

    /// <summary>Row-level security policies (G-76). PostgreSQL has no <c>pg_get_policydef</c>, unlike triggers/indexes/views/constraints, so the statement is assembled client-side from <c>pg_policy</c>'s already-rendered pieces in <see cref="BuildCreatePolicyStatement"/>.</summary>
    private static async Task<IReadOnlyList<LivePolicySnapshot>> ReadPoliciesAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string policySql =
            """
            select ns.nspname as schema_name,
                   tbl.relname as table_name,
                   pol.polname as policy_name,
                   pol.polpermissive as permissive,
                   case pol.polcmd
                       when 'r' then 'select'
                       when 'a' then 'insert'
                       when 'w' then 'update'
                       when 'd' then 'delete'
                       else 'all'
                   end as cmd_name,
                   (
                       select array_agg(case when role_oid = 0 then 'PUBLIC' else pg_catalog.pg_get_userbyid(role_oid) end order by ord)
                       from unnest(pol.polroles) with ordinality as roles(role_oid, ord)
                   ) as roles,
                   pg_catalog.pg_get_expr(pol.polqual, pol.polrelid) as using_expression,
                   pg_catalog.pg_get_expr(pol.polwithcheck, pol.polrelid) as with_check_expression,
                   pg_catalog.obj_description(pol.oid, 'pg_policy') as comment
            from pg_catalog.pg_policy pol
            inner join pg_catalog.pg_class tbl on tbl.oid = pol.polrelid
            inner join pg_catalog.pg_namespace ns on ns.oid = tbl.relnamespace
            where ns.nspname not in ('pg_catalog', 'information_schema')
              and ns.nspname not like 'pg_toast%'
              -- A policy on an extension's table is the extension's (G-274).
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = tbl.oid and ext_dep.deptype = 'e')
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, tbl.relname, pol.polname;
            """;

        var policies = new List<LivePolicySnapshot>();

        await using var command = CreateCommand(policySql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            policies.Add(new LivePolicySnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? [] : reader.GetFieldValue<string[]>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return policies;
    }

    /// <summary>
    /// A clause is emitted only when it differs from what omitting it means, the same convention <c>CreateOperation.
    /// GenerateCreatePolicy</c> uses for a project-authored policy, so a scaffolded/re-read policy round-trips to the same text (G-76).
    /// </summary>
    internal static string BuildPolicyCommentStatement(LivePolicySnapshot policy)
        => $"COMMENT ON POLICY {QuoteIdentifier(policy.PolicyName)} ON {QualifyIdentifier(policy.SchemaName, policy.TableName)} IS {QuoteLiteral(policy.Comment ?? string.Empty)};";

    internal static string BuildCreatePolicyStatement(LivePolicySnapshot policy)
    {
        var asClause = policy.IsPermissive ? string.Empty : " AS RESTRICTIVE";
        var forClause = string.Equals(policy.Command, "all", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" FOR {policy.Command.ToUpperInvariant()}";
        var toClause = policy.Roles.Count == 1 && string.Equals(policy.Roles[0], "PUBLIC", StringComparison.Ordinal)
            ? string.Empty
            : $" TO {string.Join(", ", policy.Roles.Select(RenderGrantee))}";
        var usingClause = string.IsNullOrEmpty(policy.UsingExpression) ? string.Empty : $" USING ({policy.UsingExpression})";
        var withCheckClause = string.IsNullOrEmpty(policy.WithCheckExpression) ? string.Empty : $" WITH CHECK ({policy.WithCheckExpression})";

        return $"CREATE POLICY {RenderIdentifier(policy.PolicyName)} ON {QualifyIdentifier(policy.SchemaName, policy.TableName)}{asClause}{forClause}{toClause}{usingClause}{withCheckClause};";
    }

    /// <summary>
    /// The database's event triggers (G-127), read from <c>pg_event_trigger</c>. One an extension owns (<c>deptype</c> <c>e</c>, as
    /// <c>pgaudit</c>'s are) belongs to that extension and is left out. An event trigger has no schema, so <paramref name="scopeSchemas"/>
    /// scopes it by the schema of the function it runs, the way a project that lists <c>schemas</c> means its own objects (G-85).
    /// </summary>
    private static async Task<IReadOnlyList<LiveEventTriggerSnapshot>> ReadEventTriggersAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string eventTriggerSql =
            """
            select evt.evtname as name,
                   evt.evtevent as event_name,
                   evt.evttags as tags,
                   fns.nspname as function_schema,
                   fn.proname as function_name,
                   evt.evtenabled::text as enabled_mode
            from pg_catalog.pg_event_trigger evt
            inner join pg_catalog.pg_proc fn on fn.oid = evt.evtfoid
            inner join pg_catalog.pg_namespace fns on fns.oid = fn.pronamespace
            where not exists (
                      select 1
                      from pg_catalog.pg_depend dep
                      where dep.classid = 'pg_catalog.pg_event_trigger'::regclass
                        and dep.objid = evt.oid
                        and dep.deptype = 'e')
              and (@schemas is null or fns.nspname = any(@schemas))
            order by evt.evtname;
            """;

        var eventTriggers = new List<LiveEventTriggerSnapshot>();

        await using var command = CreateCommand(eventTriggerSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            eventTriggers.Add(new LiveEventTriggerSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? [] : reader.GetFieldValue<string[]>(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return eventTriggers;
    }

    /// <summary>
    /// <c>CREATE EVENT TRIGGER</c> for a live one, with the <c>ALTER EVENT TRIGGER</c> that says when it fires unless that is the default (G-127),
    /// the same shape <c>CreateOperation.GenerateCreateEventTrigger</c> writes for a project's, so a scaffolded one reads back as itself.
    /// </summary>
    internal static string BuildCreateEventTriggerStatement(LiveEventTriggerSnapshot eventTrigger)
    {
        var tagClause = eventTrigger.Tags.Count == 0
            ? string.Empty
            : $" WHEN TAG IN ({string.Join(", ", eventTrigger.Tags.Select(QuoteLiteral))})";
        var name = RenderIdentifier(eventTrigger.Name);
        var statement = $"CREATE EVENT TRIGGER {name} ON {eventTrigger.EventName}{tagClause} EXECUTE FUNCTION {QualifyIdentifier(eventTrigger.FunctionSchema, eventTrigger.FunctionName)}();";

        return eventTrigger.EnabledMode.ToUpperInvariant() switch
        {
            "D" => $"{statement}{Environment.NewLine}ALTER EVENT TRIGGER {name} DISABLE;",
            "R" => $"{statement}{Environment.NewLine}ALTER EVENT TRIGGER {name} ENABLE REPLICA;",
            "A" => $"{statement}{Environment.NewLine}ALTER EVENT TRIGGER {name} ENABLE ALWAYS;",
            _ => statement,
        };
    }

    /// <summary>
    /// The database's user-defined operators (G-129), read from <c>pg_operator</c>. The ones PostgreSQL ships have OIDs below <c>FirstNormalObjectId</c>
    /// (16384); one an extension owns (<c>deptype</c> <c>e</c>) belongs to that extension; and a <em>shell</em> operator (one that only a commutator or negator
    /// named, so it has no function, <c>oprcode</c> 0) is not an operator yet. Those are left out. An operator is in a schema, so <paramref name="scopeSchemas"/>
    /// scopes it by that. A commutator or negator is read as its schema and name (PostgreSQL links it both ways, and the model does too).
    /// </summary>
    private static async Task<IReadOnlyList<LiveOperatorSnapshot>> ReadOperatorsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string operatorSql =
            """
            select ns.nspname as operator_schema,
                   o.oprname as operator_name,
                   case when o.oprleft = 0 then null else pg_catalog.format_type(o.oprleft, null) end as left_type,
                   case when o.oprright = 0 then null else pg_catalog.format_type(o.oprright, null) end as right_type,
                   fns.nspname as function_schema,
                   fn.proname as function_name,
                   comns.nspname as commutator_schema,
                   com.oprname as commutator_name,
                   negns.nspname as negator_schema,
                   neg.oprname as negator_name,
                   case when o.oprrest = 0 then null else o.oprrest::pg_catalog.regproc::text end as restrict_function,
                   case when o.oprjoin = 0 then null else o.oprjoin::pg_catalog.regproc::text end as join_function,
                   o.oprcanhash as hashes,
                   o.oprcanmerge as merges
            from pg_catalog.pg_operator o
            inner join pg_catalog.pg_namespace ns on ns.oid = o.oprnamespace
            inner join pg_catalog.pg_proc fn on fn.oid = o.oprcode::pg_catalog.oid
            inner join pg_catalog.pg_namespace fns on fns.oid = fn.pronamespace
            left join pg_catalog.pg_operator com on com.oid = o.oprcom
            left join pg_catalog.pg_namespace comns on comns.oid = com.oprnamespace
            left join pg_catalog.pg_operator neg on neg.oid = o.oprnegate
            left join pg_catalog.pg_namespace negns on negns.oid = neg.oprnamespace
            where o.oid >= 16384
              and o.oprcode::pg_catalog.oid <> 0
              and not exists (
                      select 1
                      from pg_catalog.pg_depend dep
                      where dep.classid = 'pg_catalog.pg_operator'::regclass
                        and dep.objid = o.oid
                        and dep.deptype = 'e')
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, o.oprname, left_type nulls first, right_type nulls first;
            """;

        var operators = new List<LiveOperatorSnapshot>();

        await using var command = CreateCommand(operatorSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

            operators.Add(new LiveOperatorSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                Text(2),
                Text(3),
                reader.GetString(4),
                reader.GetString(5),
                Text(6),
                Text(7),
                Text(8),
                Text(9),
                Text(10),
                Text(11),
                reader.GetBoolean(12),
                reader.GetBoolean(13)));
        }

        return operators;
    }

    /// <summary>
    /// <c>CREATE OPERATOR</c> for a live one (G-129), the same shape <c>CreateOperation.GenerateCreateOperator</c> writes for a project's. The operand
    /// types are <c>format_type</c>'s text; the function is qualified; a commutator and negator are written <c>OPERATOR(schema.name)</c>.
    /// </summary>
    internal static string BuildCreateOperatorStatement(LiveOperatorSnapshot operatorSnapshot)
    {
        var options = new List<string>();
        if (operatorSnapshot.LeftType is { Length: > 0 } left)
        {
            options.Add($"LEFTARG = {left}");
        }

        if (operatorSnapshot.RightType is { Length: > 0 } right)
        {
            options.Add($"RIGHTARG = {right}");
        }

        options.Add($"FUNCTION = {QualifyIdentifier(operatorSnapshot.FunctionSchema, operatorSnapshot.FunctionName)}");
        if (operatorSnapshot.CommutatorName is not null)
        {
            options.Add($"COMMUTATOR = OPERATOR({QualifyOperator(operatorSnapshot.CommutatorSchema, operatorSnapshot.CommutatorName)})");
        }

        if (operatorSnapshot.NegatorName is not null)
        {
            options.Add($"NEGATOR = OPERATOR({QualifyOperator(operatorSnapshot.NegatorSchema, operatorSnapshot.NegatorName)})");
        }

        if (operatorSnapshot.RestrictFunction is not null)
        {
            options.Add($"RESTRICT = {operatorSnapshot.RestrictFunction}");
        }

        if (operatorSnapshot.JoinFunction is not null)
        {
            options.Add($"JOIN = {operatorSnapshot.JoinFunction}");
        }

        if (operatorSnapshot.Hashes)
        {
            options.Add("HASHES");
        }

        if (operatorSnapshot.Merges)
        {
            options.Add("MERGES");
        }

        return $"CREATE OPERATOR {QualifyOperator(operatorSnapshot.OperatorSchema, operatorSnapshot.OperatorName)} ({string.Join(", ", options)});";
    }

    /// <summary>An operator's name with its schema quoted where needed; the symbols are never quoted.</summary>
    private static string QualifyOperator(string? schemaName, string name)
        => string.IsNullOrWhiteSpace(schemaName) ? name : $"{RenderIdentifier(schemaName)}.{name}";

    /// <summary>
    /// The database's query rewrite rules (G-128), read from <c>pg_rewrite</c> with <c>pg_get_ruledef</c>, which is PostgreSQL's own rendering of the rule. The rules
    /// PostgreSQL ships (system views have them) have OIDs below <c>FirstNormalObjectId</c> (16384); a view is a <c>_RETURN</c> rule on an empty table, which is the
    /// view itself and not a rule; and one an extension owns (<c>deptype</c> <c>e</c>) belongs to that extension. A rule is on a table or a view, so
    /// <paramref name="scopeSchemas"/> scopes it by that relation's schema.
    /// </summary>
    private static async Task<IReadOnlyList<LiveRuleSnapshot>> ReadRulesAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string ruleSql =
            """
            select ns.nspname, cls.relname, r.rulename, pg_catalog.pg_get_ruledef(r.oid, false), r.ev_enabled::text
            from pg_catalog.pg_rewrite r
            inner join pg_catalog.pg_class cls on cls.oid = r.ev_class
            inner join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            where r.oid >= 16384
              and r.rulename <> '_RETURN'
              and cls.relkind in ('r', 'p', 'v')
              and not exists (
                      select 1
                      from pg_catalog.pg_depend dep
                      where dep.classid = 'pg_catalog.pg_rewrite'::regclass
                        and dep.objid = r.oid
                        and dep.deptype = 'e')
              -- A rule on an extension's view or table is the extension's, as PostGIS's three on geometry_columns are, though PostgreSQL does not
              -- record the rule itself as a member (G-274).
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = cls.oid and ext_dep.deptype = 'e')
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, cls.relname, r.rulename;
            """;

        var rules = new List<LiveRuleSnapshot>();

        await using var command = CreateCommand(ruleSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new LiveRuleSnapshot(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), EnabledStateOf(reader.GetString(4))));
        }

        return rules;
    }

    /// <summary><c>CREATE RULE</c> for a live one (G-128): <c>pg_get_ruledef</c>'s text, which is already a <c>CREATE RULE</c> statement, with one semicolon.</summary>
    internal static string BuildCreateRuleStatement(LiveRuleSnapshot rule)
        => rule.Definition.Trim().TrimEnd(';').TrimEnd() + ";"
            + (rule.EnabledState is null ? string.Empty : $"\nALTER TABLE {QualifyIdentifier(rule.SchemaName, rule.TableName)} {rule.EnabledState} RULE {QuoteIdentifier(rule.RuleName)};");

    /// <summary>
    /// The database's user-defined collations (G-148), read from <c>pg_collation</c>. The ones PostgreSQL ships, and the ones <c>pg_import_system_collations</c> makes from the
    /// operating system, have OIDs below <c>FirstNormalObjectId</c> (16384); one an extension owns (<c>deptype</c> <c>e</c>) belongs to that extension. Both are left out.
    /// A collation is in a schema, so <paramref name="scopeSchemas"/> scopes it by that. The columns that differ between PostgreSQL versions (<c>colllocale</c> from 17,
    /// <c>colliculocale</c> in 15 and 16, <c>collicurules</c> from 16) are read through <c>to_jsonb</c>, which has only the ones the server has; before 15 an ICU collation's locale is in <c>collcollate</c> (G-269).
    /// </summary>
    private static async Task<IReadOnlyList<LiveCollationSnapshot>> ReadCollationsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string collationSql =
            """
            select ns.nspname,
                   c.collname,
                   case c.collprovider::text when 'i' then 'icu' when 'b' then 'builtin' else 'libc' end,
                   -- Before PostgreSQL 15 an ICU collation keeps its locale in collcollate and collctype (G-269); it is read as the locale, as 15 and later report it.
                   case when c.collprovider::text = 'i' and to_jsonb(c) ->> 'colllocale' is null and to_jsonb(c) ->> 'colliculocale' is null then null else to_jsonb(c) ->> 'collcollate' end,
                   case when c.collprovider::text = 'i' and to_jsonb(c) ->> 'colllocale' is null and to_jsonb(c) ->> 'colliculocale' is null then null else to_jsonb(c) ->> 'collctype' end,
                   coalesce(to_jsonb(c) ->> 'colllocale', to_jsonb(c) ->> 'colliculocale', case when c.collprovider::text = 'i' then to_jsonb(c) ->> 'collcollate' end),
                   c.collisdeterministic,
                   to_jsonb(c) ->> 'collicurules',
                   pg_catalog.obj_description(c.oid, 'pg_collation')
            from pg_catalog.pg_collation c
            inner join pg_catalog.pg_namespace ns on ns.oid = c.collnamespace
            where c.oid >= 16384
              and not exists (
                      select 1
                      from pg_catalog.pg_depend dep
                      where dep.classid = 'pg_catalog.pg_collation'::regclass
                        and dep.objid = c.oid
                        and dep.deptype = 'e')
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, c.collname;
            """;

        var collations = new List<LiveCollationSnapshot>();

        await using var command = CreateCommand(collationSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            collations.Add(new LiveCollationSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                LcCollate: Text(3),
                LcCtype: Text(4),
                Locale: Text(5),
                IsDeterministic: reader.GetBoolean(6),
                Rules: Text(7),
                Comment: Text(8)));
        }

        return collations;
    }

    /// <summary>
    /// The database's text search dictionaries and configurations (G-149). The ones PostgreSQL ships have OIDs below <c>FirstNormalObjectId</c> (16384), and one an
    /// extension owns (<c>deptype</c> <c>e</c>) belongs to that extension; both are left out. They are in schemas, so <paramref name="scopeSchemas"/> scopes them by that.
    /// A dictionary is its template and <c>dictinitoption</c>, which the server stores as the option list it will parse again. A configuration is its parser and one
    /// mapping per token type and dictionary, in <c>mapseqno</c> order; the token type's name is <c>ts_token_type</c>'s alias for the configuration's parser.
    /// </summary>
    private static async Task<LiveTextSearchSnapshot> ReadTextSearchAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string dictionarySql =
            """
            select ns.nspname, d.dictname, tns.nspname, t.tmplname, coalesce(d.dictinitoption, ''), pg_catalog.obj_description(d.oid, 'pg_ts_dict')
            from pg_catalog.pg_ts_dict d
            inner join pg_catalog.pg_namespace ns on ns.oid = d.dictnamespace
            inner join pg_catalog.pg_ts_template t on t.oid = d.dicttemplate
            inner join pg_catalog.pg_namespace tns on tns.oid = t.tmplnamespace
            where d.oid >= 16384
              and not exists (select 1 from pg_catalog.pg_depend dep where dep.classid = 'pg_catalog.pg_ts_dict'::regclass and dep.objid = d.oid and dep.deptype = 'e')
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, d.dictname;
            """;
        const string configurationSql =
            """
            select ns.nspname, c.cfgname, pns.nspname, p.prsname, pg_catalog.obj_description(c.oid, 'pg_ts_config')
            from pg_catalog.pg_ts_config c
            inner join pg_catalog.pg_namespace ns on ns.oid = c.cfgnamespace
            inner join pg_catalog.pg_ts_parser p on p.oid = c.cfgparser
            inner join pg_catalog.pg_namespace pns on pns.oid = p.prsnamespace
            where c.oid >= 16384
              and not exists (select 1 from pg_catalog.pg_depend dep where dep.classid = 'pg_catalog.pg_ts_config'::regclass and dep.objid = c.oid and dep.deptype = 'e')
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, c.cfgname;
            """;
        const string mappingSql =
            """
            select ns.nspname, c.cfgname, tt.alias, m.mapseqno, dn.nspname, d.dictname
            from pg_catalog.pg_ts_config_map m
            inner join pg_catalog.pg_ts_config c on c.oid = m.mapcfg
            inner join pg_catalog.pg_namespace ns on ns.oid = c.cfgnamespace
            inner join pg_catalog.pg_ts_dict d on d.oid = m.mapdict
            inner join pg_catalog.pg_namespace dn on dn.oid = d.dictnamespace
            cross join lateral pg_catalog.ts_token_type(c.cfgparser) tt
            where tt.tokid = m.maptokentype
              and c.oid >= 16384
              and not exists (select 1 from pg_catalog.pg_depend dep where dep.classid = 'pg_catalog.pg_ts_config'::regclass and dep.objid = c.oid and dep.deptype = 'e')
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, c.cfgname, tt.alias, m.mapseqno;
            """;

        var dictionaries = new List<LiveTextSearchDictionarySnapshot>();
        await using (var command = CreateCommand(dictionarySql, connection, scopeSchemas))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                dictionaries.Add(new LiveTextSearchDictionarySnapshot(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        var mappings = new List<(string Schema, string Config, string Token, string DictionarySchema, string Dictionary)>();
        await using (var command = CreateCommand(mappingSql, connection, scopeSchemas))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                mappings.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(4), reader.GetString(5)));
            }
        }

        var configurations = new List<LiveTextSearchConfigurationSnapshot>();
        await using (var command = CreateCommand(configurationSql, connection, scopeSchemas))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                var tokens = mappings
                    .Where(mapping => mapping.Schema == schema && mapping.Config == name)
                    .GroupBy(static mapping => mapping.Token, StringComparer.Ordinal)
                    .Select(static group => new LiveTextSearchMapping(group.Key, [.. group.Select(static mapping => $"{mapping.DictionarySchema}.{mapping.Dictionary}")]))
                    .ToArray();
                configurations.Add(new LiveTextSearchConfigurationSnapshot(schema, name, reader.GetString(2), reader.GetString(3), tokens, reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        return new LiveTextSearchSnapshot(dictionaries, configurations);
    }

    /// <summary>
    /// <c>CREATE TEXT SEARCH DICTIONARY</c> and <c>CREATE TEXT SEARCH CONFIGURATION</c> with its mappings for what was read (G-149), every name quoted (a parser called
    /// <c>default</c> needs it). A configuration is made with its parser and mapped token by token, the shape <c>init</c> writes, so it never depends on a copy's source.
    /// </summary>
    internal static IEnumerable<string> BuildTextSearchStatements(LiveTextSearchSnapshot textSearch)
    {
        static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        static string Name(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

        foreach (var dictionary in textSearch.Dictionaries.OrderBy(static item => item.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.Name, StringComparer.Ordinal))
        {
            var options = string.IsNullOrWhiteSpace(dictionary.InitOption) ? string.Empty : ", " + dictionary.InitOption;
            yield return $"CREATE TEXT SEARCH DICTIONARY {Name(dictionary.SchemaName, dictionary.Name)} (TEMPLATE = {Name(dictionary.TemplateSchemaName, dictionary.TemplateName)}{options});";
            if (dictionary.Comment is not null)
            {
                yield return BuildObjectCommentStatement("TEXT SEARCH DICTIONARY", dictionary.SchemaName, dictionary.Name, dictionary.Comment);
            }
        }

        foreach (var configuration in textSearch.Configurations.OrderBy(static item => item.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.Name, StringComparer.Ordinal))
        {
            var name = Name(configuration.SchemaName, configuration.Name);
            yield return $"CREATE TEXT SEARCH CONFIGURATION {name} (PARSER = {Name(configuration.ParserSchemaName, configuration.ParserName)});";
            foreach (var mapping in configuration.Mappings.OrderBy(static item => item.Token, StringComparer.Ordinal))
            {
                var dictionaries = string.Join(", ", mapping.Dictionaries.Select(static dictionary => string.Join('.', dictionary.Split('.', 2).Select(Quote))));
                yield return $"ALTER TEXT SEARCH CONFIGURATION {name} ADD MAPPING FOR {Quote(mapping.Token)} WITH {dictionaries};";
            }

            if (configuration.Comment is not null)
            {
                yield return BuildObjectCommentStatement("TEXT SEARCH CONFIGURATION", configuration.SchemaName, configuration.Name, configuration.Comment);
            }
        }
    }

    /// <summary>
    /// The database's extended statistics (G-150), read from <c>pg_statistic_ext</c>. The definition is the server's own <c>pg_get_statisticsobjdef</c>, a complete
    /// <c>CREATE STATISTICS</c> (schema-qualified, kinds only when they are not all three, expressions in parentheses), which the parser reads like a project's. The
    /// object is in a schema, so <paramref name="scopeSchemas"/> scopes it by that; one an extension owns (<c>deptype</c> <c>e</c>) is left out.
    /// </summary>
    private static async Task<IReadOnlyList<LiveStatisticsSnapshot>> ReadStatisticsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string statisticsSql =
            """
            select ns.nspname, s.stxname, tns.nspname, t.relname, pg_catalog.pg_get_statisticsobjdef(s.oid), pg_catalog.obj_description(s.oid, 'pg_statistic_ext')
            from pg_catalog.pg_statistic_ext s
            inner join pg_catalog.pg_namespace ns on ns.oid = s.stxnamespace
            inner join pg_catalog.pg_class t on t.oid = s.stxrelid
            inner join pg_catalog.pg_namespace tns on tns.oid = t.relnamespace
            where s.oid >= 16384
              and not exists (
                      select 1
                      from pg_catalog.pg_depend dep
                      where dep.classid = 'pg_catalog.pg_statistic_ext'::regclass
                        and dep.objid = s.oid
                        and dep.deptype = 'e')
              -- Statistics on an extension's table are the extension's (G-274).
              and not exists (select 1 from pg_catalog.pg_depend ext_dep where ext_dep.classid = 'pg_catalog.pg_class'::regclass and ext_dep.objid = t.oid and ext_dep.deptype = 'e')
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, s.stxname;
            """;

        var statistics = new List<LiveStatisticsSnapshot>();

        await using var command = CreateCommand(statisticsSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            statistics.Add(new LiveStatisticsSnapshot(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return statistics;
    }

    /// <summary><c>COMMENT ON keyword schema.name IS 'text'</c> for a live object (G-154).</summary>
    internal static string BuildObjectCommentStatement(string keyword, string schemaName, string name, string comment)
        => $"COMMENT ON {keyword} {QualifyIdentifier(schemaName, name)} IS {QuoteLiteral(comment)};";

    /// <summary>
    /// <c>CREATE COLLATION</c> for a live one (G-148), the same shape <c>CreateOperation.GenerateCreateCollation</c> writes for a project's: an ICU or builtin collation by its
    /// locale, a libc one by <c>LC_COLLATE</c> and <c>LC_CTYPE</c>; <c>DETERMINISTIC = false</c> and the rules only when they are not the defaults.
    /// </summary>
    internal static string BuildCreateCollationStatement(LiveCollationSnapshot collation)
    {
        var options = new List<string> { $"PROVIDER = {collation.Provider}" };
        // An ICU collation on the root locale has an empty one before PostgreSQL 16, and 14 and 15 refuse a collation that states none (G-298).
        if (!string.IsNullOrEmpty(collation.Locale) || (collation.Locale is not null && collation.Provider != "libc"))
        {
            options.Add($"LOCALE = {QuoteLiteral(collation.Locale)}");
        }

        if (collation.Provider == "libc")
        {
            if (!string.IsNullOrEmpty(collation.LcCollate))
            {
                options.Add($"LC_COLLATE = {QuoteLiteral(collation.LcCollate)}");
            }

            if (!string.IsNullOrEmpty(collation.LcCtype))
            {
                options.Add($"LC_CTYPE = {QuoteLiteral(collation.LcCtype)}");
            }
        }

        if (!collation.IsDeterministic)
        {
            options.Add("DETERMINISTIC = false");
        }

        if (!string.IsNullOrEmpty(collation.Rules))
        {
            options.Add($"RULES = {QuoteLiteral(collation.Rules)}");
        }

        return $"CREATE COLLATION {QualifyIdentifier(collation.SchemaName, collation.Name)} ({string.Join(", ", options)});";
    }

    /// <summary>
    /// The database's user-defined aggregates (G-130), read from <c>pg_aggregate</c>. The ones PostgreSQL ships have OIDs below <c>FirstNormalObjectId</c> (16384), and
    /// one an extension owns (<c>deptype</c> <c>e</c>) belongs to that extension; both are left out. An aggregate is in a schema, so <paramref name="scopeSchemas"/>
    /// scopes it by that. The functions and the sort operator are read as a schema and a name (not <c>regproc</c> text, which quotes a name that needs it and would not
    /// match the one a project writes), and the argument list is <c>pg_get_function_identity_arguments</c>, which spells out <c>ORDER BY</c> for an ordered-set aggregate.
    /// Each function is joined by its OID to <c>pg_proc</c> and <c>pg_namespace</c> (G-329): through a CTE of every function, joined eight times, PostgreSQL 14 to 16 had no
    /// statistics for its rows and estimated the query at a million rows, past the JIT thresholds.
    /// </summary>
    private static async Task<IReadOnlyList<LiveAggregateSnapshot>> ReadAggregatesAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string aggregateSql =
            """
            select ns.nspname, p.proname, pg_catalog.pg_get_function_identity_arguments(p.oid), a.aggkind::text, a.aggnumdirectargs::int,
                   fns_trans.nspname, f_trans.proname, pg_catalog.format_type(a.aggtranstype, null), a.aggtransspace,
                   fns_final.nspname, f_final.proname, a.aggfinalextra, a.aggfinalmodify::text,
                   fns_comb.nspname, f_comb.proname, fns_ser.nspname, f_ser.proname, fns_deser.nspname, f_deser.proname,
                   a.agginitval,
                   fns_mtrans.nspname, f_mtrans.proname, fns_minv.nspname, f_minv.proname,
                   case when a.aggmtranstype = 0 then null else pg_catalog.format_type(a.aggmtranstype, null) end, a.aggmtransspace,
                   fns_mfinal.nspname, f_mfinal.proname, a.aggmfinalextra, a.aggmfinalmodify::text,
                   a.aggminitval,
                   sortns.nspname, sortop.oprname,
                   p.proparallel::text
            from pg_catalog.pg_aggregate a
            inner join pg_catalog.pg_proc p on p.oid = a.aggfnoid
            inner join pg_catalog.pg_namespace ns on ns.oid = p.pronamespace
            left join pg_catalog.pg_proc f_trans on f_trans.oid = a.aggtransfn::pg_catalog.oid
            left join pg_catalog.pg_namespace fns_trans on fns_trans.oid = f_trans.pronamespace
            left join pg_catalog.pg_proc f_final on f_final.oid = a.aggfinalfn::pg_catalog.oid
            left join pg_catalog.pg_namespace fns_final on fns_final.oid = f_final.pronamespace
            left join pg_catalog.pg_proc f_comb on f_comb.oid = a.aggcombinefn::pg_catalog.oid
            left join pg_catalog.pg_namespace fns_comb on fns_comb.oid = f_comb.pronamespace
            left join pg_catalog.pg_proc f_ser on f_ser.oid = a.aggserialfn::pg_catalog.oid
            left join pg_catalog.pg_namespace fns_ser on fns_ser.oid = f_ser.pronamespace
            left join pg_catalog.pg_proc f_deser on f_deser.oid = a.aggdeserialfn::pg_catalog.oid
            left join pg_catalog.pg_namespace fns_deser on fns_deser.oid = f_deser.pronamespace
            left join pg_catalog.pg_proc f_mtrans on f_mtrans.oid = a.aggmtransfn::pg_catalog.oid
            left join pg_catalog.pg_namespace fns_mtrans on fns_mtrans.oid = f_mtrans.pronamespace
            left join pg_catalog.pg_proc f_minv on f_minv.oid = a.aggminvtransfn::pg_catalog.oid
            left join pg_catalog.pg_namespace fns_minv on fns_minv.oid = f_minv.pronamespace
            left join pg_catalog.pg_proc f_mfinal on f_mfinal.oid = a.aggmfinalfn::pg_catalog.oid
            left join pg_catalog.pg_namespace fns_mfinal on fns_mfinal.oid = f_mfinal.pronamespace
            left join pg_catalog.pg_operator sortop on sortop.oid = a.aggsortop and a.aggsortop <> 0
            left join pg_catalog.pg_namespace sortns on sortns.oid = sortop.oprnamespace
            where p.oid >= 16384
              and not exists (
                      select 1
                      from pg_catalog.pg_depend dep
                      where dep.classid = 'pg_catalog.pg_proc'::regclass
                        and dep.objid = p.oid
                        and dep.deptype = 'e')
              and (@schemas is null or ns.nspname = any(@schemas))
            order by ns.nspname, p.proname, 3;
            """;

        var aggregates = new List<LiveAggregateSnapshot>();

        await using var command = CreateCommand(aggregateSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            string? Function(int schemaOrdinal) => Text(schemaOrdinal + 1) is { } name ? QualifyIdentifier(Text(schemaOrdinal), name) : null;

            var kind = reader.GetString(3);
            aggregates.Add(new LiveAggregateSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                kind,
                reader.GetInt32(4),
                StateFunction: Function(5) ?? string.Empty,
                StateType: reader.GetString(7),
                StateSpace: reader.GetInt32(8),
                FinalFunction: Function(9),
                FinalExtra: reader.GetBoolean(11),
                FinalModify: reader.GetString(12),
                CombineFunction: Function(13),
                SerialFunction: Function(15),
                DeserialFunction: Function(17),
                InitialValue: Text(19),
                MovingStateFunction: Function(20),
                MovingInverseFunction: Function(22),
                MovingStateType: Text(24),
                MovingStateSpace: reader.GetInt32(25),
                MovingFinalFunction: Function(26),
                MovingFinalExtra: reader.GetBoolean(28),
                MovingFinalModify: reader.GetString(29),
                MovingInitialValue: Text(30),
                SortOperator: Text(32) is { } sortName ? QualifyOperator(Text(31), sortName) : null,
                Parallel: reader.GetString(33)));
        }

        return aggregates;
    }

    /// <summary>
    /// <c>CREATE AGGREGATE</c> for a live one (G-130), the same shape <c>CreateOperation.GenerateCreateAggregate</c> writes for a project's. An option that is
    /// PostgreSQL's default is left out (<c>PARALLEL = UNSAFE</c>, <c>FINALFUNC_MODIFY</c> when it is what the kind of aggregate gets), and an ordered-set aggregate
    /// keeps the <c>ORDER BY</c> that <c>pg_get_function_identity_arguments</c> writes; <c>aggkind</c> <c>h</c> is <c>HYPOTHETICAL</c>.
    /// </summary>
    internal static string BuildCreateAggregateStatement(LiveAggregateSnapshot aggregate)
    {
        var defaultModify = aggregate.Kind == "n" ? "r" : "w";
        static string Modify(string modify) => modify switch { "s" => "SHAREABLE", "w" => "READ_WRITE", _ => "READ_ONLY" };

        var options = new List<string> { $"SFUNC = {aggregate.StateFunction}", $"STYPE = {aggregate.StateType}" };
        if (aggregate.StateSpace > 0)
        {
            options.Add($"SSPACE = {aggregate.StateSpace}");
        }

        if (aggregate.FinalFunction is not null)
        {
            options.Add($"FINALFUNC = {aggregate.FinalFunction}");
        }

        if (aggregate.FinalExtra)
        {
            options.Add("FINALFUNC_EXTRA");
        }

        if (aggregate.FinalModify != defaultModify)
        {
            options.Add($"FINALFUNC_MODIFY = {Modify(aggregate.FinalModify)}");
        }

        foreach (var (name, function) in new[] { ("COMBINEFUNC", aggregate.CombineFunction), ("SERIALFUNC", aggregate.SerialFunction), ("DESERIALFUNC", aggregate.DeserialFunction) })
        {
            if (function is not null)
            {
                options.Add($"{name} = {function}");
            }
        }

        if (aggregate.InitialValue is not null)
        {
            options.Add($"INITCOND = {QuoteLiteral(aggregate.InitialValue)}");
        }

        if (aggregate.MovingStateFunction is not null)
        {
            options.Add($"MSFUNC = {aggregate.MovingStateFunction}");
        }

        if (aggregate.MovingInverseFunction is not null)
        {
            options.Add($"MINVFUNC = {aggregate.MovingInverseFunction}");
        }

        if (aggregate.MovingStateType is not null)
        {
            options.Add($"MSTYPE = {aggregate.MovingStateType}");
        }

        if (aggregate.MovingStateSpace > 0)
        {
            options.Add($"MSSPACE = {aggregate.MovingStateSpace}");
        }

        if (aggregate.MovingFinalFunction is not null)
        {
            options.Add($"MFINALFUNC = {aggregate.MovingFinalFunction}");
        }

        if (aggregate.MovingFinalExtra)
        {
            options.Add("MFINALFUNC_EXTRA");
        }

        if (aggregate.MovingStateFunction is not null && aggregate.MovingFinalModify != defaultModify)
        {
            options.Add($"MFINALFUNC_MODIFY = {Modify(aggregate.MovingFinalModify)}");
        }

        if (aggregate.MovingInitialValue is not null)
        {
            options.Add($"MINITCOND = {QuoteLiteral(aggregate.MovingInitialValue)}");
        }

        if (aggregate.SortOperator is not null)
        {
            options.Add($"SORTOP = OPERATOR({aggregate.SortOperator})");
        }

        if (aggregate.Parallel != "u")
        {
            options.Add($"PARALLEL = {(aggregate.Parallel == "s" ? "SAFE" : "RESTRICTED")}");
        }

        if (aggregate.Kind == "h")
        {
            options.Add("HYPOTHETICAL");
        }

        var arguments = string.IsNullOrWhiteSpace(aggregate.Arguments) ? "*" : aggregate.Arguments;
        return $"CREATE AGGREGATE {QualifyIdentifier(aggregate.SchemaName, aggregate.Name)} ({arguments}) ({string.Join(", ", options)});";
    }

    /// <summary>
    /// The database's publications (G-134), read from <c>pg_publication</c> with the tables (<c>pg_publication_rel</c>: the column list and the row filter as <c>pg_get_expr</c>
    /// prints it) and schemas (<c>pg_publication_namespace</c>) they name, and the operations they publish. One an extension owns (<c>deptype</c> <c>e</c>) belongs to that
    /// extension and is left out. A publication belongs to no schema, so <paramref name="scopeSchemas"/> scopes it by what it names: it is in scope when one of its tables
    /// or schemas is in a listed schema, or when it names nothing (<c>FOR ALL TABLES</c>, or an empty one, which no schema list can say anything about).
    /// </summary>
    private static async Task<IReadOnlyList<LivePublicationSnapshot>> ReadPublicationsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string publicationSql =
            """
            select p.pubname, p.puballtables, p.pubinsert, p.pubupdate, p.pubdelete, p.pubtruncate, p.pubviaroot,
                   coalesce(pg_catalog.to_jsonb(p) ->> 'pubgencols', 'n') as generated_columns,
                   coalesce((
                       select pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
                                  'schema', ns.nspname,
                                  'table', c.relname,
                                  'columns', (select coalesce(pg_catalog.jsonb_agg(a.attname order by a.attnum), '[]'::pg_catalog.jsonb)
                                              from pg_catalog.pg_attribute a
                                              where a.attrelid = c.oid and a.attnum = any(pr.prattrs)),
                                  'filter', pg_catalog.pg_get_expr(pr.prqual, pr.prrelid)) order by ns.nspname, c.relname)
                       from pg_catalog.pg_publication_rel pr
                       inner join pg_catalog.pg_class c on c.oid = pr.prrelid
                       inner join pg_catalog.pg_namespace ns on ns.oid = c.relnamespace
                       where pr.prpubid = p.oid), '[]'::pg_catalog.jsonb)::text as tables,
                   coalesce((
                       select pg_catalog.jsonb_agg(ns.nspname order by ns.nspname)
                       from pg_catalog.pg_publication_namespace pn
                       inner join pg_catalog.pg_namespace ns on ns.oid = pn.pnnspid
                       where pn.pnpubid = p.oid), '[]'::pg_catalog.jsonb)::text as schemas
            from pg_catalog.pg_publication p
            where not exists (
                      select 1
                      from pg_catalog.pg_depend dep
                      where dep.classid = 'pg_catalog.pg_publication'::regclass
                        and dep.objid = p.oid
                        and dep.deptype = 'e')
              and (@schemas is null
                   or (not exists (select 1 from pg_catalog.pg_publication_rel r where r.prpubid = p.oid)
                       and not exists (select 1 from pg_catalog.pg_publication_namespace n where n.pnpubid = p.oid))
                   or exists (select 1
                              from pg_catalog.pg_publication_rel r
                              inner join pg_catalog.pg_class c on c.oid = r.prrelid
                              inner join pg_catalog.pg_namespace ns on ns.oid = c.relnamespace
                              where r.prpubid = p.oid and ns.nspname = any(@schemas))
                   or exists (select 1
                              from pg_catalog.pg_publication_namespace n
                              inner join pg_catalog.pg_namespace ns on ns.oid = n.pnnspid
                              where n.pnpubid = p.oid and ns.nspname = any(@schemas)))
            order by p.pubname;
            """;

        // PostgreSQL 14 has no column lists, row filters, or schemas in a publication (all 15, G-269): a table is read without them and there are no schemas.
        const string publicationSql14 =
            """
            select p.pubname, p.puballtables, p.pubinsert, p.pubupdate, p.pubdelete, p.pubtruncate, p.pubviaroot,
                   coalesce(pg_catalog.to_jsonb(p) ->> 'pubgencols', 'n') as generated_columns,
                   coalesce((
                       select pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
                                  'schema', ns.nspname,
                                  'table', c.relname,
                                  'columns', '[]'::pg_catalog.jsonb,
                                  'filter', null) order by ns.nspname, c.relname)
                       from pg_catalog.pg_publication_rel pr
                       inner join pg_catalog.pg_class c on c.oid = pr.prrelid
                       inner join pg_catalog.pg_namespace ns on ns.oid = c.relnamespace
                       where pr.prpubid = p.oid), '[]'::pg_catalog.jsonb)::text as tables,
                   '[]' as schemas
            from pg_catalog.pg_publication p
            where not exists (
                      select 1
                      from pg_catalog.pg_depend dep
                      where dep.classid = 'pg_catalog.pg_publication'::regclass
                        and dep.objid = p.oid
                        and dep.deptype = 'e')
              and (@schemas is null
                   or not exists (select 1 from pg_catalog.pg_publication_rel r where r.prpubid = p.oid)
                   or exists (select 1
                              from pg_catalog.pg_publication_rel r
                              inner join pg_catalog.pg_class c on c.oid = r.prrelid
                              inner join pg_catalog.pg_namespace ns on ns.oid = c.relnamespace
                              where r.prpubid = p.oid and ns.nspname = any(@schemas)))
            order by p.pubname;
            """;

        var publications = new List<LivePublicationSnapshot>();

        await using var command = CreateCommand(connection.PostgreSqlVersion.Major >= 15 ? publicationSql : publicationSql14, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var tables = new List<LivePublicationTable>();
            using (var tableDocument = System.Text.Json.JsonDocument.Parse(reader.GetString(8)))
            {
                foreach (var table in tableDocument.RootElement.EnumerateArray())
                {
                    tables.Add(new LivePublicationTable(
                        table.GetProperty("schema").GetString()!,
                        table.GetProperty("table").GetString()!,
                        [.. table.GetProperty("columns").EnumerateArray().Select(static column => column.GetString()!)],
                        table.TryGetProperty("filter", out var filter) && filter.ValueKind == System.Text.Json.JsonValueKind.String ? filter.GetString() : null));
                }
            }

            using var schemaDocument = System.Text.Json.JsonDocument.Parse(reader.GetString(9));
            publications.Add(new LivePublicationSnapshot(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.GetString(7),
                tables,
                [.. schemaDocument.RootElement.EnumerateArray().Select(static schema => schema.GetString()!)]));
        }

        return publications;
    }

    /// <summary>
    /// <c>CREATE PUBLICATION</c> for a live one (G-134), the same shape <c>CreateOperation.GenerateCreatePublication</c> writes for a project's. An option that is
    /// PostgreSQL's default is left out (<c>publish</c> when it is all four operations, <c>publish_via_partition_root</c> when false, <c>publish_generated_columns</c> when none).
    /// </summary>
    internal static string BuildCreatePublicationStatement(LivePublicationSnapshot publication)
    {
        var sql = $"CREATE PUBLICATION {RenderIdentifier(publication.Name)}";
        if (publication.AllTables)
        {
            sql += " FOR ALL TABLES";
        }
        else if (publication.Tables.Count > 0 || publication.Schemas.Count > 0)
        {
            var members = new List<string>();
            if (publication.Tables.Count > 0)
            {
                members.Add("TABLE " + string.Join(", ", publication.Tables.Select(static table =>
                {
                    var text = QualifyIdentifier(table.SchemaName, table.TableName);
                    if (table.Columns.Count > 0)
                    {
                        text += " (" + string.Join(", ", table.Columns.Select(RenderIdentifier)) + ")";
                    }

                    return table.Filter is null ? text : $"{text} WHERE ({table.Filter})";
                })));
            }

            members.AddRange(publication.Schemas.Select(static schema => "TABLES IN SCHEMA " + RenderIdentifier(schema)));
            sql += " FOR " + string.Join(", ", members);
        }

        var operations = new List<string>();
        foreach (var (published, name) in new[] { (publication.Insert, "insert"), (publication.Update, "update"), (publication.Delete, "delete"), (publication.Truncate, "truncate") })
        {
            if (published)
            {
                operations.Add(name);
            }
        }

        var options = new List<string>();
        if (operations.Count != 4)
        {
            options.Add($"publish = {QuoteLiteral(string.Join(", ", operations))}");
        }

        if (publication.ViaRoot)
        {
            options.Add("publish_via_partition_root = true");
        }

        if (publication.GeneratedColumns == "s")
        {
            options.Add("publish_generated_columns = stored");
        }

        return options.Count == 0 ? sql + ";" : $"{sql} WITH ({string.Join(", ", options)});";
    }

    /// <summary>
    /// The database's user-defined casts (G-131), read from <c>pg_cast</c>. The ones PostgreSQL itself ships have OIDs below
    /// <c>FirstNormalObjectId</c> (16384); one an extension owns (<c>deptype</c> <c>e</c>) belongs to that extension; and one PostgreSQL makes along with a
    /// type (<c>deptype</c> <c>i</c>: a range type gets a cast to its multirange type) is part of that type, so it comes back when the type does. All three are left out. A
    /// cast has no schema, so <paramref name="scopeSchemas"/> scopes it by the schemas of the function it runs and of the two types it converts
    /// (a cast between built-in types with no function has none of these, and belongs to no project that lists <c>schemas</c>).
    /// </summary>
    private static async Task<IReadOnlyList<LiveCastSnapshot>> ReadCastsAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeSchemas, CancellationToken cancellationToken)
    {
        const string castSql =
            """
            select pg_catalog.format_type(c.castsource, null) as source_type,
                   pg_catalog.format_type(c.casttarget, null) as target_type,
                   fns.nspname as function_schema,
                   fn.proname as function_name,
                   pg_catalog.pg_get_function_identity_arguments(fn.oid) as function_arguments,
                   c.castcontext::text as context,
                   c.castmethod::text as method
            from pg_catalog.pg_cast c
            inner join pg_catalog.pg_type st on st.oid = c.castsource
            inner join pg_catalog.pg_namespace sns on sns.oid = st.typnamespace
            inner join pg_catalog.pg_type tt on tt.oid = c.casttarget
            inner join pg_catalog.pg_namespace tns on tns.oid = tt.typnamespace
            left join pg_catalog.pg_proc fn on fn.oid = c.castfunc and c.castfunc <> 0
            left join pg_catalog.pg_namespace fns on fns.oid = fn.pronamespace
            where c.oid >= 16384
              and not exists (
                      select 1
                      from pg_catalog.pg_depend dep
                      where dep.classid = 'pg_catalog.pg_cast'::regclass
                        and dep.objid = c.oid
                        and dep.deptype in ('e', 'i'))
              and (@schemas is null or fns.nspname = any(@schemas) or sns.nspname = any(@schemas) or tns.nspname = any(@schemas))
            order by source_type, target_type;
            """;

        var casts = new List<LiveCastSnapshot>();

        await using var command = CreateCommand(castSql, connection, scopeSchemas);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            casts.Add(new LiveCastSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }

        return casts;
    }

    /// <summary>
    /// <c>CREATE CAST</c> for a live one (G-131), the same shape <c>CreateOperation.GenerateCreateCast</c> writes for a project's: <c>WITH
    /// FUNCTION</c>, <c>WITHOUT FUNCTION</c> (<c>castmethod</c> <c>b</c>), or <c>WITH INOUT</c> (<c>i</c>), and <c>AS ASSIGNMENT</c> or <c>AS IMPLICIT</c>
    /// from <c>castcontext</c> <c>a</c>/<c>i</c> (<c>e</c>, explicit only, is what omitting it means).
    /// </summary>
    internal static string BuildCreateCastStatement(LiveCastSnapshot cast)
    {
        var method = cast.Method switch
        {
            "i" => "WITH INOUT",
            "b" => "WITHOUT FUNCTION",
            _ => $"WITH FUNCTION {QualifyIdentifier(cast.FunctionSchema, cast.FunctionName ?? string.Empty)}({cast.FunctionArguments})",
        };
        var context = cast.Context switch
        {
            "i" => " AS IMPLICIT",
            "a" => " AS ASSIGNMENT",
            _ => string.Empty,
        };

        return $"CREATE CAST ({cast.SourceType} AS {cast.TargetType}) {method}{context};";
    }

    private static readonly System.Text.RegularExpressions.Regex ExecuteFunctionClauseRegex = new(
        @"(?<prefix>EXECUTE\s+(?:FUNCTION|PROCEDURE)\s+)(?<name>(?:""[^""]*""|[^\s(""])+)\(",
        System.Text.RegularExpressions.RegexOptions.RightToLeft | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A primary key's or unique constraint's definition with its index's storage parameters (G-195), which <c>pg_get_constraintdef</c> does not print. The
    /// <c>WITH (...)</c> goes where the grammar has it: after the columns and any <c>INCLUDE</c>, before <c>USING INDEX TABLESPACE</c> and <c>DEFERRABLE</c>.
    /// </summary>
    internal static string WithIndexOptions(string definition, string? indexOptions)
    {
        if (string.IsNullOrWhiteSpace(indexOptions))
        {
            return definition;
        }

        var position = new[] { " USING INDEX TABLESPACE ", " DEFERRABLE", " NOT DEFERRABLE", " INITIALLY " }
            .Select(keyword => definition.IndexOf(keyword, StringComparison.Ordinal))
            .Where(static index => index >= 0)
            .DefaultIfEmpty(definition.Length)
            .Min();
        return definition[..position] + $" WITH ({indexOptions})" + definition[position..];
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    internal static string RenderIdentifier(string identifier)
        // Quoted unless it is plain lower case and not a reserved word (a column named "order" must stay quoted) (G-70).
        => SqlIdentifiers.QuoteName(identifier);

    private static string RenderGrantee(string grantee)
        => string.Equals(grantee, "PUBLIC", StringComparison.OrdinalIgnoreCase)
            ? "PUBLIC"
            : RenderIdentifier(grantee);


    internal static string QualifyIdentifierForViewPlaceholder(string? schemaName, string name) => QualifyIdentifier(schemaName, name);

    private static string QualifyIdentifier(string? schemaName, string name)
        => string.IsNullOrWhiteSpace(schemaName)
            ? RenderIdentifier(name)
            : $"{RenderIdentifier(schemaName)}.{RenderIdentifier(name)}";

    internal static string QuoteLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}

internal sealed record LiveTableSnapshot(
    string SchemaName,
    string TableName,
    IReadOnlyList<LiveColumnSnapshot> Columns,
    string? Comment = null,
    bool IsUnlogged = false,
    string? TablespaceName = null,
    IReadOnlyList<string>? StorageOptions = null,
    string? PartitionKeyDef = null,
    string? PartitionOfSchemaName = null,
    string? PartitionOfTableName = null,
    string? PartitionBoundRaw = null,
    bool IsRowSecurityEnabled = false,
    bool IsRowSecurityForced = false,
    string? OfTypeSchemaName = null,
    string? OfTypeName = null,
    IReadOnlyList<string>? InheritsFrom = null)
{
    /// <summary>The table's access method; null for a partitioned table that has none (G-223).</summary>
    public string? AccessMethod { get; init; }

    /// <summary>True when <see cref="AccessMethod"/> is the server's <c>default_table_access_method</c>, which a scaffold then leaves unwritten (G-223).</summary>
    public bool IsDefaultAccessMethod { get; init; }

    /// <summary>The table's replica identity when it is <c>FULL</c> or <c>NOTHING</c> (G-255); <c>USING INDEX</c> is the index's statement, and the default needs none.</summary>
    public string? ReplicaIdentity { get; init; }

    /// <summary>The <c>tgenabled</c> code of each of the table's internal (foreign-key) triggers (G-261).</summary>
    public IReadOnlyList<string> InternalTriggerStates { get; init; } = [];

    /// <summary>
    /// <c>DISABLE</c> when most of the table's internal triggers are disabled, which <c>ALTER TABLE ... DISABLE TRIGGER ALL</c> states (G-261); null otherwise (enabled is the default). A table
    /// whose internal triggers are in mixed states, or in a state <c>TRIGGER ALL</c> cannot state (replica, always), is written as its most common state, and <see cref="InternalTriggerStatesAreMixed"/> says so.
    /// </summary>
    public string? InternalTriggerState => MostCommonInternalState() == "D" ? "DISABLE" : null;

    /// <summary>True when the internal triggers are not all in one state <c>ENABLE | DISABLE TRIGGER ALL</c> can state, so what is written is not exactly what the database has (G-261).</summary>
    public bool InternalTriggerStatesAreMixed => InternalTriggerStates.Distinct(StringComparer.Ordinal).Count() > 1 || MostCommonInternalState() is "R" or "A";

    private string? MostCommonInternalState()
        => InternalTriggerStates.Count == 0
            ? null
            : InternalTriggerStates.GroupBy(static state => state, StringComparer.Ordinal)
                .OrderByDescending(static group => group.Count()).ThenBy(static group => group.Key == "O" ? 0 : 1).First().Key;
}

internal sealed record LiveSchemaSnapshot(string SchemaName, string? Comment = null)
{
    /// <summary>The text PostgreSQL gives the <c>public</c> schema when a database is created.</summary>
    internal const string DefaultPublicComment = "standard public schema";

    /// <summary>
    /// The comment someone put on the schema, or null: none, or the one every new database has on <c>public</c>, which says nothing about this database (G-160).
    /// A project that does not mention a schema's comment is not compared against it either way, so leaving the default out only keeps a scaffold and a document free of it.
    /// </summary>
    internal string? StatedComment
        => string.IsNullOrEmpty(Comment) || (string.Equals(SchemaName, "public", StringComparison.OrdinalIgnoreCase) && string.Equals(Comment, DefaultPublicComment, StringComparison.Ordinal))
            ? null
            : Comment;
}

internal sealed record LiveColumnSnapshot(
    string ColumnName,
    string DataType,
    bool IsNullable,
    string? DefaultExpression,
    bool IsGenerated,
    string? GeneratedExpression,
    string? CollationName,
    string? IdentityGeneration = null,
    string? Comment = null,
    bool IsVirtual = false,
    string? NotValidNotNullName = null,
    bool IsLocal = true)
{
    /// <summary>An identity column's options, as <see cref="LiveSequenceSnapshot.IdentityOptionsText"/> writes them (G-214).</summary>
    internal string? IdentityOptions { get; init; }

    /// <summary>The column's storage (<c>plain</c>, <c>external</c>, <c>main</c>, <c>extended</c>) (G-222).</summary>
    internal string? Storage { get; init; }

    /// <summary>True when <see cref="Storage"/> is the column's type's, which a scaffold then leaves unwritten (G-222).</summary>
    internal bool IsDefaultStorage { get; init; }

    /// <summary>The column's own compression (<c>pglz</c>, <c>lz4</c>); null when it has none (G-222).</summary>
    internal string? Compression { get; init; }

    /// <summary>True when the column's own NOT NULL constraint is <c>NO INHERIT</c> (PostgreSQL 18; G-253).</summary>
    internal bool NotNullNoInherit { get; init; }

    /// <summary>True when the column's NOT NULL is only a parent's (PostgreSQL 18): the child's own declaration of the column does not repeat it (G-253).</summary>
    internal bool NotNullIsInheritedOnly { get; init; }

    /// <summary>True when this child's default for a column it only inherits is its own, not the parent's (G-237).</summary>
    internal bool OverridesInheritedDefault { get; init; }

    /// <summary>The name of a partition column's NOT NULL constraint (PostgreSQL 18; G-264); null for a column of another kind of table or one without it.</summary>
    internal string? PartitionNotNullName { get; init; }

    /// <summary>True when <see cref="PartitionNotNullName"/> is not the parent's: <c>PARTITION OF</c> would give it the parent's, so the partition is written with its columns and attached (G-264).</summary>
    internal bool PartitionNotNullDiffers { get; init; }

    /// <summary>True when the partition's columns are in another order than its parent's (G-297): it is written with its columns and attached, as for <see cref="PartitionNotNullDiffers"/>.</summary>
    internal bool PartitionOrderDiffers { get; init; }

    /// <summary>True for a partition column that is NOT NULL while its parent's is not (G-266): written <c>col NOT NULL</c> in the partition's element list.</summary>
    internal bool PartitionAddsNotNull { get; init; }

    /// <summary>The name of the column's NOT NULL constraint (PostgreSQL 18; G-235); null before 18 and for a nullable column.</summary>
    internal string? NotNullName { get; init; }

    /// <summary>The comment on the column's NOT NULL constraint (G-235).</summary>
    internal string? NotNullComment { get; init; }

    /// <summary>
    /// True when <see cref="NotNullName"/> is the one PostgreSQL gives a NOT NULL by itself, <c>&lt;table&gt;_&lt;column&gt;_not_null</c>, which a scaffold leaves unwritten unless the constraint
    /// has a comment to attach to it (G-235).
    /// </summary>
    internal bool HasDefaultNotNullName(string tableName) => string.Equals(NotNullName, $"{tableName}_{ColumnName}_not_null", StringComparison.Ordinal);

    /// <summary><c>GENERATED ... AS IDENTITY</c> with its options, or null for a column that is not an identity.</summary>
    internal string? IdentityClause
        => string.IsNullOrWhiteSpace(IdentityGeneration)
            ? null
            : $"GENERATED {IdentityGeneration} AS IDENTITY{(IdentityOptions is null ? string.Empty : $" ({IdentityOptions})")}";
}

internal sealed record LiveConstraintSnapshot(string SchemaName, string TableName, string ConstraintName, string ConstraintDefinition, string? Comment = null, string? CheckExpression = null, string? IndexOptions = null, bool IsClustered = false, bool IsReplicaIdentity = false)
{
    /// <summary>The comment on the index of a primary key, unique, or exclusion constraint, which has the constraint's name (G-295); null when it has none.</summary>
    public string? IndexComment { get; init; }
}

internal sealed record LiveIndexSnapshot(string SchemaName, string TableName, string IndexName, string IndexDefinition, string? Comment = null, string? Predicate = null, bool IsClustered = false, bool IsReplicaIdentity = false, bool IsValid = true);

internal sealed record LiveViewSnapshot(string SchemaName, string ViewName, string ViewDefinition, bool IsMaterialized = false, string? Comment = null, IReadOnlyList<string>? Options = null)
{
    /// <summary>The view's columns' comments, each a <c>COMMENT ON COLUMN</c> statement (G-216).</summary>
    public IReadOnlyList<string> ColumnCommentStatements { get; init; } = [];

    /// <summary>The defaults of the view's columns, each an <c>ALTER VIEW ... ALTER COLUMN ... SET DEFAULT</c> statement (G-291).</summary>
    public IReadOnlyList<string> ColumnDefaultStatements { get; init; } = [];

    /// <summary>A materialized view's storage parameters as PostgreSQL stores them (<c>fillfactor=50</c>) (G-215).</summary>
    public IReadOnlyList<string> StorageOptions { get; init; } = [];

    /// <summary>False for a materialized view that has not been populated (created <c>WITH NO DATA</c> and not refreshed since), which is scripted so (G-215).</summary>
    public bool IsPopulated { get; init; } = true;

    /// <summary>A materialized view's own tablespace; null when it is in the database's default one (G-219).</summary>
    public string? TablespaceName { get; init; }

    /// <summary>A materialized view's access method (G-219).</summary>
    public string? AccessMethod { get; init; }

    /// <summary>
    /// The columns (name and type) of a view that is created from a placeholder because it is part of a cycle of views (G-254); null for every other view.
    /// </summary>
    public IReadOnlyList<(string Name, string Type)>? PlaceholderColumns { get; init; }

    /// <summary>
    /// <c>CREATE VIEW v AS SELECT NULL::type AS column, ...;</c>, the first definition of a view in a cycle of views (G-254), with a line break after it; empty for any other view.
    /// </summary>
    public string PlaceholderStatement
        => PlaceholderColumns is not { Count: > 0 } columns
            ? string.Empty
            : $"CREATE VIEW {LiveDatabaseModelLoader.QualifyIdentifierForViewPlaceholder(SchemaName, ViewName)} AS SELECT {string.Join(", ", columns.Select(static column => $"NULL::{column.Type} AS {LiveDatabaseModelLoader.RenderIdentifier(column.Name)}"))};{Environment.NewLine}";

    /// <summary>True when <see cref="AccessMethod"/> is the server's <c>default_table_access_method</c>, which a scaffold then leaves unwritten (G-219).</summary>
    public bool IsDefaultAccessMethod { get; init; }

    /// <summary>
    /// What a scripted <c>CREATE VIEW</c> has between its name and <c>AS</c> (with its leading space): a materialized view's <c>USING</c>, its <c>WITH (...)</c>, and its
    /// <c>TABLESPACE</c> (G-180, G-215, G-219). The database read always states the access method, since the project's is compared when it states one; a scaffold
    /// (<paramref name="scaffold"/>) states it only when it is not the server's default.
    /// </summary>
    public string HeaderClause(bool scaffold)
        => IsMaterialized
            ? (AccessMethod is not null && !(scaffold && IsDefaultAccessMethod) ? $" USING {LiveDatabaseModelLoader.RenderIdentifier(AccessMethod)}" : string.Empty)
                + (StorageOptions.Count == 0 ? string.Empty : $" WITH ({string.Join(", ", StorageOptions)})")
                + (TablespaceName is null ? string.Empty : $" TABLESPACE {LiveDatabaseModelLoader.RenderIdentifier(TablespaceName)}")
            : Options is null || ViewOptions.HeaderOptions(Options).ToArray() is not { Length: > 0 } header ? string.Empty : $" WITH ({string.Join(", ", header)})";

    /// <summary>The scripted query, followed by <c>WITH [LOCAL | CASCADED] CHECK OPTION</c> when the view has one, which <c>pg_get_viewdef</c> leaves out (G-180).</summary>
    public string ScriptedQuery
        => ViewDefinition.Trim().TrimEnd(';')
            + (Options is not null && ViewOptions.CheckOptionOf(Options) is { } check ? $" WITH {check} CHECK OPTION" : string.Empty)
            + (IsMaterialized && !IsPopulated ? " WITH NO DATA" : string.Empty);
}

internal sealed record LiveFunctionSnapshot(string SchemaName, string FunctionName, string FunctionDefinition, string? Comment = null, string? IdentityArguments = null, bool IsProcedure = false);

internal sealed record LiveTypeSnapshot(string SchemaName, string TypeName, string TypeKind, string Statement, string? Comment = null)
{
    /// <summary>An extension's installed version (G-213).</summary>
    internal string? ExtensionVersion { get; init; }

    /// <summary>A composite type's fields' comments, each a <c>COMMENT ON COLUMN</c> statement (G-216).</summary>
    internal IReadOnlyList<string> ColumnCommentStatements { get; init; } = [];

    /// <summary>The comment an extension comes with, from its control file (<c>pg_available_extensions</c>), or null (G-213).</summary>
    internal string? DefaultComment { get; init; }

    /// <summary>The names of the extensions an extension requires (G-213).</summary>
    internal IReadOnlyList<string> Requires { get; init; } = [];

    internal bool IsExtension => string.Equals(TypeKind, "extension", StringComparison.Ordinal);

    /// <summary>
    /// True when someone changed an extension's comment from the one it comes with, removing it included (G-213). A project that does not state an extension's
    /// comment is not compared against it either way, so leaving the default out only keeps a scaffold free of it, as for the <c>public</c> schema's (G-160).
    /// </summary>
    internal bool IsCommentChanged => !string.Equals(NullIfEmpty(Comment), NullIfEmpty(DefaultComment), StringComparison.Ordinal);

    /// <summary><c>COMMENT ON EXTENSION</c> for a changed comment (<c>IS NULL</c> for a removed one), or null when it is the one the extension comes with.</summary>
    internal string? ExtensionCommentStatement
        => IsCommentChanged
            ? $"COMMENT ON EXTENSION {LiveDatabaseModelLoader.RenderIdentifier(TypeName)} IS {(NullIfEmpty(Comment) is { } comment ? LiveDatabaseModelLoader.QuoteLiteral(comment) : "NULL")};"
            : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Extensions ordered so each comes after those it requires (G-213), and otherwise by name. One whose requirement is not among them (out of scope) is
    /// placed as if it had none.
    /// </summary>
    internal static IReadOnlyList<LiveTypeSnapshot> OrderExtensions(IEnumerable<LiveTypeSnapshot> extensions)
    {
        var pending = extensions.OrderBy(static extension => extension.TypeName, StringComparer.Ordinal).ToList();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<LiveTypeSnapshot>();
        var names = pending.Select(static extension => extension.TypeName).ToHashSet(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            // A cycle cannot be created, but if one were read, the first remaining extension is placed to keep going.
            var next = pending.FirstOrDefault(extension => extension.Requires.All(required => placed.Contains(required) || !names.Contains(required))) ?? pending[0];
            pending.Remove(next);
            placed.Add(next.TypeName);
            ordered.Add(next);
        }

        return ordered;
    }
}

internal sealed record LiveTriggerSnapshot(string SchemaName, string TableName, string TriggerName, string TriggerDefinition, string FunctionSchema, string FunctionName, string? Comment = null, string? EnabledState = null)
{
    /// <summary>The partitions whose copy of this trigger has a firing mode of its own, and the mode (G-293).</summary>
    public IReadOnlyList<(string SchemaName, string TableName, string State)> PartitionStates { get; init; } = [];
}

internal sealed record LivePolicySnapshot(
    string SchemaName,
    string TableName,
    string PolicyName,
    bool IsPermissive,
    string Command,
    IReadOnlyList<string> Roles,
    string? UsingExpression,
    string? WithCheckExpression,
    string? Comment = null);

internal sealed record LiveGrantSnapshot(string SchemaName, string ObjectKey, string Grantee, string Statement);

internal sealed record LiveSequenceSnapshot(
    string SchemaName,
    string SequenceName,
    string? DataType,
    string StartValue,
    string MinValue,
    string MaxValue,
    string IncrementBy,
    bool Cycle,
    string CacheSize,
    string? OwnedBySchema,
    string? OwnedByTable,
    string? OwnedByColumn,
    string? Comment = null)
{
    /// <summary>True for an identity column's sequence, which is written as the column's options rather than a sequence of its own (G-214).</summary>
    internal bool IsIdentity { get; init; }

    /// <summary>True for an unlogged sequence (<c>relpersistence</c> <c>u</c>), written <c>CREATE UNLOGGED SEQUENCE</c> (G-224).</summary>
    internal bool IsUnlogged { get; init; }

    /// <summary>
    /// An identity sequence's options as <c>GENERATED ... AS IDENTITY ( ... )</c> writes them, only those that are not PostgreSQL's defaults for the
    /// sequence's type and direction; null when all are (G-214).
    /// </summary>
    internal string? IdentityOptionsText => IdentityOptionsTextFor(tableIsUnlogged: IsUnlogged);

    /// <summary>
    /// <see cref="IdentityOptionsText"/> for a sequence whose table is, or is not, unlogged: an identity's sequence follows its table, so <c>UNLOGGED</c> (or <c>LOGGED</c>) is written only when the
    /// sequence is the other way (G-294).
    /// </summary>
    internal string? IdentityOptionsTextFor(bool tableIsUnlogged)
    {
        var (typeMinimum, typeMaximum) = DataType switch
        {
            "smallint" => (short.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture), short.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "integer" => (int.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture), int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            _ => (long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture), long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        var ascending = !IncrementBy.StartsWith('-');
        var defaultMinimum = ascending ? "1" : typeMinimum;
        var defaultMaximum = ascending ? typeMaximum : "-1";
        var options = new List<string>();
        if (StartValue != (ascending ? MinValue : MaxValue))
        {
            options.Add($"START WITH {StartValue}");
        }

        if (IncrementBy != "1")
        {
            options.Add($"INCREMENT BY {IncrementBy}");
        }

        if (MinValue != defaultMinimum)
        {
            options.Add($"MINVALUE {MinValue}");
        }

        if (MaxValue != defaultMaximum)
        {
            options.Add($"MAXVALUE {MaxValue}");
        }

        if (CacheSize != "1")
        {
            options.Add($"CACHE {CacheSize}");
        }

        if (Cycle)
        {
            options.Add("CYCLE");
        }

        if (IsUnlogged != tableIsUnlogged)
        {
            options.Add(IsUnlogged ? "UNLOGGED" : "LOGGED");
        }

        return options.Count == 0 ? null : string.Join(" ", options);
    }
}

/// <summary>An event trigger read from <c>pg_event_trigger</c> (G-127). <paramref name="EnabledMode"/> is <c>evtenabled</c>: <c>O</c>, <c>D</c>, <c>R</c>, or <c>A</c>.</summary>
internal sealed record LiveEventTriggerSnapshot(
    string Name,
    string EventName,
    IReadOnlyList<string> Tags,
    string FunctionSchema,
    string FunctionName,
    string EnabledMode);

/// <summary>
/// A user-defined cast read from <c>pg_cast</c> (G-131). The types are <c>format_type</c>'s text; the function (when there is one) is its schema, name,
/// and identity arguments; <paramref name="Context"/> is <c>castcontext</c> (<c>e</c>, <c>a</c>, <c>i</c>) and <paramref name="Method"/> is <c>castmethod</c>
/// (<c>f</c> function, <c>b</c> binary, <c>i</c> in/out).
/// </summary>
internal sealed record LiveCastSnapshot(
    string SourceType,
    string TargetType,
    string? FunctionSchema,
    string? FunctionName,
    string FunctionArguments,
    string Context,
    string Method);

/// <summary>
/// A user-defined operator read from <c>pg_operator</c> (G-129). The operand types are <c>format_type</c>'s text (null for a missing side); the function is
/// its schema and name; a commutator and negator are a schema and a name; the estimators are <c>regproc</c> text.
/// </summary>
internal sealed record LiveOperatorSnapshot(
    string OperatorSchema,
    string OperatorName,
    string? LeftType,
    string? RightType,
    string FunctionSchema,
    string FunctionName,
    string? CommutatorSchema,
    string? CommutatorName,
    string? NegatorSchema,
    string? NegatorName,
    string? RestrictFunction,
    string? JoinFunction,
    bool Hashes,
    bool Merges);

/// <summary>
/// A rule read from <c>pg_rewrite</c> (G-128): the relation it is on, its name, and <paramref name="Definition"/>, <c>pg_get_ruledef</c>'s rendering of it (a whole
/// <c>CREATE RULE</c> statement).
/// </summary>
internal sealed record LiveRuleSnapshot(string SchemaName, string TableName, string RuleName, string Definition, string? EnabledState = null);

/// <summary>
/// A user-defined aggregate read from <c>pg_aggregate</c> (G-130). Functions are already SQL (a quoted schema and name), types are <c>format_type</c>'s text, the sort
/// operator is <c>schema.name</c>, <paramref name="Kind"/> is <c>aggkind</c> (<c>n</c> ordinary, <c>o</c> ordered-set, <c>h</c> hypothetical-set), and the modifiers are
/// <c>aggfinalmodify</c> and <c>aggmfinalmodify</c> (<c>r</c>, <c>s</c>, <c>w</c>); <paramref name="Parallel"/> is <c>proparallel</c>.
/// </summary>
/// <summary>
/// A user-defined collation read from <c>pg_collation</c> (G-148). <paramref name="Provider"/> is <c>libc</c>, <c>icu</c>, or <c>builtin</c>; <paramref name="Locale"/> is the ICU or
/// builtin locale and <paramref name="LcCollate"/>/<paramref name="LcCtype"/> a libc collation's; <paramref name="Rules"/> is an ICU collation's tailoring.
/// </summary>
internal sealed record LiveCollationSnapshot(
    string SchemaName,
    string Name,
    string Provider,
    string? LcCollate,
    string? LcCtype,
    string? Locale,
    bool IsDeterministic,
    string? Rules,
    string? Comment = null);

/// <summary>A text search dictionary read from <c>pg_ts_dict</c> (G-149); <paramref name="InitOption"/> is <c>dictinitoption</c>, the option list as the server stores it.</summary>
internal sealed record LiveTextSearchDictionarySnapshot(string SchemaName, string Name, string TemplateSchemaName, string TemplateName, string InitOption, string? Comment = null);

/// <summary>One token type of a text search configuration and the dictionaries tried for it, each as <c>schema.name</c>, in order (G-149).</summary>
internal sealed record LiveTextSearchMapping(string Token, IReadOnlyList<string> Dictionaries);

/// <summary>A text search configuration read from <c>pg_ts_config</c> and <c>pg_ts_config_map</c> (G-149).</summary>
internal sealed record LiveTextSearchConfigurationSnapshot(string SchemaName, string Name, string ParserSchemaName, string ParserName, IReadOnlyList<LiveTextSearchMapping> Mappings, string? Comment = null);

/// <summary>What was read of text search (G-149).</summary>
internal sealed record LiveTextSearchSnapshot(IReadOnlyList<LiveTextSearchDictionarySnapshot> Dictionaries, IReadOnlyList<LiveTextSearchConfigurationSnapshot> Configurations)
{
    public static readonly LiveTextSearchSnapshot Empty = new([], []);
}

/// <summary>An extended statistics object read from <c>pg_statistic_ext</c> (G-150); <paramref name="Definition"/> is <c>pg_get_statisticsobjdef</c>'s <c>CREATE STATISTICS</c>.</summary>
internal sealed record LiveStatisticsSnapshot(string SchemaName, string Name, string TableSchemaName, string TableName, string Definition, string? Comment = null);

internal sealed record LiveAggregateSnapshot(
    string SchemaName,
    string Name,
    string Arguments,
    string Kind,
    int DirectArguments,
    string StateFunction,
    string StateType,
    int StateSpace,
    string? FinalFunction,
    bool FinalExtra,
    string FinalModify,
    string? CombineFunction,
    string? SerialFunction,
    string? DeserialFunction,
    string? InitialValue,
    string? MovingStateFunction,
    string? MovingInverseFunction,
    string? MovingStateType,
    int MovingStateSpace,
    string? MovingFinalFunction,
    bool MovingFinalExtra,
    string MovingFinalModify,
    string? MovingInitialValue,
    string? SortOperator,
    string Parallel);

/// <summary>A table of a live publication (G-134): its column list (empty when every column is published) and its row filter as <c>pg_get_expr</c> prints it.</summary>
internal sealed record LivePublicationTable(string SchemaName, string TableName, IReadOnlyList<string> Columns, string? Filter);

/// <summary>
/// A publication read from <c>pg_publication</c> (G-134): the operations it publishes, <paramref name="ViaRoot"/> (<c>pubviaroot</c>), <paramref name="GeneratedColumns"/> (PostgreSQL
/// 18's <c>pubgencols</c>: <c>n</c> none, <c>s</c> stored), and the tables and schemas it names (none of them for <c>FOR ALL TABLES</c>).
/// </summary>
internal sealed record LivePublicationSnapshot(
    string Name,
    bool AllTables,
    bool Insert,
    bool Update,
    bool Delete,
    bool Truncate,
    bool ViaRoot,
    string GeneratedColumns,
    IReadOnlyList<LivePublicationTable> Tables,
    IReadOnlyList<string> Schemas);
