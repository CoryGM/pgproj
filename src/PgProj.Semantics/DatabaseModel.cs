namespace PgProj.Semantics;

public sealed record DatabaseModel
{
    // Intentionally a computed property (not a cached singleton instance): every access must
    // return a brand-new DatabaseModel with brand-new mutable backing dictionaries. DeclarationPass
    // (and other passes) mutate these dictionaries and record properties in place via direct
    // assignment/IDictionary.Add, so a single shared "empty" instance would let concurrently
    // executing SemanticPipeline runs (e.g. parallel test execution) race on and corrupt each
    // other's starting state. See G-25.
    public static DatabaseModel Empty => new()
    {
        Tables = new Dictionary<string, TableDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Views = new Dictionary<string, ViewDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Functions = new Dictionary<string, FunctionDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Types = new Dictionary<string, TypeDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Indexes = new Dictionary<string, IndexDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Constraints = new Dictionary<string, ConstraintDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Grants = new Dictionary<string, GrantDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Sequences = new Dictionary<string, SequenceDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Procedures = new Dictionary<string, ProcedureDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Triggers = new Dictionary<string, TriggerDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Policies = new Dictionary<string, PolicyDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        EventTriggers = new Dictionary<string, EventTriggerDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Casts = new Dictionary<string, CastDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Operators = new Dictionary<string, OperatorDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Rules = new Dictionary<string, RuleDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Aggregates = new Dictionary<string, AggregateDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Collations = new Dictionary<string, CollationDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Statistics = new Dictionary<string, StatisticsDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        TextSearchDictionaries = new Dictionary<string, TextSearchDictionaryDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        TextSearchConfigurations = new Dictionary<string, TextSearchConfigurationDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Publications = new Dictionary<string, PublicationDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Owners = new Dictionary<string, OwnerDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        SchemaDefinitions = new Dictionary<string, SchemaDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer),
        Diagnostics = [],
        SemanticGraph = SemanticGraph.Empty,
        TopologicalOrder = [],
    };

    public required IReadOnlyDictionary<string, TableDefinition> Tables { get; set; }

    public required IReadOnlyDictionary<string, ViewDefinition> Views { get; set; }

    public required IReadOnlyDictionary<string, FunctionDefinition> Functions { get; set; }

    public required IReadOnlyDictionary<string, TypeDefinition> Types { get; set; }

    public required IReadOnlyDictionary<string, IndexDefinition> Indexes { get; set; }

    public required IReadOnlyDictionary<string, ConstraintDefinition> Constraints { get; set; }

    public required IReadOnlyDictionary<string, GrantDefinition> Grants { get; set; }

    public required IReadOnlyDictionary<string, SequenceDefinition> Sequences { get; set; }

    public IReadOnlyDictionary<string, ProcedureDefinition> Procedures { get; set; }
        = new Dictionary<string, ProcedureDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    public IReadOnlyDictionary<string, TriggerDefinition> Triggers { get; set; }
        = new Dictionary<string, TriggerDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The database's event triggers, by <see cref="CreateEventTriggerKey"/> (G-127).</summary>
    public IReadOnlyDictionary<string, EventTriggerDefinition> EventTriggers { get; set; }
        = new Dictionary<string, EventTriggerDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The database's user-defined casts, by <see cref="CastTypeNames.CreateKey"/> (G-131).</summary>
    public IReadOnlyDictionary<string, CastDefinition> Casts { get; set; }
        = new Dictionary<string, CastDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The database's user-defined operators, by <see cref="OperatorNames.CreateKey"/> (G-129).</summary>
    public IReadOnlyDictionary<string, OperatorDefinition> Operators { get; set; }
        = new Dictionary<string, OperatorDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The database's query rewrite rules, by <see cref="RuleKeys.Create"/> (G-128).</summary>
    public IReadOnlyDictionary<string, RuleDefinition> Rules { get; set; }
        = new Dictionary<string, RuleDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The database's user-defined aggregates, by <see cref="AggregateOptions.CreateKey"/> (G-130).</summary>
    public IReadOnlyDictionary<string, AggregateDefinition> Aggregates { get; set; }
        = new Dictionary<string, AggregateDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The database's collations, by <see cref="CreateKey"/> (G-148).</summary>
    public IReadOnlyDictionary<string, CollationDefinition> Collations { get; set; }
        = new Dictionary<string, CollationDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The database's extended statistics, by <see cref="CreateKey"/> (G-150).</summary>
    public IReadOnlyDictionary<string, StatisticsDefinition> Statistics { get; set; }
        = new Dictionary<string, StatisticsDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The database's text search dictionaries, by <see cref="CreateKey"/> (G-149).</summary>
    public IReadOnlyDictionary<string, TextSearchDictionaryDefinition> TextSearchDictionaries { get; set; }
        = new Dictionary<string, TextSearchDictionaryDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The database's text search configurations, by <see cref="CreateKey"/> (G-149).</summary>
    public IReadOnlyDictionary<string, TextSearchConfigurationDefinition> TextSearchConfigurations { get; set; }
        = new Dictionary<string, TextSearchConfigurationDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The database's publications, by <see cref="PublicationOptions.CreateKey"/> (G-134).</summary>
    public IReadOnlyDictionary<string, PublicationDefinition> Publications { get; set; }
        = new Dictionary<string, PublicationDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The owners the model states for objects, by <see cref="OwnerKeys.Create"/> (G-146). A live model has one for every object it reads; a project has the ones it states.</summary>
    public IReadOnlyDictionary<string, OwnerDefinition> Owners { get; set; }
        = new Dictionary<string, OwnerDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>What the model says about schemas as such, by <see cref="SchemaKeys.Create"/>: their comments (G-160). A project has the ones it states; a live model has one for each schema with a comment.</summary>
    public IReadOnlyDictionary<string, SchemaDefinition> SchemaDefinitions { get; set; }
        = new Dictionary<string, SchemaDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    public IReadOnlyDictionary<string, PolicyDefinition> Policies { get; set; }
        = new Dictionary<string, PolicyDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

    public required IReadOnlyList<SemanticDiagnostic> Diagnostics { get; set; }

    public required SemanticGraph SemanticGraph { get; set; }

    public required IReadOnlyList<string> TopologicalOrder { get; set; }

    /// <summary>
    /// For a model read from a live database, the role it was read as (<c>current_user</c>); null otherwise. Default privileges
    /// that a script states without <c>FOR ROLE</c> are the connected role's, so the comparison needs to know who that is to see
    /// that <c>FOR ROLE &lt;the connected role&gt;</c> says the same thing (G-65).
    /// </summary>
    public string? ConnectedRole { get; set; }

    /// <summary>
    /// The name of the connected database's default tablespace (<c>pg_database.dattablespace</c>), for a model read from a live database (G-153); null otherwise.
    /// A table written with <c>TABLESPACE</c> naming it is stored as having no tablespace of its own, so the project's clause is the same as none.
    /// </summary>
    public string? DefaultTablespaceName { get; set; }

    /// <summary>
    /// The project's <c>pgproj.json</c> <c>schemas</c> list (G-83), empty for a project that never populates it or for a
    /// model built without one (for example, the live database's reconstructed snapshot). See
    /// <see cref="SchemaScopeSeverity"/> for how this changes the severity of an otherwise-unresolvable reference.
    /// </summary>
    public IReadOnlyList<string> Schemas { get; set; } = [];

    /// <summary>
    /// The tables a <c>LIKE</c> clause copies from that the project does not declare (G-221), as keys. <c>diff</c> and <c>publish</c> read them from the
    /// database and build the project again with them, so the copied columns are known.
    /// </summary>
    public IReadOnlyList<string> ExternalLikeSources { get; set; } = [];

    /// <summary>
    /// The project's <c>searchPath</c> (G-93): the schemas an unqualified reference is found in, in order, after <c>pg_catalog</c>. <c>public</c>
    /// alone unless <c>pgproj.json</c> says otherwise, and for a model read from a live database.
    /// </summary>
    public IReadOnlyList<string> SearchPath { get; set; } = ["public"];

    /// <summary>
    /// The schemas whose objects find an unqualified name along a search path of their own instead of <see cref="SearchPath"/> (G-290): a tenant template's
    /// stand-in schema in <c>build</c>, or a tenant's schema in <c>diff</c> and <c>publish</c>, each with that schema first and then the project's
    /// <c>searchPath</c>, as PostgreSQL resolves them in the session that deploys the tenant. Empty for a project without tenants.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SchemaSearchPaths { get; set; } = new Dictionary<string, IReadOnlyList<string>>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The search path an object in <paramref name="schemaName"/> resolves its unqualified names along: its schema's own (G-290), else <see cref="SearchPath"/>.</summary>
    public IReadOnlyList<string> SearchPathFor(string? schemaName)
        => schemaName is not null && SchemaSearchPaths.TryGetValue(schemaName, out var path) ? path : SearchPath;

    /// <summary>
    /// For each unqualified function or type name that an object uses and the <see cref="SearchPath"/> does not find, the schemas the project declares it in (G-251). <c>init</c> picks the
    /// smallest set of schemas that finds them all and writes it as the new project's <c>searchPath</c>.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> SearchPathFindings { get; set; } = [];

    /// <summary>
    /// For a live model read with grants excluded (G-87), the object privileges the database has, so an object that is dropped and
    /// recreated keeps them (G-116). Empty otherwise: when grants are modeled, the project's own grants are issued again instead.
    /// </summary>
    public IReadOnlyList<UnmodeledPrivilege> UnmodeledPrivileges { get; set; } = [];

    /// <summary>For a live model, the views and materialized views that depend on each relation and column (G-103). Empty otherwise.</summary>
    public IReadOnlyList<LiveDependency> LiveDependencies { get; set; } = [];

    /// <summary>For a live model, the owner of each view and materialized view, by key (G-103), so one that is recreated keeps it.</summary>
    public IReadOnlyDictionary<string, string> LiveOwners { get; set; } = new Dictionary<string, string>(PgProj.Parser.SqlIdentifiers.NameComparer);

    public bool RecoveryActive { get; set; }

    public IReadOnlyList<SemanticDiagnostic> RecoveryDiagnostics { get; set; } = [];

    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error);

    public bool TryGetTable(string key, out TableDefinition? table)
        => Tables.TryGetValue(NormalizeKey(key), out table);

    public bool TryGetView(string key, out ViewDefinition? view)
        => Views.TryGetValue(NormalizeKey(key), out view);

    public bool TryGetFunction(string key, out FunctionDefinition? function)
        => Functions.TryGetValue(NormalizeKey(key), out function);

    public bool TryGetType(string key, out TypeDefinition? type)
        => Types.TryGetValue(NormalizeKey(key), out type);

    /// <summary>
    /// The materialized view an index may be on (G-171): a materialized view is the only relation besides a table that can own an
    /// index. An unqualified name is looked for along the search path, then in <paramref name="schemaName"/>, then in any schema.
    /// </summary>
    public ViewDefinition? FindMaterializedView(string? schemaName, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        if (Views.TryGetValue(NormalizeKey(trimmed), out var exact) && exact.IsMaterialized)
        {
            return exact;
        }

        if (!string.IsNullOrWhiteSpace(schemaName)
            && Views.TryGetValue(NormalizeKey(CreateKey(schemaName, trimmed)), out var scoped)
            && scoped.IsMaterialized)
        {
            return scoped;
        }

        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var pathSchema in SearchPathFor(schemaName))
        {
            if (Views.TryGetValue(NormalizeKey(CreateKey(pathSchema, trimmed)), out var onPath) && onPath.IsMaterialized)
            {
                return onPath;
            }
        }

        return null;
    }

    /// <summary>
    /// The sequence a query may read as a relation (<c>SELECT last_value FROM seq</c>, G-231). An unqualified name is looked for along the search path, then in
    /// <paramref name="schemaName"/>.
    /// </summary>
    public SequenceDefinition? FindSequence(string? schemaName, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        if (Sequences.TryGetValue(NormalizeKey(trimmed), out var exact))
        {
            return exact;
        }

        if (!string.IsNullOrWhiteSpace(schemaName) && Sequences.TryGetValue(NormalizeKey(CreateKey(schemaName, trimmed)), out var scoped))
        {
            return scoped;
        }

        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var pathSchema in SearchPathFor(schemaName))
        {
            if (Sequences.TryGetValue(NormalizeKey(CreateKey(pathSchema, trimmed)), out var onPath))
            {
                return onPath;
            }
        }

        return null;
    }

    public bool TryGetIndex(string key, out IndexDefinition? index)
        => Indexes.TryGetValue(NormalizeKey(key), out index);

    public bool TryGetConstraint(string key, out ConstraintDefinition? constraint)
        => Constraints.TryGetValue(NormalizeKey(key), out constraint);

    public bool TryGetGrant(string key, out GrantDefinition? grant)
        => Grants.TryGetValue(NormalizeKey(key), out grant);

    public bool TryGetSequence(string key, out SequenceDefinition? sequence)
        => Sequences.TryGetValue(NormalizeKey(key), out sequence);

    public bool TryGetProcedure(string key, out ProcedureDefinition? procedure)
        => Procedures.TryGetValue(NormalizeKey(key), out procedure);

    public bool TryGetTrigger(string key, out TriggerDefinition? trigger)
        => Triggers.TryGetValue(NormalizeKey(key), out trigger);

    public bool TryGetPublication(string key, out PublicationDefinition? publication)
        => Publications.TryGetValue(NormalizeKey(key), out publication);

    public bool TryGetOwner(string key, out OwnerDefinition? owner)
        => Owners.TryGetValue(NormalizeKey(key), out owner);

    public bool TryGetAggregate(string key, out AggregateDefinition? aggregate)
        => Aggregates.TryGetValue(NormalizeKey(key), out aggregate);

    /// <summary>A collation by its key, or by its schema-qualified name as a column names it (G-228).</summary>
    public bool TryGetCollation(string key, out CollationDefinition? collation)
    {
        var normalized = NormalizeKey(key);
        return Collations.TryGetValue(normalized.StartsWith("collation:", StringComparison.Ordinal) ? normalized : "collation:" + normalized, out collation);
    }

    public bool TryGetRule(string key, out RuleDefinition? rule)
        => Rules.TryGetValue(NormalizeKey(key), out rule);

    public bool TryGetOperator(string key, out OperatorDefinition? @operator)
        => Operators.TryGetValue(NormalizeKey(key), out @operator);

    public bool TryGetCast(string key, out CastDefinition? cast)
        => Casts.TryGetValue(NormalizeKey(key), out cast);

    public bool TryGetEventTrigger(string key, out EventTriggerDefinition? eventTrigger)
        => EventTriggers.TryGetValue(NormalizeKey(key), out eventTrigger);

    public bool TryGetPolicy(string key, out PolicyDefinition? policy)
        => Policies.TryGetValue(NormalizeKey(key), out policy);

    public IEnumerable<ISemanticNode> GetAllNodes()
    {
        foreach (var table in Tables.Values)
        {
            yield return table;
        }

        foreach (var view in Views.Values)
        {
            yield return view;
        }

        foreach (var function in Functions.Values)
        {
            yield return function;
        }

        foreach (var type in Types.Values)
        {
            yield return type;
        }

        foreach (var index in Indexes.Values)
        {
            yield return index;
        }

        foreach (var constraint in Constraints.Values)
        {
            yield return constraint;
        }

        foreach (var grant in Grants.Values)
        {
            yield return grant;
        }

        foreach (var sequence in Sequences.Values)
        {
            yield return sequence;
        }

        foreach (var procedure in Procedures.Values)
        {
            yield return procedure;
        }

        foreach (var trigger in Triggers.Values)
        {
            yield return trigger;
        }

        foreach (var policy in Policies.Values)
        {
            yield return policy;
        }

        foreach (var eventTrigger in EventTriggers.Values)
        {
            yield return eventTrigger;
        }

        foreach (var cast in Casts.Values)
        {
            yield return cast;
        }

        foreach (var @operator in Operators.Values)
        {
            yield return @operator;
        }

        foreach (var rule in Rules.Values)
        {
            yield return rule;
        }

        foreach (var aggregate in Aggregates.Values)
        {
            yield return aggregate;
        }

        foreach (var collation in Collations.Values)
        {
            yield return collation;
        }

        foreach (var statistics in Statistics.Values)
        {
            yield return statistics;
        }

        foreach (var dictionary in TextSearchDictionaries.Values)
        {
            yield return dictionary;
        }

        foreach (var configuration in TextSearchConfigurations.Values)
        {
            yield return configuration;
        }

        foreach (var publication in Publications.Values)
        {
            yield return publication;
        }

        foreach (var owner in Owners.Values)
        {
            yield return owner;
        }

        foreach (var schema in SchemaDefinitions.Values)
        {
            yield return schema;
        }
    }

    public static string CreateKey(string? schemaName, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return string.IsNullOrWhiteSpace(schemaName)
            ? name.Trim()
            : $"{schemaName.Trim()}.{name.Trim()}";
    }

    /// <summary>
    /// Builds an overload-safe identity key for a function or procedure. PostgreSQL identifies
    /// a specific routine overload by schema, name, and the types of its <c>IN</c>/<c>INOUT</c>/
    /// <c>VARIADIC</c> parameters (see <see cref="RoutineSignature.RenderArgumentTypeSignature"/>),
    /// not by schema and name alone (G-29). When <paramref name="argumentTypeSignature"/> is
    /// null/empty (no captured signature, or a zero-argument routine), the key degrades to the
    /// plain schema-qualified name so unsignatured routines keep a stable, readable identity.
    /// <para>
    /// The types are compared in canonical form (<see cref="RoutineTypeNames.Canonicalize"/>, G-67): <c>integer</c>, <c>int4</c>, and
    /// <c>pg_catalog.int4</c> are one type, so <c>f(integer)</c> and <c>f(int4)</c> are one routine, as they are to PostgreSQL. The
    /// signature as written stays on the routine for the SQL that is generated.
    /// </para>
    /// </summary>
    public static string CreateRoutineKey(string? schemaName, string name, string? argumentTypeSignature)
    {
        var baseKey = CreateKey(schemaName, name);

        // A routine with no arguments is "schema.name()" (G-228): plain "schema.name" is a relation's key, and PostgreSQL allows a function and a table of one name.
        // Only a routine whose signature was not captured at all keeps the plain name.
        return argumentTypeSignature is null
            ? baseKey
            : string.IsNullOrWhiteSpace(argumentTypeSignature)
                ? $"{baseKey}()"
                : $"{baseKey}({CanonicalArgumentTypeSignature(argumentTypeSignature)})";
    }

    /// <summary>
    /// The key of an extended statistics object (G-228). Statistics, collations, and text search dictionaries and configurations each have a namespace of their own in
    /// PostgreSQL, so one may have a table's name; their keys carry their kind, as a cast's, a rule's, and a publication's do, or the two would be one node of the graph.
    /// </summary>
    public static string CreateStatisticsKey(string? schemaName, string name) => "statistics:" + CreateKey(schemaName, name);

    /// <summary>
    /// A key as a message shows it (G-228): without the kind a statistics object's, a collation's, or a text search object's key carries, since the message names
    /// the kind already. Any other key is returned as it is.
    /// </summary>
    public static string DisplayKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var prefix in new[] { "statistics:", "collation:", "tsdictionary:", "tsconfiguration:", "schema-comment:" })
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return key[prefix.Length..];
            }
        }

        return key;
    }

    /// <summary>The key of a collation (G-228); see <see cref="CreateStatisticsKey"/>.</summary>
    public static string CreateCollationKey(string? schemaName, string name) => "collation:" + CreateKey(schemaName, name);

    /// <summary>The key of a text search dictionary (G-228); see <see cref="CreateStatisticsKey"/>.</summary>
    public static string CreateTextSearchDictionaryKey(string? schemaName, string name) => "tsdictionary:" + CreateKey(schemaName, name);

    /// <summary>The key of a text search configuration (G-228); see <see cref="CreateStatisticsKey"/>.</summary>
    public static string CreateTextSearchConfigurationKey(string? schemaName, string name) => "tsconfiguration:" + CreateKey(schemaName, name);

    /// <summary>
    /// The canonical form of an argument-type signature as <see cref="RoutineSignature.RenderArgumentTypeSignature"/> renders it
    /// (types joined by commas, which may also appear inside a type's modifiers): each type canonical, joined by <c>,</c>.
    /// </summary>
    public static string CanonicalArgumentTypeSignature(string argumentTypeSignature)
    {
        var types = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < argumentTypeSignature.Length; index++)
        {
            switch (argumentTypeSignature[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    types.Add(argumentTypeSignature[start..index]);
                    start = index + 1;
                    break;
            }
        }

        types.Add(argumentTypeSignature[start..]);
        return string.Join(",", types.Select(RoutineTypeNames.Canonicalize));
    }

    /// <summary>
    /// Builds the identity key for a trigger. PostgreSQL scopes trigger names to their table, not
    /// their schema, so the same trigger name may legitimately exist on several tables in one schema
    /// (G-36). The key is <c>schema.table.trigger</c>; when the table is unknown it degrades to the
    /// plain schema-qualified name.
    /// </summary>
    public static string CreateTriggerKey(string? schemaName, string? tableName, string triggerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
        return string.IsNullOrWhiteSpace(tableName)
            ? CreateKey(schemaName, triggerName)
            : $"{CreateKey(schemaName, tableName)}.{triggerName.Trim()}";
    }

    /// <summary>
    /// Builds the identity key for a constraint. PostgreSQL scopes constraint names to their table, so
    /// the same name (for example a <c>CHECK</c> or <c>FOREIGN KEY</c> constraint) may legitimately exist
    /// on several tables in one schema; a per-schema key silently collapsed them (G-38). The key is
    /// <c>schema.table.constraint</c>; when the table is unknown it degrades to the plain
    /// schema-qualified name. Unnamed constraints use the synthesized name <c>{table}_constraint</c>.
    /// </summary>
    public static string CreateConstraintKey(string? schemaName, string? tableName, string? constraintName)
    {
        var name = !string.IsNullOrWhiteSpace(constraintName)
            ? constraintName
            : string.IsNullOrWhiteSpace(tableName) ? "unknown_constraint" : $"{tableName.Trim()}_constraint";

        return string.IsNullOrWhiteSpace(tableName)
            ? CreateKey(schemaName, name)
            : $"{CreateKey(schemaName, tableName)}.{name.Trim()}";
    }

    /// <summary>
    /// Builds the identity key for a row-level security policy (G-76). PostgreSQL scopes a policy's name to its table, the
    /// same as a trigger (G-36) or a constraint (G-38), so the same policy name may legitimately exist on several tables.
    /// The key is <c>schema.table.policy</c>; when the table is unknown it degrades to the plain schema-qualified name.
    /// </summary>
    public static string CreatePolicyKey(string? schemaName, string? tableName, string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        return string.IsNullOrWhiteSpace(tableName)
            ? CreateKey(schemaName, policyName)
            : $"{CreateKey(schemaName, tableName)}.{policyName.Trim()}";
    }

    /// <summary>
    /// The identity key of an event trigger (G-127): it belongs to no schema, so its name alone, behind a prefix that no schema-qualified
    /// object's key can have, since a node's key is its id in the dependency graph.
    /// </summary>
    public static string CreateEventTriggerKey(string eventTriggerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventTriggerName);
        return $"event trigger:{eventTriggerName.Trim()}";
    }

    private static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return key.Trim();
    }
}
