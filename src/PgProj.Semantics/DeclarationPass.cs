using PgProj.Parser;

namespace PgProj.Semantics;

internal sealed class DeclarationPass
{
    public void Execute(
        DatabaseModel model,
        IEnumerable<SqlSyntaxTree> syntaxTrees,
        bool excludeGrants = false,
        bool excludeRoleMemberships = false,
        bool excludeOwners = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(syntaxTrees);

        model.Tables = new Dictionary<string, TableDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Views = new Dictionary<string, ViewDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Functions = new Dictionary<string, FunctionDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Types = new Dictionary<string, TypeDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Indexes = new Dictionary<string, IndexDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Constraints = new Dictionary<string, ConstraintDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Grants = new Dictionary<string, GrantDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Sequences = new Dictionary<string, SequenceDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Procedures = new Dictionary<string, ProcedureDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Triggers = new Dictionary<string, TriggerDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Policies = new Dictionary<string, PolicyDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Diagnostics = new List<SemanticDiagnostic>();
        model.EventTriggers = new Dictionary<string, EventTriggerDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Casts = new Dictionary<string, CastDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Operators = new Dictionary<string, OperatorDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Rules = new Dictionary<string, RuleDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Aggregates = new Dictionary<string, AggregateDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Collations = new Dictionary<string, CollationDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Statistics = new Dictionary<string, StatisticsDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.TextSearchDictionaries = new Dictionary<string, TextSearchDictionaryDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.TextSearchConfigurations = new Dictionary<string, TextSearchConfigurationDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Publications = new Dictionary<string, PublicationDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.Owners = new Dictionary<string, OwnerDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.SchemaDefinitions = new Dictionary<string, SchemaDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        model.SemanticGraph = SemanticGraph.Empty;
        model.TopologicalOrder = [];

        var trees = syntaxTrees as IReadOnlyList<SqlSyntaxTree> ?? syntaxTrees.ToArray();
        var eventTriggerStates = CollectEventTriggerStates(trees);

        foreach (var syntaxTree in trees)
        {
            MapDiagnostics(syntaxTree, model);
            DeclareTables(syntaxTree, model);
            DeclareViews(syntaxTree, model);
            DeclareFunctions(syntaxTree, model);
            DeclareProcedures(syntaxTree, model);
            DeclareTriggers(syntaxTree, model);
            DeclarePolicies(syntaxTree, model);
            DeclareEventTriggers(syntaxTree, model, eventTriggerStates);
            DeclareCasts(syntaxTree, model);
            DeclareOperators(syntaxTree, model);
            DeclareRules(syntaxTree, model);
            DeclareAggregates(syntaxTree, model);
            DeclareCollations(syntaxTree, model);
            DeclareStatistics(syntaxTree, model);
            DeclareTextSearch(syntaxTree, model);
            DeclarePublications(syntaxTree, model);
            DeclareIndexes(syntaxTree, model);
            DeclareTypes(syntaxTree, model);
            DeclareGrants(syntaxTree, model, excludeGrants, excludeRoleMemberships);
            DeclareSequences(syntaxTree, model);
        }

        ReportUndeclaredEventTriggerStates(model, eventTriggerStates, trees);

        // After every file is declared, so an ALTER ... MAPPING may be in a different file from the configuration it changes (G-149).
        TextSearchDeclarations.Apply(trees, model);

        // After every file is declared, so a `COMMENT ON TRIGGER` may be in a different file from the trigger (G-163).
        TableObjectCommentDeclarations.Apply(trees, model);

        // After every file is declared, so a `COMMENT ON SCHEMA` may be in a different file from the schema's objects (G-160).
        SchemaCommentDeclarations.Declare(trees, model);

        // After every file is declared, so an `ALTER ... OWNER TO` may be in a different file from the object it names (G-146).
        if (!excludeOwners)
        {
            OwnerDeclarations.Declare(trees, model);
        }
    }

    private static void DeclareIndexes(SqlSyntaxTree tree, DatabaseModel model)
    {
        var indexes = GetMutableDefinitions(model.Indexes, nameof(DatabaseModel.Indexes));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var indexNode in tree.Indexes)
        {
            AddDefinition(indexes, MapIndex(indexNode) with { Location = Place(tree, indexNode) }, diagnostics);
        }

        ApplyComments(tree, indexes, "Index", static (definition, comment) => definition with { Comment = comment });
    }

    private static void MapDiagnostics(SqlSyntaxTree syntaxTree, DatabaseModel model)
    {
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var diagnostic in syntaxTree.Diagnostics)
        {
            diagnostics.Add(new SemanticDiagnostic(
                diagnostic.Severity switch
                {
                    SqlDiagnosticSeverity.Error => SemanticDiagnosticSeverity.Error,
                    SqlDiagnosticSeverity.Warning => SemanticDiagnosticSeverity.Warning,
                    _ => SemanticDiagnosticSeverity.Info,
                },
                diagnostic.Message,
                SemanticLocation.FromSql(syntaxTree.FilePath, diagnostic),
                diagnostic.Source));
        }
    }

    private static void DeclareTables(SqlSyntaxTree tree, DatabaseModel model)
    {
        var tables = GetMutableDefinitions(model.Tables, nameof(DatabaseModel.Tables));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var tableNode in tree.Tables)
        {
            AddDefinition(tables, MapTable(tableNode) with { Location = Place(tree, tableNode) }, diagnostics);
        }

        ApplyTableComments(tree, tables);
    }

    private static void ApplyTableComments(SqlSyntaxTree tree, IDictionary<string, TableDefinition> tables)
    {
        foreach (var commentNode in tree.ObjectComments.Where(static node => node.ObjectKind == "Table"))
        {
            var key = DatabaseModel.CreateKey(commentNode.SchemaName, commentNode.TableName ?? string.Empty);
            if (tables.TryGetValue(key, out var table))
            {
                tables[key] = table with { Comment = commentNode.CommentText };
                continue;
            }

            // Fall back to an unqualified match when the COMMENT statement omitted the schema
            // but the table was declared with an explicit schema (or vice versa).
            var match = tables.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, commentNode.TableName, StringComparison.Ordinal)
                && (commentNode.SchemaName is null || string.Equals(candidate.SchemaName, commentNode.SchemaName, StringComparison.Ordinal)));
            if (match is not null)
            {
                tables[match.Key] = match with { Comment = commentNode.CommentText };
            }
        }
    }

    private static void DeclareViews(SqlSyntaxTree tree, DatabaseModel model)
    {
        var views = GetMutableDefinitions(model.Views, nameof(DatabaseModel.Views));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var viewNode in tree.Views)
        {
            var view = MapView(viewNode) with { Location = Place(tree, viewNode) };

            // CREATE OR REPLACE VIEW replaces the view of that name the project declared before, as it does in PostgreSQL (G-236).
            if (viewNode.IsReplace)
            {
                if (views.TryGetValue(view.Key, out var replaced) && !view.IsMaterialized)
                {
                    view = view with { InitialDefinition = (replaced.InitialDefinition ?? replaced) with { InitialDefinition = null } };
                }

                views.Remove(view.Key);
            }

            AddDefinition(views, view, diagnostics);
        }

        ApplyComments(tree, views, "View", static (definition, comment) => definition with { Comment = comment });
        ApplyColumnComments(tree, views, static _ => true, static (definition, column, comment) => definition with { ColumnComments = ColumnCommentMap.With(definition.ColumnComments, column, comment) });
    }

    private static void DeclareFunctions(SqlSyntaxTree tree, DatabaseModel model)
    {
        var functions = GetMutableDefinitions(model.Functions, nameof(DatabaseModel.Functions));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var functionNode in tree.Functions)
        {
            AddDefinition(functions, MapFunction(functionNode) with { Location = Place(tree, functionNode) }, diagnostics);
        }

        ApplyRoutineComments(
            tree,
            functions,
            "Function",
            static definition => definition.Signature?.RenderArgumentTypeSignature(),
            static (definition, comment) => definition with { Comment = comment });
    }

    private static void DeclareProcedures(SqlSyntaxTree tree, DatabaseModel model)
    {
        var procedures = GetMutableDefinitions(model.Procedures, nameof(DatabaseModel.Procedures));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var procedureNode in tree.Procedures)
        {
            AddDefinition(procedures, MapProcedure(procedureNode) with { Location = Place(tree, procedureNode) }, diagnostics);
        }

        ApplyRoutineComments(
            tree,
            procedures,
            "Procedure",
            static definition => definition.Signature?.RenderArgumentTypeSignature(),
            static (definition, comment) => definition with { Comment = comment });
    }

    private static void DeclareTriggers(SqlSyntaxTree tree, DatabaseModel model)
    {
        var triggers = GetMutableDefinitions(model.Triggers, nameof(DatabaseModel.Triggers));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var triggerNode in tree.Triggers)
        {
            AddDefinition(triggers, MapTrigger(triggerNode) with { Location = Place(tree, triggerNode) }, diagnostics);
        }
    }

    private static void DeclarePolicies(SqlSyntaxTree tree, DatabaseModel model)
    {
        var policies = GetMutableDefinitions(model.Policies, nameof(DatabaseModel.Policies));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var policyNode in tree.Policies)
        {
            AddDefinition(policies, MapPolicy(policyNode) with { Location = Place(tree, policyNode) }, diagnostics);
        }
    }

    private static PolicyDefinition MapPolicy(PolicyNode policyNode)
    {
        var policyName = policyNode.Name ?? "unknown_policy";
        var key = DatabaseModel.CreatePolicyKey(policyNode.SchemaName, policyNode.TableName, policyName);

        return new PolicyDefinition(
            key,
            policyName,
            policyNode.SchemaName,
            policyNode.TableName)
        {
            DeclarationNode = policyNode,
            Command = policyNode.Command,
            IsPermissive = policyNode.IsPermissive,
            Roles = policyNode.Roles,
            RawUsingExpression = policyNode.RawUsingExpression,
            RawWithCheckExpression = policyNode.RawWithCheckExpression,
            ReferencedTables = string.IsNullOrWhiteSpace(policyNode.TableName) ? [] : [policyNode.TableName],
        };
    }

    /// <summary>
    /// The mode each <c>ALTER EVENT TRIGGER name DISABLE|ENABLE ...</c> sets (G-127), by trigger name, across every file: the last one in
    /// file and statement order wins, as it does on the server.
    /// </summary>
    private static Dictionary<string, string> CollectEventTriggerStates(IReadOnlyList<SqlSyntaxTree> trees)
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var state in trees.SelectMany(static tree => tree.EventTriggerStates))
        {
            if (!string.IsNullOrWhiteSpace(state.Name))
            {
                states[state.Name] = state.EnabledMode;
            }
        }

        return states;
    }

    private static void DeclareEventTriggers(SqlSyntaxTree tree, DatabaseModel model, IReadOnlyDictionary<string, string> states)
    {
        var eventTriggers = GetMutableDefinitions(model.EventTriggers, nameof(DatabaseModel.EventTriggers));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var node in tree.EventTriggers)
        {
            AddDefinition(eventTriggers, MapEventTrigger(node, states) with { Location = Place(tree, node) }, diagnostics);
        }
    }

    private static EventTriggerDefinition MapEventTrigger(EventTriggerNode node, IReadOnlyDictionary<string, string> states)
    {
        var name = node.Name ?? "unknown_event_trigger";
        return new EventTriggerDefinition(DatabaseModel.CreateEventTriggerKey(name), name)
        {
            DeclarationNode = node,
            EventName = node.EventName?.ToLowerInvariant(),
            Tags = node.Tags,
            FunctionName = node.FunctionName,
            EnabledMode = states.TryGetValue(name, out var mode) ? mode : node.EnabledMode,
            ReferencedFunctions = string.IsNullOrWhiteSpace(node.FunctionName) ? [] : [node.FunctionName],
        };
    }

    private static void DeclareCasts(SqlSyntaxTree tree, DatabaseModel model)
    {
        var casts = GetMutableDefinitions(model.Casts, nameof(DatabaseModel.Casts));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var node in tree.Casts)
        {
            AddDefinition(casts, MapCast(node) with { Location = Place(tree, node) }, diagnostics);
        }
    }

    private static CastDefinition MapCast(CastNode node)
    {
        var key = CastTypeNames.CreateKey(node.SourceType, node.TargetType);
        return new CastDefinition(key, $"{node.SourceType} AS {node.TargetType}")
        {
            DeclarationNode = node,
            WrittenTypes = node.WrittenTypes,
            SourceType = node.SourceType,
            TargetType = node.TargetType,
            FunctionName = node.FunctionName,
            FunctionArguments = node.FunctionArguments,
            Method = node.IsInOut ? "inout" : string.IsNullOrWhiteSpace(node.FunctionName) ? "binary" : "function",
            Context = node.Context,
            ReferencedFunctions = string.IsNullOrWhiteSpace(node.FunctionName) ? [] : [node.FunctionName],
            ReferencedTypes = [.. new[] { node.SourceType, node.TargetType }.Where(static type => !string.IsNullOrWhiteSpace(type)).Select(static type => type!)],
        };
    }

    private static void DeclareOperators(SqlSyntaxTree tree, DatabaseModel model)
    {
        var operators = GetMutableDefinitions(model.Operators, nameof(DatabaseModel.Operators));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var node in tree.Operators)
        {
            AddDefinition(operators, MapOperator(node) with { Location = Place(tree, node) }, diagnostics);
        }
    }

    private static OperatorDefinition MapOperator(OperatorNode node)
    {
        var name = node.Name ?? string.Empty;
        var key = OperatorNames.CreateKey(node.SchemaName, name, node.LeftType, node.RightType);
        return new OperatorDefinition(key, DatabaseModel.CreateKey(node.SchemaName, name))
        {
            SchemaName = node.SchemaName,
            DeclarationNode = node,
            WrittenTypes = node.WrittenTypes,
            OperatorName = name,
            LeftType = node.LeftType,
            RightType = node.RightType,
            FunctionName = node.FunctionName,
            Commutator = node.Commutator,
            Negator = node.Negator,
            RestrictFunction = node.RestrictFunction,
            JoinFunction = node.JoinFunction,
            IsHashes = node.IsHashes,
            IsMerges = node.IsMerges,
            ReferencedFunctions = [.. new[] { node.FunctionName, node.RestrictFunction, node.JoinFunction }.Where(static function => !string.IsNullOrWhiteSpace(function)).Select(static function => function!)],
            ReferencedTypes = [.. new[] { node.LeftType, node.RightType }.Where(static type => !string.IsNullOrWhiteSpace(type)).Select(static type => type!)],
        };
    }

    private static void DeclareRules(SqlSyntaxTree tree, DatabaseModel model)
    {
        var rules = GetMutableDefinitions(model.Rules, nameof(DatabaseModel.Rules));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var node in tree.Rules)
        {
            AddDefinition(rules, MapRule(node) with { Location = Place(tree, node) }, diagnostics);
        }
    }

    private static RuleDefinition MapRule(RuleNode node)
    {
        var name = node.Name ?? "unknown_rule";
        return new RuleDefinition(RuleKeys.Create(node.SchemaName, node.TableName, name), name, node.SchemaName, node.TableName)
        {
            DeclarationNode = node,
            Event = node.Event,
            IsInstead = node.IsInstead,
            RawDefinition = node.RawDefinition,
            EnabledState = node.EnabledState,
        };
    }

    private static void DeclareCollations(SqlSyntaxTree tree, DatabaseModel model)
    {
        var collations = GetMutableDefinitions(model.Collations, nameof(DatabaseModel.Collations));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var node in tree.Collations)
        {
            AddDefinition(collations, MapCollation(node) with { Location = Place(tree, node) }, diagnostics);
        }

        ApplyComments(tree, collations, "Collation", static (definition, comment) => definition with { Comment = comment });
    }

    private static void DeclareStatistics(SqlSyntaxTree tree, DatabaseModel model)
    {
        var statistics = GetMutableDefinitions(model.Statistics, nameof(DatabaseModel.Statistics));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var node in tree.Statistics)
        {
            AddDefinition(statistics, MapStatistics(node) with { Location = Place(tree, node) }, diagnostics);
        }

        ApplyComments(tree, statistics, "Statistics", static (definition, comment) => definition with { Comment = comment });
    }

    private static void DeclareTextSearch(SqlSyntaxTree tree, DatabaseModel model)
    {
        var dictionaries = GetMutableDefinitions(model.TextSearchDictionaries, nameof(DatabaseModel.TextSearchDictionaries));
        var configurations = GetMutableDefinitions(model.TextSearchConfigurations, nameof(DatabaseModel.TextSearchConfigurations));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var node in tree.TextSearchDictionaries)
        {
            var name = node.Name ?? "unknown_dictionary";
            AddDefinition(
                dictionaries,
                new TextSearchDictionaryDefinition(DatabaseModel.CreateTextSearchDictionaryKey(node.SchemaName, name), name)
                {
                    SchemaName = node.SchemaName,
                    DeclarationNode = node,
                    Location = Place(tree, node),
                    Template = node.Template,
                    Options = node.Options,
                },
                diagnostics);
        }

        foreach (var node in tree.TextSearchConfigurations)
        {
            var name = node.Name ?? "unknown_configuration";
            AddDefinition(
                configurations,
                new TextSearchConfigurationDefinition(DatabaseModel.CreateTextSearchConfigurationKey(node.SchemaName, name), name)
                {
                    SchemaName = node.SchemaName,
                    DeclarationNode = node,
                    Location = Place(tree, node),
                    Parser = node.Parser,
                    CopyFrom = node.CopyFrom,
                },
                diagnostics);
        }

        ApplyComments(tree, dictionaries, "TextSearchDictionary", static (definition, comment) => definition with { Comment = comment });
        ApplyComments(tree, configurations, "TextSearchConfiguration", static (definition, comment) => definition with { Comment = comment });
    }

    private static StatisticsDefinition MapStatistics(StatisticsNode node)
    {
        var name = node.Name ?? "unknown_statistics";
        return new StatisticsDefinition(DatabaseModel.CreateStatisticsKey(node.SchemaName, name), name)
        {
            SchemaName = node.SchemaName,
            DeclarationNode = node,
            TableSchemaName = node.TableSchemaName,
            TableName = node.TableName,
            Kinds = node.Kinds,
            NormalizedKinds = StatisticsDefinition.NormalizeKinds(node.Kinds),
            Elements = [.. node.Elements.Select(static element => new StatisticsElementDefinition(element.Name) { RawExpression = element.RawExpression, ExpressionAst = element.ExpressionAst })],
            CopiedByLike = node.CopiedByLike,
            ReferencedTables = string.IsNullOrWhiteSpace(node.TableName) ? [] : [DatabaseModel.CreateKey(node.TableSchemaName, node.TableName)],
        };
    }

    private static CollationDefinition MapCollation(CollationNode node)
    {
        var name = node.Name ?? "unknown_collation";
        return new CollationDefinition(DatabaseModel.CreateCollationKey(node.SchemaName, name), name)
        {
            SchemaName = node.SchemaName,
            DeclarationNode = node,
            Provider = node.Provider,
            Locale = node.Locale,
            LcCollate = node.LcCollate,
            LcCtype = node.LcCtype,
            IsDeterministic = node.IsDeterministic,
            Rules = node.Rules,
        };
    }

    private static void DeclareAggregates(SqlSyntaxTree tree, DatabaseModel model)
    {
        var aggregates = GetMutableDefinitions(model.Aggregates, nameof(DatabaseModel.Aggregates));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var node in tree.Aggregates)
        {
            AddDefinition(aggregates, MapAggregate(node) with { Location = Place(tree, node) }, diagnostics);
        }
    }

    private static AggregateDefinition MapAggregate(AggregateNode node)
    {
        var name = node.Name ?? "unknown_aggregate";
        static string WithoutArray(string type) => type.EndsWith("[]", StringComparison.Ordinal) ? type[..type.IndexOf('[', StringComparison.Ordinal)] : type;

        var types = node.Arguments
            .Select(static argument => argument.StartsWith("VARIADIC ", StringComparison.Ordinal) ? argument["VARIADIC ".Length..] : argument)
            .Concat(new[] { "stype", "mstype" }.Where(node.Options.ContainsKey).Select(option => node.Options[option]))
            .Select(WithoutArray)
            .Where(static type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return new AggregateDefinition(AggregateOptions.CreateKey(node.SchemaName, name, node.Arguments), name)
        {
            SchemaName = node.SchemaName,
            DeclarationNode = node,
            WrittenTypes = node.WrittenTypes,
            Arguments = node.Arguments,
            ArgumentNames = node.ArgumentNames,
            DirectArgumentCount = node.DirectArgumentCount,
            Options = node.Options,
            NormalizedOptions = AggregateOptions.Normalize(node.DirectArgumentCount, node.Options),
            ReferencedFunctions = [.. AggregateOptions.FunctionOptions.Where(node.Options.ContainsKey).Select(option => node.Options[option]).Distinct(StringComparer.OrdinalIgnoreCase)],
            ReferencedTypes = [.. types],
        };
    }

    private static void DeclarePublications(SqlSyntaxTree tree, DatabaseModel model)
    {
        var publications = GetMutableDefinitions(model.Publications, nameof(DatabaseModel.Publications));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var node in tree.Publications)
        {
            AddDefinition(publications, MapPublication(node) with { Location = Place(tree, node) }, diagnostics);
        }
    }

    private static PublicationDefinition MapPublication(PublicationNode node)
    {
        var name = node.Name ?? "unknown_publication";
        return new PublicationDefinition(PublicationOptions.CreateKey(name), name)
        {
            DeclarationNode = node,
            IsAllTables = node.IsAllTables,
            Tables = node.Tables
                .Select(static table => new PublicationTableDefinition(table.SchemaName, table.TableName)
                {
                    Columns = table.Columns,
                    NormalizedColumns = [.. table.Columns.Order(StringComparer.Ordinal)],
                    RawFilter = table.RawFilter,
                    FilterAst = table.FilterAst,
                })
                .ToArray(),
            Schemas = node.Schemas,
            Options = node.Options,
            NormalizedOptions = PublicationOptions.Normalize(node.Options),
            ReferencedTables = [.. node.Tables.Select(static table => DatabaseModel.CreateKey(table.SchemaName, table.TableName)).Distinct(StringComparer.OrdinalIgnoreCase)],
        };
    }

    private static void ReportUndeclaredEventTriggerStates(DatabaseModel model, IReadOnlyDictionary<string, string> states, IReadOnlyList<SqlSyntaxTree> trees)
    {
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);
        foreach (var name in states.Keys.Where(name => !model.EventTriggers.ContainsKey(DatabaseModel.CreateEventTriggerKey(name))))
        {
            // The last ALTER EVENT TRIGGER of that name, the one whose state would have been used (G-309e).
            var place = trees
                .SelectMany(static tree => tree.EventTriggerStates.Select(node => (Tree: tree, Node: node)))
                .LastOrDefault(state => string.Equals(state.Node.Name, name, StringComparison.Ordinal));
            diagnostics.Add(SemanticDiagnostics.Warning(
                $"ALTER EVENT TRIGGER {name}: the project does not declare that event trigger, so the statement is ignored.",
                place.Tree is null ? null : Place(place.Tree, place.Node),
                nameof(DeclarationPass)));
        }
    }

    private static ProcedureDefinition MapProcedure(ProcedureNode procedureNode)
    {
        var procedureName = procedureNode.Name ?? "unknown_procedure";
        var key = DatabaseModel.CreateRoutineKey(procedureNode.SchemaName, procedureName, procedureNode.Signature?.RenderArgumentTypeSignature());

        return new ProcedureDefinition(
            key,
            procedureName,
            procedureNode.SchemaName,
            procedureNode.Language,
            ReferencedTables: [],
            ReferencedColumns: [],
            ReferencedTypes: [],
            ReferencedFunctions: [],
            ResolvedTables: [],
            ResolvedColumns: [],
            ResolvedTypes: [],
            ResolvedFunctions: [])
        {
            DeclarationNode = procedureNode,
            RawBody = procedureNode.RawBody,
            BodyDelimiter = procedureNode.BodyDelimiter,
            IsSqlStandardBody = procedureNode.IsSqlStandardBody,
            Signature = procedureNode.Signature,
        };
    }

    private static TriggerDefinition MapTrigger(TriggerNode triggerNode)
    {
        var triggerName = triggerNode.Name ?? "unknown_trigger";
        var key = DatabaseModel.CreateTriggerKey(triggerNode.SchemaName, triggerNode.TableName, triggerName);

        return new TriggerDefinition(
            key,
            triggerName,
            triggerNode.SchemaName,
            triggerNode.TableName)
        {
            DeclarationNode = triggerNode,
            FunctionName = triggerNode.FunctionName,
            EnabledState = triggerNode.EnabledState,
            PartitionStates = triggerNode.PartitionStates.ToDictionary(static item => string.IsNullOrEmpty(item.SchemaName) ? item.TableName : item.SchemaName + "." + item.TableName, static item => item.State, PgProj.Parser.SqlIdentifiers.NameComparer),
            Timing = triggerNode.Timing,
            Events = triggerNode.Events,
            IsRowLevel = triggerNode.IsRowLevel,
            RawStatement = triggerNode.RawStatement,
            IsConstraintTrigger = triggerNode.IsConstraintTrigger,
            IsDeferrable = triggerNode.IsDeferrable,
            IsInitiallyDeferred = triggerNode.IsInitiallyDeferred,
            ConstraintTableName = triggerNode.ConstraintTableName,
            UpdateColumns = triggerNode.UpdateColumns,
            FunctionArguments = triggerNode.FunctionArguments,
            OldTransitionTable = triggerNode.OldTransitionTable,
            NewTransitionTable = triggerNode.NewTransitionTable,
            RawWhenCondition = triggerNode.RawWhenCondition,
            ReferencedTables = (string.IsNullOrWhiteSpace(triggerNode.TableName) ? [] : new[] { triggerNode.TableName }).Concat(triggerNode.PartitionStates.Select(static item => string.IsNullOrEmpty(item.SchemaName) ? item.TableName : item.SchemaName + "." + item.TableName)).ToArray(),
            ReferencedFunctions = string.IsNullOrWhiteSpace(triggerNode.FunctionName) ? [] : [triggerNode.FunctionName],
        };
    }

    /// <summary>
    /// <c>CREATE TYPE</c>/<c>DOMAIN</c>/<c>EXTENSION</c> declarations, read from pg_query's AST via
    /// <see cref="SqlSyntaxTree.Types"/>/<see cref="SqlSyntaxTree.Domains"/>/<see cref="SqlSyntaxTree.Extensions"/>
    /// rather than matched with regexes over the raw SQL text (G-78).
    /// </summary>
    private static void DeclareTypes(SqlSyntaxTree tree, DatabaseModel model)
    {
        var types = GetMutableDefinitions(model.Types, nameof(DatabaseModel.Types));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var typeNode in tree.Types)
        {
            AddDefinition(types, CreateTypeDefinition(typeNode.SchemaName, typeNode.Name ?? "unknown_type", "user-defined") with { Location = Place(tree, typeNode) }, diagnostics);
        }

        foreach (var domainNode in tree.Domains)
        {
            AddDefinition(types, CreateTypeDefinition(domainNode.SchemaName, domainNode.Name ?? "unknown_domain", "domain") with { Location = Place(tree, domainNode) }, diagnostics);
        }

        foreach (var extensionNode in tree.Extensions)
        {
            var extensionName = extensionNode.Name ?? "unknown_extension";
            if (BuiltInExtensions.IsBuiltIn(extensionName))
            {
                // Every database ships with these; pgproj neither creates nor drops them (G-46).
                continue;
            }

            // An extension is named by its name alone (G-213): its schema is where its objects go, and only when the project states it.
            var extension = CreateTypeDefinition(null, extensionName, "extension") with
            {
                Key = ExtensionKeys.Create(extensionName),
                SchemaName = extensionNode.SchemaName,
                ExtensionSchemaStated = extensionNode.SchemaName is not null,
                ExtensionVersion = extensionNode.Version,
                ExtensionWrittenText = extensionNode.WrittenText,
                ExtensionRequires = [.. types.Values.Where(static type => string.Equals(type.TypeKind, "extension", StringComparison.Ordinal)).Select(static type => type.Key)],
                Location = Place(tree, extensionNode),
            };
            AddDefinition(types, extension, diagnostics);
        }

        ApplyComments(tree, types, "Type", static (definition, comment) => definition with { Comment = comment });
        ApplyColumnComments(
            tree,
            types,
            static type => string.Equals(type.TypeKind, "user-defined", StringComparison.Ordinal),
            static (definition, column, comment) => definition with { ColumnComments = ColumnCommentMap.With(definition.ColumnComments, column, comment) });
        // COMMENT ON EXTENSION names the extension alone, so it is matched by its key, never by a type that shares its name (G-213).
        foreach (var commentNode in tree.ObjectComments.Where(static node => node.ObjectKind == "Extension"))
        {
            var key = ExtensionKeys.Create(commentNode.TableName ?? string.Empty);
            if (types.TryGetValue(key, out var extension))
            {
                types[key] = extension with { Comment = commentNode.CommentText, CommentStated = true };
            }
        }
    }

    private static void DeclareGrants(SqlSyntaxTree tree, DatabaseModel model, bool excludeGrants, bool excludeRoleMemberships)
    {
        var grants = GetMutableDefinitions(model.Grants, nameof(DatabaseModel.Grants));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        // Grants come from the parsed statements (G-65), so text that only looks like one (a comment, a string, a routine body)
        // is never a grant (G-60). Saying the same grant twice is legal SQL and means the same thing, so it is not a duplicate.
        foreach (var node in tree.Grants)
        {
            foreach (var grant in GrantDeclarations.Declare(node, diagnostics, excludeGrants, excludeRoleMemberships, Place(tree, node)))
            {
                grants.TryAdd(grant.Key, grant);
            }
        }
    }

    private static void DeclareSequences(SqlSyntaxTree tree, DatabaseModel model)
    {
        var sequences = GetMutableDefinitions(model.Sequences, nameof(DatabaseModel.Sequences));
        var diagnostics = GetMutableDiagnostics(model.Diagnostics);

        foreach (var sequenceNode in tree.Sequences)
        {
            AddDefinition(sequences, MapSequence(sequenceNode) with { Location = Place(tree, sequenceNode) }, diagnostics);
        }

        ApplyComments(tree, sequences, "Sequence", static (definition, comment) => definition with { Comment = comment });
    }

    /// <summary>
    /// Applies <c>COMMENT ON ...</c> text captured for the given <paramref name="objectKind"/> onto
    /// already-declared definitions, matching by schema-qualified name with an unqualified-name
    /// fallback (mirroring <see cref="ApplyTableComments"/>) for objects whose identity is just a
    /// schema-qualified name (views, indexes, sequences, types).
    /// </summary>
    /// <summary>
    /// <c>COMMENT ON COLUMN relation.column</c> where the relation is one of <paramref name="definitions"/> (a view, or a composite type) rather than a
    /// table (G-216). A table's column comments are applied by <c>ColumnPass</c>; a name is never both, since they share a namespace.
    /// </summary>
    private static void ApplyColumnComments<TDefinition>(
        SqlSyntaxTree tree,
        IDictionary<string, TDefinition> definitions,
        Func<TDefinition, bool> accepts,
        Func<TDefinition, string, string?, TDefinition> withColumnComment)
        where TDefinition : ISemanticNode
    {
        foreach (var commentNode in tree.ObjectComments.Where(static node => node.ObjectKind == "Column" && node.ColumnName is not null))
        {
            var key = DatabaseModel.CreateKey(commentNode.SchemaName, commentNode.TableName ?? string.Empty);
            if (definitions.TryGetValue(key, out var definition) && accepts(definition))
            {
                definitions[key] = withColumnComment(definition, commentNode.ColumnName!, commentNode.CommentText);
            }
        }
    }

    private static void ApplyComments<TDefinition>(
        SqlSyntaxTree tree,
        IDictionary<string, TDefinition> definitions,
        string objectKind,
        Func<TDefinition, string?, TDefinition> withComment)
        where TDefinition : ISemanticNode
    {
        foreach (var commentNode in tree.ObjectComments.Where(node => node.ObjectKind == objectKind))
        {
            var key = DatabaseModel.CreateKey(commentNode.SchemaName, commentNode.TableName ?? string.Empty);
            if (definitions.TryGetValue(key, out var definition))
            {
                definitions[key] = withComment(definition, commentNode.CommentText);
                continue;
            }

            var match = definitions.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, commentNode.TableName, StringComparison.Ordinal)
                && (commentNode.SchemaName is null || string.Equals(candidate.SchemaName, commentNode.SchemaName, StringComparison.Ordinal)));
            if (match is not null)
            {
                definitions[match.Key] = withComment(match, commentNode.CommentText);
            }
        }
    }

    /// <summary>
    /// Applies <c>COMMENT ON FUNCTION/PROCEDURE ...</c> text onto declared routines. Routine
    /// identity requires overload-aware matching (G-29): first try the overload-qualified key
    /// built from the comment's captured argument-type signature, then fall back to matching by
    /// schema/name alone when the signature is absent or does not match any declared overload
    /// exactly, mirroring the bare-reference fallback used elsewhere for routines.
    /// </summary>
    private static void ApplyRoutineComments<TDefinition>(
        SqlSyntaxTree tree,
        IDictionary<string, TDefinition> definitions,
        string objectKind,
        Func<TDefinition, string?> getArgumentTypeSignature,
        Func<TDefinition, string?, TDefinition> withComment)
        where TDefinition : ISemanticNode
    {
        foreach (var commentNode in tree.ObjectComments.Where(node => node.ObjectKind == objectKind))
        {
            var routineName = commentNode.TableName ?? string.Empty;

            if (commentNode.ArgumentTypeSignature is not null)
            {
                var key = DatabaseModel.CreateRoutineKey(commentNode.SchemaName, routineName, commentNode.ArgumentTypeSignature);
                if (definitions.TryGetValue(key, out var exactMatch))
                {
                    definitions[key] = withComment(exactMatch, commentNode.CommentText);
                    continue;
                }
            }

            var match = definitions.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, routineName, StringComparison.Ordinal)
                && (commentNode.SchemaName is null || string.Equals(candidate.SchemaName, commentNode.SchemaName, StringComparison.Ordinal))
                && (commentNode.ArgumentTypeSignature is null || string.Equals(DatabaseModel.CanonicalArgumentTypeSignature(getArgumentTypeSignature(candidate) ?? string.Empty), DatabaseModel.CanonicalArgumentTypeSignature(commentNode.ArgumentTypeSignature), StringComparison.OrdinalIgnoreCase)));

            match ??= definitions.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, routineName, StringComparison.Ordinal)
                && (commentNode.SchemaName is null || string.Equals(candidate.SchemaName, commentNode.SchemaName, StringComparison.Ordinal)));

            if (match is not null)
            {
                definitions[match.Key] = withComment(match, commentNode.CommentText);
            }
        }
    }

    private static TypeDefinition CreateTypeDefinition(string? schemaName, string name, string typeKind)
        => new(
            DatabaseModel.CreateKey(schemaName, name),
            name,
            schemaName,
            typeKind,
            ReferencedTables: [],
            ReferencedColumns: [],
            ReferencedTypes: [],
            ReferencedFunctions: [],
            ResolvedTables: [],
            ResolvedColumns: [],
            ResolvedTypes: [],
            ResolvedFunctions: []);

    private static TableDefinition MapTable(TableNode tableNode)
    {
        var tableName = tableNode.Name ?? "unknown_table";
        var key = DatabaseModel.CreateKey(tableNode.SchemaName, tableName);

        // A partition depends on its parent existing first (CREATE TABLE ... PARTITION OF parent ... requires parent to already
        // be a partitioned table), reusing the same ReferencedTables that DependencyAnalyzer already turns into edges for every
        // other kind, so this needs no change there (G-24c).
        // A child of INHERITS is created after its parents, and dropped before them, the same way (G-194).
        var inheritsFrom = tableNode.InheritsFrom.Select(static parent => DatabaseModel.CreateKey(parent.SchemaName, parent.Name)).ToArray();
        // A table is created after the tables its LIKE clauses copy from, and a composite type it copies from (G-221); a source the project does not
        // declare is a missing table, as any other reference to one is.
        var likeSources = tableNode.LikeClauses.Select(static like => (Key: DatabaseModel.CreateKey(like.SchemaName, like.Name), like.SourceKind)).ToArray();
        var referencedTables = !string.IsNullOrWhiteSpace(tableNode.PartitionOfTableName)
            ? new[] { DatabaseModel.CreateKey(tableNode.PartitionOfSchemaName, tableNode.PartitionOfTableName) }
            : [.. inheritsFrom, .. likeSources.Where(static source => source.SourceKind != PgProj.Parser.TableLikeNode.TypeSource).Select(static source => source.Key)];

        return new TableDefinition(
            key,
            tableName,
            tableNode.SchemaName,
            Columns: [],
            ConstraintKeys: [],
            ReferencedTables: referencedTables,
            ReferencedColumns: [],
            // A typed table is created after the composite type it is of (G-136).
            ReferencedTypes: string.IsNullOrWhiteSpace(tableNode.OfTypeName)
                ? [.. likeSources.Where(static source => source.SourceKind == PgProj.Parser.TableLikeNode.TypeSource).Select(static source => source.Key)]
                : [DatabaseModel.CreateKey(tableNode.OfTypeSchemaName, tableNode.OfTypeName)],
            ReferencedFunctions: [],
            ResolvedTables: [],
            ResolvedColumns: [],
            ResolvedTypes: [],
            ResolvedFunctions: [])
        {
            DeclarationNode = tableNode,
            IsUnlogged = tableNode.IsUnlogged,
            TablespaceName = tableNode.TablespaceName,
            AccessMethod = tableNode.AccessMethod,
            StorageOptions = tableNode.StorageOptions,
            PartitionStrategy = tableNode.PartitionStrategy,
            PartitionKeyElements = tableNode.PartitionKeyElements,
            PartitionOfSchemaName = tableNode.PartitionOfSchemaName,
            PartitionOfTableName = tableNode.PartitionOfTableName,
            PartitionBoundRaw = tableNode.PartitionBoundRaw,
            ReplicaIdentity = tableNode.ReplicaIdentity,
            WithoutCluster = tableNode.WithoutCluster,
            InternalTriggerState = tableNode.InternalTriggerState,
            InheritedColumnDefaults = tableNode.InheritedColumnDefaults,
            InheritedColumnNotNulls = tableNode.InheritedColumnNotNulls,
            PartitionElementsText = tableNode.PartitionElementsText,
            OfTypeSchemaName = tableNode.OfTypeSchemaName,
            OfTypeName = tableNode.OfTypeName,
            InheritsFrom = inheritsFrom,
            LikeClauses = tableNode.LikeClauses,
            IsRowSecurityEnabled = tableNode.IsRowSecurityEnabled,
            IsRowSecurityForced = tableNode.IsRowSecurityForced,
        };
    }

    private static IndexDefinition MapIndex(IndexNode indexNode)
    {
        var indexName = indexNode.Name ?? (indexNode.TableName is null ? "unknown_index" : $"{indexNode.TableName}_idx");
        var key = DatabaseModel.CreateKey(indexNode.SchemaName, indexName);

        return new IndexDefinition(
            key,
            indexName,
            indexNode.SchemaName,
            indexNode.TableName,
            indexNode.AccessMethod,
            ReferencedTables: [],
            ReferencedColumns: [],
            ReferencedTypes: [],
            ReferencedFunctions: [],
            ResolvedTables: [],
            ResolvedColumns: [],
            ResolvedTypes: [],
            ResolvedFunctions: [])
        {
            DeclarationNode = indexNode,
            KeyColumns = indexNode.KeyColumns,
            IsUnique = indexNode.IsUnique,
            NullsNotDistinct = indexNode.NullsNotDistinct,
            KeyElements = indexNode.KeyElements
                .Select(static element => new IndexKeyElementDefinition(
                    element.Name,
                    element.RawExpression,
                    element.CollationName,
                    element.OperatorClassName,
                    element.OperatorClassOptions,
                    element.IsDescending,
                    element.NullsFirst))
                .ToArray(),
            IncludeColumns = indexNode.IncludeColumns,
            RawPredicateExpression = indexNode.RawPredicateExpression,
            StorageOptions = indexNode.StorageOptions,
            IsClustered = indexNode.IsClustered,
            IsReplicaIdentity = indexNode.IsReplicaIdentity,
            CopiedByLike = indexNode.CopiedByLike,
            IsNameGenerated = indexNode.IsNameGenerated,
        };
    }

    private static SequenceDefinition MapSequence(SequenceNode sequenceNode)
    {
        var sequenceName = sequenceNode.Name ?? "unknown_sequence";
        var key = DatabaseModel.CreateKey(sequenceNode.SchemaName, sequenceName);
        var options = sequenceNode.Options;

        string? ownedByTable = null;
        string? ownedByColumn = null;

        if (options is not null && options.TryGetValue("owned_by", out var ownedBy) && !string.IsNullOrWhiteSpace(ownedBy))
        {
            var separatorIndex = ownedBy.LastIndexOf('.');
            if (separatorIndex >= 0)
            {
                ownedByTable = ownedBy[..separatorIndex];
                ownedByColumn = ownedBy[(separatorIndex + 1)..];
            }
        }

        return new SequenceDefinition(
            key,
            sequenceName,
            sequenceNode.SchemaName,
            ReferencedTables: [],
            ReferencedColumns: [],
            ReferencedTypes: [],
            ReferencedFunctions: [],
            ResolvedTables: [],
            ResolvedColumns: [],
            ResolvedTypes: [],
            ResolvedFunctions: [])
        {
            DeclarationNode = sequenceNode,
            DataType = GetOption(options, "as"),
            IncrementBy = GetOption(options, "increment"),
            MinValue = GetOption(options, "minvalue"),
            MaxValue = GetOption(options, "maxvalue"),
            StartValue = GetOption(options, "start"),
            CacheSize = GetOption(options, "cache"),
            Cycle = TryGetBooleanOption(options, "cycle"),
            OwnedByTable = ownedByTable,
            OwnedByColumn = ownedByColumn,
            IsUnlogged = sequenceNode.IsUnlogged,
        };
    }

    private static string? GetOption(IReadOnlyDictionary<string, string>? options, string name)
        => options is not null && options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static bool? TryGetBooleanOption(IReadOnlyDictionary<string, string>? options, string name)
        => GetOption(options, name) is { } value && bool.TryParse(value, out var parsed)
            ? parsed
            : null;

    private static ViewDefinition MapView(ViewNode viewNode)
    {
        var viewName = viewNode.Name ?? "unknown_view";
        var key = DatabaseModel.CreateKey(viewNode.SchemaName, viewName);

        return new ViewDefinition(
            key,
            viewName,
            viewNode.SchemaName,
            ReferencedTables: [],
            ReferencedColumns: [],
            ReferencedTypes: [],
            ReferencedFunctions: [],
            ResolvedTables: [],
            ResolvedColumns: [],
            ResolvedTypes: [],
            ResolvedFunctions: [])
        {
            IsMaterialized = viewNode.IsMaterialized,
            DeclarationNode = viewNode,
            RawBodyExpression = viewNode.RawBodyExpression,
            QueryAst = viewNode.QueryAst,
            Options = viewNode.Options,
            ColumnAliases = viewNode.ColumnAliases,
            StorageOptions = viewNode.StorageOptions,
            WrittenOptions = viewNode.WrittenOptions,
            WithNoData = viewNode.WithNoData,
            TablespaceName = viewNode.TablespaceName,
            AccessMethod = viewNode.AccessMethod,
            ColumnDefaults = viewNode.ColumnDefaults.ToDictionary(static item => item.ColumnName, static item => item.Expression, PgProj.Parser.SqlIdentifiers.NameComparer),
            NormalizedColumnDefaults = viewNode.ColumnDefaults
                .Select(static item => (item.ColumnName, Normalized: DefaultExpressionCanonicalizer.Canonicalize(item.Expression, null, 0)))
                .Where(static item => item.Normalized is not null)
                .ToDictionary(static item => item.ColumnName, static item => item.Normalized!, PgProj.Parser.SqlIdentifiers.NameComparer),
        };
    }

    private static FunctionDefinition MapFunction(FunctionNode functionNode)
    {
        var functionName = functionNode.Name ?? "unknown_function";
        var key = DatabaseModel.CreateRoutineKey(functionNode.SchemaName, functionName, functionNode.Signature?.RenderArgumentTypeSignature());

        return new FunctionDefinition(
            key,
            functionName,
            functionNode.SchemaName,
            functionNode.Language,
            ReferencedTables: [],
            ReferencedColumns: [],
            ReferencedTypes: [],
            ReferencedFunctions: [],
            ResolvedTables: [],
            ResolvedColumns: [],
            ResolvedTypes: [],
            ResolvedFunctions: [])
        {
            DeclarationNode = functionNode,
            RawBody = functionNode.RawBody,
            BodyDelimiter = functionNode.BodyDelimiter,
            IsSqlStandardBody = functionNode.IsSqlStandardBody,
            Signature = functionNode.Signature,
        };
    }

    private static void AddDefinition<TDefinition>(
        IDictionary<string, TDefinition> definitions,
        TDefinition definition,
        ICollection<SemanticDiagnostic> diagnostics)
        where TDefinition : ISemanticNode
    {
        if (definitions.ContainsKey(definition.Key))
        {
            // The second definition is the one named (G-309e): the first is not a mistake on its own.
            diagnostics.Add(SemanticDiagnostics.Error($"Duplicate {definition.Kind} definition '{definition.Key}'.", definition.Location, nameof(DeclarationPass)));
            return;
        }

        definitions.Add(definition.Key, definition);
    }

    /// <summary>Where <paramref name="node"/> was written in <paramref name="tree"/>'s file (G-309e); null for a model read from a database.</summary>
    internal static SemanticLocation? Place(SqlSyntaxTree tree, SqlNode node) => SemanticLocation.Of(tree.FilePath, node.Range);

    private static IDictionary<string, TDefinition> GetMutableDefinitions<TDefinition>(
        IReadOnlyDictionary<string, TDefinition> definitions,
        string propertyName)
    {
        if (definitions is IDictionary<string, TDefinition> mutableDefinitions)
        {
            return mutableDefinitions;
        }

        throw new InvalidOperationException($"{nameof(DatabaseModel)}.{propertyName} must be backed by a mutable dictionary during {nameof(DeclarationPass)} execution.");
    }

    private static List<SemanticDiagnostic> GetMutableDiagnostics(IReadOnlyList<SemanticDiagnostic> diagnostics)
    {
        if (diagnostics is List<SemanticDiagnostic> mutableDiagnostics)
        {
            return mutableDiagnostics;
        }

        throw new InvalidOperationException($"{nameof(DatabaseModel)}.{nameof(DatabaseModel.Diagnostics)} must be backed by a mutable list during {nameof(DeclarationPass)} execution.");
    }
}
