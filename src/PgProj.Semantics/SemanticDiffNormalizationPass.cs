using PgProj.Parser;

namespace PgProj.Semantics;

internal sealed class SemanticDiffNormalizationPass
{
    public void Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        NormalizeTables(model);
        NormalizeStandaloneConstraints(model);
        NormalizeStandaloneIndexes(model);
        NormalizeViews(model);
        NormalizeFunctions(model);
        NormalizeProcedures(model);
        NormalizeTriggers(model);
        NormalizeSequenceOwners(model);
        NormalizePolicies(model);
        NormalizeEventTriggers(model);
        NormalizeCasts(model);
        NormalizeOperators(model);
        NormalizeRules(model);
        NormalizeAggregates(model);
        NormalizeCollations(model);
        NormalizeStatistics(model);
        NormalizeTextSearch(model);
        NormalizePublications(model);
        NormalizeTypes(model);
    }

    /// <summary>
    /// A sequence's <c>OWNED BY table.column</c> written with an unqualified table means the table the search path finds (G-283): the first schema of the path that has a table of that name, else the
    /// default schema. The database reads the owning table back qualified, so the diff compares this form.
    /// </summary>
    private static void NormalizeSequenceOwners(DatabaseModel model)
    {
        if (model.Sequences is not IDictionary<string, SequenceDefinition> sequences || !sequences.Values.Any(static sequence => sequence.OwnedByTable is { Length: > 0 } table && !table.Contains('.', StringComparison.Ordinal)))
        {
            return;
        }

        var resolved = sequences.Values.Select(sequence =>
        {
            // A tenant's sequence finds its table along the tenant's own path (G-290).
            var path = model.SearchPathFor(sequence.SchemaName) is { Count: > 0 } ownPath ? ownPath : [PgProj.Parser.DefaultSchema.Name];
            if (sequence.OwnedByTable is not { Length: > 0 } table || table.Contains('.', StringComparison.Ordinal))
            {
                return sequence;
            }

            var schema = path.FirstOrDefault(candidate => model.Tables.ContainsKey(DatabaseModel.CreateKey(candidate, table))) ?? path[0];
            return sequence with { ResolvedOwnedByTable = schema + "." + table };
        }).ToArray();

        ReplaceDefinitions(sequences, resolved, keepSourceOrder: true);
    }

    private void NormalizeTables(DatabaseModel model)
    {
        if (model.Tables is not IDictionary<string, TableDefinition> tables)
        {
            return;
        }

        var diagnostics = model.Diagnostics as IList<SemanticDiagnostic>;
        var normalizedTables = tables.Values
            .Select(table =>
            {
                var normalizedTable = table with
                {
                    Key = CanonicalizeIdentifier(table.Key),
                    Name = PreserveIdentifier(table.Name),
                    SchemaName = PreserveOptionalIdentifier(table.SchemaName),
                    ReferencedTables = CanonicalizeList(table.ReferencedTables),
                    ReferencedColumns = CanonicalizeList(table.ReferencedColumns),
                    ReferencedTypes = CanonicalizeList(table.ReferencedTypes),
                    ReferencedFunctions = CanonicalizeList(table.ReferencedFunctions),
                    ResolvedTables = CanonicalizeList(table.ResolvedTables),
                    ResolvedColumns = CanonicalizeList(table.ResolvedColumns),
                    ResolvedTypes = CanonicalizeList(table.ResolvedTypes),
                    ResolvedFunctions = CanonicalizeList(table.ResolvedFunctions),
                    TablespaceName = PreserveOptionalIdentifier(table.TablespaceName),
                    NormalizedStorageOptions = table.StorageOptions
                        .OrderBy(static option => option.Name, StringComparer.Ordinal)
                        .ToArray(),
                };

                normalizedTable = NormalizeColumns(normalizedTable);
                normalizedTable = NormalizeConstraints(normalizedTable);
                normalizedTable = NormalizeIndexes(normalizedTable);
                ReportDuplicateColumns(normalizedTable, diagnostics);
                return normalizedTable;
            })
            .ToArray();

        ReplaceDefinitions(tables, normalizedTables);
    }

    private TableDefinition NormalizeColumns(TableDefinition table)
    {
        var columns = table.Columns
            .Select(column => column with
            {
                Key = CanonicalizeIdentifier(column.Key),
                Name = PreserveIdentifier(column.Name),
                SchemaName = PreserveOptionalIdentifier(column.SchemaName),
                TableName = PreserveOptionalIdentifier(column.TableName),
                DataType = TrimOrNull(column.DataType),
                NormalizedTypeModifiers = ColumnTypeNormalizer.NormalizeModifiers(column.DataType, column.TypeModifiers),
                NormalizedGeneratedExpression = NormalizeGeneratedExpression(table, column),
                IntervalFields = TrimOrNull(column.IntervalFields)?.ToLowerInvariant(),
                ReferencedTables = CanonicalizeList(column.ReferencedTables),
                ReferencedColumns = CanonicalizeList(column.ReferencedColumns),
                ReferencedTypes = CanonicalizeList(column.ReferencedTypes),
                ReferencedFunctions = CanonicalizeList(column.ReferencedFunctions),
                ResolvedTables = CanonicalizeList(column.ResolvedTables),
                ResolvedColumns = CanonicalizeList(column.ResolvedColumns),
                ResolvedTypes = CanonicalizeList(column.ResolvedTypes),
                ResolvedFunctions = CanonicalizeList(column.ResolvedFunctions),
                // Already canonical (DefaultExpressionCanonicalizer). Collapsing whitespace again here would also
                // rewrite the inside of a string literal, hiding a real change such as 'a  b' -> 'a b'.
                NormalizedDefaultExpression = column.NormalizedDefaultExpression,
            })
            .ToArray();

        return table with
        {
            Columns = columns,
        };
    }

    /// <summary>
    /// The comparison form of a generated column's expression (G-71): PostgreSQL stores and prints it fully parenthesized, so the text is
    /// never compared; the parsed expression is, with the column types of the table so a <c>varchar</c> column's own <c>::text</c> folds.
    /// </summary>
    private static string? NormalizeGeneratedExpression(TableDefinition table, ColumnDefinition column)
    {
        if (!column.IsGenerated || string.IsNullOrWhiteSpace(column.GeneratedExpression))
        {
            return null;
        }

        return TriggerWhenConditionCanonicalizer.Canonicalize(column.DeclarationNode?.GeneratedExpressionAst, name => ColumnBaseType(table, name))
            ?? CanonicalizeExpression(column.GeneratedExpression);
    }

    private static string? ColumnBaseType(TableDefinition? table, string column)
    {
        var type = table?.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, column, StringComparison.Ordinal))?.RawType;
        var name = ColumnTypeNormalizer.NormalizeBaseType(type);
        return name is "character varying" ? "varchar" : name;
    }

    private TableDefinition NormalizeConstraints(TableDefinition table)
        => table with
        {
            ConstraintKeys = CanonicalizeList(table.ConstraintKeys),
        };

    private TableDefinition NormalizeIndexes(TableDefinition table)
    {
        // TODO: Attach per-table index associations and normalize their ordering once table semantics model index membership directly.
        return table;
    }

    private void NormalizeViews(DatabaseModel model)
    {
        if (model.Views is not IDictionary<string, ViewDefinition> views)
        {
            return;
        }

        var normalizedViews = views.Values
            .Select(view => view with
            {
                Key = CanonicalizeIdentifier(view.Key),
                Name = PreserveIdentifier(view.Name),
                SchemaName = PreserveOptionalIdentifier(view.SchemaName),
                ReferencedTables = CanonicalizeList(view.ReferencedTables),
                ReferencedColumns = CanonicalizeList(view.ReferencedColumns),
                ReferencedTypes = CanonicalizeList(view.ReferencedTypes),
                ReferencedFunctions = CanonicalizeList(view.ReferencedFunctions),
                ResolvedTables = CanonicalizeList(view.ResolvedTables),
                ResolvedColumns = CanonicalizeList(view.ResolvedColumns),
                ResolvedTypes = CanonicalizeList(view.ResolvedTypes),
                ResolvedFunctions = CanonicalizeList(view.ResolvedFunctions),
                OutputColumnNames = CanonicalizeExpressions(view.OutputColumnNames, preserveOrder: true),
                OutputColumnTypes = CanonicalizeExpressions(view.OutputColumnTypes, preserveOrder: true),
                NormalizedExpressions = CanonicalizeExpressions(view.NormalizedExpressions, preserveOrder: true),
            })
            .ToArray();

        // TODO: Canonicalize view SELECT-list ordering and alias projection structure once parser-backed view shape metadata is available.
        ReplaceDefinitions(views, normalizedViews);
    }

    private void NormalizeFunctions(DatabaseModel model)
    {
        if (model.Functions is not IDictionary<string, FunctionDefinition> functions)
        {
            return;
        }

        var normalizedFunctions = functions.Values
            .Select(function => function with
            {
                Key = CanonicalizeIdentifier(function.Key),
                Name = PreserveIdentifier(function.Name),
                SchemaName = PreserveOptionalIdentifier(function.SchemaName),
                Language = CanonicalizeOptionalToken(function.Language),
                ReferencedTables = CanonicalizeList(function.ReferencedTables),
                ReferencedColumns = CanonicalizeList(function.ReferencedColumns),
                ReferencedTypes = CanonicalizeList(function.ReferencedTypes),
                ReferencedFunctions = CanonicalizeList(function.ReferencedFunctions),
                ResolvedTables = CanonicalizeList(function.ResolvedTables),
                ResolvedColumns = CanonicalizeList(function.ResolvedColumns),
                ResolvedTypes = CanonicalizeList(function.ResolvedTypes),
                ResolvedFunctions = CanonicalizeList(function.ResolvedFunctions),
                NormalizedExpressions = CanonicalizeExpressions(function.NormalizedExpressions, preserveOrder: true),
            })
            .ToArray();

        // RawBody, BodyDelimiter, and Signature are deliberately not canonicalized. They are
        // deploy-only state that must survive verbatim; only NormalizedExpressions drives diffing.
        // TODO: Canonicalize function attributes such as cost, volatility, and parallel safety once they are modeled explicitly.
        // TODO: Canonicalize function parameter and return type metadata once signature objects are represented in the semantic model.
        ReplaceDefinitions(functions, normalizedFunctions);
    }

    private void NormalizeProcedures(DatabaseModel model)
    {
        if (model.Procedures is not IDictionary<string, ProcedureDefinition> procedures)
        {
            return;
        }

        // RawBody, BodyDelimiter, and Signature are deliberately preserved verbatim.
        var normalizedProcedures = procedures.Values
            .Select(procedure => procedure with
            {
                Key = CanonicalizeIdentifier(procedure.Key),
                Name = PreserveIdentifier(procedure.Name),
                SchemaName = PreserveOptionalIdentifier(procedure.SchemaName),
                Language = CanonicalizeOptionalToken(procedure.Language),
                ReferencedTables = CanonicalizeList(procedure.ReferencedTables),
                ReferencedColumns = CanonicalizeList(procedure.ReferencedColumns),
                ReferencedTypes = CanonicalizeList(procedure.ReferencedTypes),
                ReferencedFunctions = CanonicalizeList(procedure.ReferencedFunctions),
                ResolvedTables = CanonicalizeList(procedure.ResolvedTables),
                ResolvedColumns = CanonicalizeList(procedure.ResolvedColumns),
                ResolvedTypes = CanonicalizeList(procedure.ResolvedTypes),
                ResolvedFunctions = CanonicalizeList(procedure.ResolvedFunctions),
                NormalizedExpressions = CanonicalizeExpressions(procedure.NormalizedExpressions, preserveOrder: true),
            })
            .ToArray();

        ReplaceDefinitions(procedures, normalizedProcedures);
    }

    private void NormalizeTriggers(DatabaseModel model)
    {
        if (model.Triggers is not IDictionary<string, TriggerDefinition> triggers)
        {
            return;
        }

        // RawStatement and RawWhenCondition are deliberately preserved verbatim.
        var normalizedTriggers = triggers.Values
            .Select(trigger => trigger with
            {
                Key = CanonicalizeIdentifier(trigger.Key),
                Name = PreserveIdentifier(trigger.Name),
                SchemaName = PreserveOptionalIdentifier(trigger.SchemaName),
                TableName = PreserveOptionalIdentifier(trigger.TableName),
                FunctionName = PreserveOptionalIdentifier(trigger.FunctionName),
                Timing = CanonicalizeOptionalToken(trigger.Timing),
                Events = CanonicalizeExpressions(trigger.Events, preserveOrder: true),
                ConstraintTableName = PreserveOptionalIdentifier(trigger.ConstraintTableName),
                // UPDATE OF is a set (PostgreSQL lists it in attribute order); the arguments are positional and stay as they are.
                UpdateColumns = PreserveIdentifierSet(trigger.UpdateColumns),
                OldTransitionTable = PreserveOptionalIdentifier(trigger.OldTransitionTable),
                NewTransitionTable = PreserveOptionalIdentifier(trigger.NewTransitionTable),
                NormalizedWhenCondition = NormalizeWhenCondition(model, trigger),
                ReferencedTables = CanonicalizeList(trigger.ReferencedTables),
                ReferencedFunctions = CanonicalizeList(trigger.ReferencedFunctions),
            })
            .ToArray();

        ReplaceDefinitions(triggers, normalizedTriggers);
    }

    /// <summary>
    /// The comparison form of a trigger's <c>WHEN</c> condition (G-40). It is built from the parsed condition, with the column types
    /// of the trigger's table so PostgreSQL's own <c>::text</c> on a <c>varchar</c> column can be told from one the author wrote. Only
    /// when there is no parsed condition to read is the condition's text compared, with whitespace collapsed.
    /// </summary>
    private static string? NormalizeWhenCondition(DatabaseModel model, TriggerDefinition trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger.RawWhenCondition))
        {
            return null;
        }

        var table = FindTriggerTable(model, trigger);
        string? ColumnBaseType(string column)
        {
            var type = table?.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, column, StringComparison.Ordinal))?.RawType;
            var name = ColumnTypeNormalizer.NormalizeBaseType(type);
            return name is "character varying" ? "varchar" : name;
        }

        var ast = (trigger.DeclarationNode as TriggerNode)?.WhenConditionAst;
        return TriggerWhenConditionCanonicalizer.Canonicalize(ast, ColumnBaseType) ?? CanonicalizeExpression(trigger.RawWhenCondition);
    }

    private static TableDefinition? FindTriggerTable(DatabaseModel model, TriggerDefinition trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger.TableName))
        {
            return null;
        }

        return model.Tables.Values.FirstOrDefault(table =>
            string.Equals(table.Name, trigger.TableName, StringComparison.Ordinal)
            && string.Equals(table.SchemaName ?? "public", trigger.SchemaName ?? "public", StringComparison.Ordinal));
    }

    /// <summary>
    /// Event triggers (G-127): the event name is a keyword (lower case), the tag filter a set stored upper-cased and unordered by PostgreSQL
    /// (so <see cref="EventTriggerDefinition.Tags"/> stays as written for the deployed SQL, and the comparison form is upper case, distinct, and
    /// sorted), the enabled mode one letter, and the function name an identifier as written.
    /// </summary>
    private void NormalizeEventTriggers(DatabaseModel model)
    {
        if (model.EventTriggers is not IDictionary<string, EventTriggerDefinition> eventTriggers)
        {
            return;
        }

        var normalized = eventTriggers.Values
            .Select(eventTrigger => eventTrigger with
            {
                Key = CanonicalizeIdentifier(eventTrigger.Key),
                Name = PreserveIdentifier(eventTrigger.Name),
                EventName = CanonicalizeOptionalToken(eventTrigger.EventName),
                NormalizedTags = eventTrigger.Tags
                    .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(static tag => tag.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                FunctionName = PreserveOptionalIdentifier(eventTrigger.FunctionName),
                EnabledMode = string.IsNullOrWhiteSpace(eventTrigger.EnabledMode) ? "O" : eventTrigger.EnabledMode.Trim().ToUpperInvariant(),
                ReferencedFunctions = CanonicalizeList(eventTrigger.ReferencedFunctions),
            })
            .ToArray();

        ReplaceDefinitions(eventTriggers, normalized);
    }

    /// <summary>
    /// Casts (G-131): the key and the function's arguments in comparison form (<see cref="CastTypeNames"/>: the argument types are
    /// positional, so their order is kept), the method and context as lower-case keywords, the function name an identifier as written.
    /// <see cref="CastDefinition.SourceType"/>, <see cref="CastDefinition.TargetType"/>, and <see cref="CastDefinition.FunctionArguments"/> stay as written,
    /// for the deployed SQL.
    /// </summary>
    private void NormalizeCasts(DatabaseModel model)
    {
        if (model.Casts is not IDictionary<string, CastDefinition> casts)
        {
            return;
        }

        var normalized = casts.Values
            .Select(cast => cast with
            {
                Key = CastTypeNames.CreateKey(cast.SourceType, cast.TargetType),
                FunctionName = PreserveOptionalIdentifier(cast.FunctionName),
                NormalizedFunctionArguments = cast.FunctionArguments.Select(static argument => CastTypeNames.Canonicalize(argument)).ToArray(),
                Method = CanonicalizeOptionalToken(cast.Method) ?? "function",
                Context = CanonicalizeOptionalToken(cast.Context) ?? "explicit",
                ReferencedFunctions = CanonicalizeList(cast.ReferencedFunctions),
            })
            .ToArray();

        ReplaceDefinitions(casts, normalized);
    }

    /// <summary>
    /// Operators (G-129): the key in comparison form (<see cref="OperatorNames"/>), the schema and names as identifiers as written. PostgreSQL links a
    /// commutator and a negator both ways, so an operator whose partner is in the project and names no partner of its own gets this one as its own: a live read
    /// shows the link on both, and without this a project that states it on one side only would differ from its database forever. The operand types,
    /// function, and estimators stay as written, for the deployed SQL.
    /// </summary>
    private void NormalizeOperators(DatabaseModel model)
    {
        if (model.Operators is not IDictionary<string, OperatorDefinition> operators)
        {
            return;
        }

        var normalized = operators.Values
            .Select(@operator => @operator with
            {
                Key = OperatorNames.CreateKey(@operator.SchemaName, @operator.OperatorName, @operator.LeftType, @operator.RightType),
                SchemaName = PreserveOptionalIdentifier(@operator.SchemaName),
                FunctionName = PreserveOptionalIdentifier(@operator.FunctionName),
                ReferencedFunctions = CanonicalizeList(@operator.ReferencedFunctions),
            })
            .ToDictionary(static @operator => @operator.Key, PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var @operator in normalized.Values.ToArray())
        {
            var self = DatabaseModel.CreateKey(@operator.SchemaName, @operator.OperatorName);
            if (@operator.Commutator is { Length: > 0 } commutator
                && FindOperator(normalized, model.SearchPathFor(@operator.SchemaName), commutator, @operator.RightType, @operator.LeftType) is { } commutated
                && commutated.Commutator is null)
            {
                normalized[commutated.Key] = commutated with { Commutator = self };
            }

            if (@operator.Negator is { Length: > 0 } negator
                && FindOperator(normalized, model.SearchPathFor(@operator.SchemaName), negator, @operator.LeftType, @operator.RightType) is { } negated
                && negated.Negator is null)
            {
                normalized[negated.Key] = negated with { Negator = self };
            }
        }

        ReplaceDefinitions(operators, normalized.Values.ToArray());
    }

    /// <summary>
    /// The operator named <paramref name="written"/> (<c>schema.name</c>, or a bare name that PostgreSQL finds through the search path) with these operand
    /// types, when the project declares it; a built-in or an extension's is not in the model.
    /// </summary>
    private static OperatorDefinition? FindOperator(IReadOnlyDictionary<string, OperatorDefinition> operators, IReadOnlyList<string> searchPath, string written, string? leftType, string? rightType)
    {
        var (schema, name) = OperatorNames.Split(written);
        IEnumerable<string?> schemas = schema is not null ? [schema] : searchPath.Count > 0 ? searchPath : ["public"];
        foreach (var candidate in schemas)
        {
            if (operators.TryGetValue(OperatorNames.CreateKey(candidate, name, leftType, rightType), out var found))
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Rules (G-128): the key from the relation and name, and <see cref="RuleDefinition.NormalizedDefinition"/> for the case where there is no server rendering to compare
    /// (no connection, or the rule is new): the statement with <c>OR REPLACE</c> and the semicolon dropped and its white space collapsed.
    /// </summary>
    private void NormalizeRules(DatabaseModel model)
    {
        if (model.Rules is not IDictionary<string, RuleDefinition> rules)
        {
            return;
        }

        var normalized = rules.Values
            .Select(rule => rule with
            {
                Key = RuleKeys.Create(rule.SchemaName, rule.TableName, rule.Name),
                SchemaName = PreserveOptionalIdentifier(rule.SchemaName),
                TableName = PreserveOptionalIdentifier(rule.TableName),
                NormalizedDefinition = NormalizeRuleText(rule.RawDefinition),
            })
            .ToArray();

        ReplaceDefinitions(rules, normalized);
    }

    private static readonly System.Text.RegularExpressions.Regex OrReplace = new(@"^\s*CREATE\s+OR\s+REPLACE\s+RULE\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex WhiteSpace = new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A rule's text in the form two spellings of it share: <c>CREATE RULE</c>, no trailing semicolon, one space between words.</summary>
    internal static string? NormalizeRuleText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? null
            : WhiteSpace.Replace(OrReplace.Replace(text.Trim().TrimEnd(';'), "CREATE RULE"), " ").Trim();

    /// <summary>
    /// Aggregates (G-130): the key from the schema, name, and argument types (in comparison form, like a routine's), and the options in comparison form
    /// (<see cref="AggregateOptions.Normalize"/>: types canonical, PostgreSQL's defaults left out). The arguments and options as written stay for the deployed SQL.
    /// </summary>
    private void NormalizeAggregates(DatabaseModel model)
    {
        if (model.Aggregates is not IDictionary<string, AggregateDefinition> aggregates)
        {
            return;
        }

        var normalized = aggregates.Values
            .Select(aggregate => aggregate with
            {
                Key = AggregateOptions.CreateKey(aggregate.SchemaName, aggregate.Name, aggregate.Arguments),
                SchemaName = PreserveOptionalIdentifier(aggregate.SchemaName),
                NormalizedOptions = AggregateOptions.Normalize(aggregate.DirectArgumentCount, aggregate.Options),
                ReferencedFunctions = CanonicalizeList(aggregate.ReferencedFunctions),
            })
            .ToArray();

        ReplaceDefinitions(aggregates, normalized);
    }

    /// <summary>
    /// Text search objects (G-149): the template and the parser without <c>pg_catalog.</c>, and each dictionary a configuration names as the project's own object
    /// (<c>schema.name</c>) or as a built-in (<c>simple</c>), so the way a database reports a name and the way a project writes it are one name.
    /// </summary>
    private void NormalizeTextSearch(DatabaseModel model)
    {
        if (model.TextSearchDictionaries is IDictionary<string, TextSearchDictionaryDefinition> dictionaries)
        {
            ReplaceDefinitions(
                dictionaries,
                dictionaries.Values
                    .Select(dictionary => dictionary with
                    {
                        SchemaName = PreserveOptionalIdentifier(dictionary.SchemaName),
                        NormalizedTemplate = TextSearchNames.BuiltIn(dictionary.Template),
                    })
                    .ToArray());
        }

        if (model.TextSearchConfigurations is IDictionary<string, TextSearchConfigurationDefinition> configurations)
        {
            ReplaceDefinitions(
                configurations,
                configurations.Values
                    .Select(configuration => configuration with
                    {
                        SchemaName = PreserveOptionalIdentifier(configuration.SchemaName),
                        NormalizedParser = TextSearchNames.BuiltIn(configuration.Parser),
                        NormalizedCopyFrom = configuration.CopyFrom is null ? null : TextSearchNames.Canonical(model, configuration.SchemaName, configuration.CopyFrom, dictionary: false),
                        NormalizedMappings = configuration.Mappings.ToDictionary(
                            static pair => pair.Key,
                            pair => (IReadOnlyList<string>)[.. pair.Value.Select(name => TextSearchNames.Canonical(model, configuration.SchemaName, name, dictionary: true))],
                            StringComparer.Ordinal),
                    })
                    .ToArray());
        }
    }

    /// <summary>
    /// Extended statistics (G-150): the kinds in comparison form (sorted, all three when none is written), and each expression canonicalized from its tree against
    /// the table it is about, so the way PostgreSQL prints it and the way a project writes it are one expression.
    /// </summary>
    private void NormalizeStatistics(DatabaseModel model)
    {
        if (model.Statistics is not IDictionary<string, StatisticsDefinition> statistics)
        {
            return;
        }

        var normalized = statistics.Values
            .Select(definition => definition with
            {
                SchemaName = PreserveOptionalIdentifier(definition.SchemaName),
                NormalizedKinds = StatisticsDefinition.NormalizeKinds(definition.Kinds),
                Elements = [.. definition.Elements.Select(element => element.ExpressionAst is null
                    ? element
                    : element with { NormalizedExpression = NormalizeTreeExpression(model, definition.TableSchemaName, definition.TableName, element.ExpressionAst) ?? CanonicalizeExpression(element.RawExpression) })],
            })
            .ToArray();

        ReplaceDefinitions(statistics, normalized);
    }

    /// <summary>
    /// Collations (G-148): the key and schema as identifiers, the provider in lower case. The locale is compared in the form two spellings share
    /// (<see cref="CollationLocales.Canonical"/>) by the diff, so the locale as written stays for the deployed SQL.
    /// </summary>
    private void NormalizeCollations(DatabaseModel model)
    {
        if (model.Collations is not IDictionary<string, CollationDefinition> collations)
        {
            return;
        }

        var normalized = collations.Values
            .Select(collation => collation with
            {
                SchemaName = PreserveOptionalIdentifier(collation.SchemaName),
                Provider = collation.Provider.ToLowerInvariant(),
            })
            .ToArray();

        ReplaceDefinitions(collations, normalized);
    }

    /// <summary>
    /// Publications (G-134): the options in comparison form (<see cref="PublicationOptions.Normalize"/>), each table's column list as a sorted set (PostgreSQL keeps it
    /// unordered), and each row filter built from its tree against the column types of its table (<see cref="NormalizeTreeExpression"/>), so <c>a &gt; 0</c> and the
    /// <c>(a &gt; 0)</c> that <c>pg_get_expr</c> prints are one. What was written stays for the deployed SQL.
    /// </summary>
    private void NormalizePublications(DatabaseModel model)
    {
        if (model.Publications is not IDictionary<string, PublicationDefinition> publications)
        {
            return;
        }

        var normalized = publications.Values
            .Select(publication => publication with
            {
                NormalizedOptions = PublicationOptions.Normalize(publication.Options),
                Tables = publication.Tables
                    .Select(table => table with
                    {
                        NormalizedColumns = [.. table.Columns.Order(StringComparer.Ordinal)],
                        NormalizedFilter = table.FilterAst is null
                            ? null
                            : NormalizeTreeExpression(model, table.SchemaName, table.TableName, table.FilterAst) ?? CanonicalizeExpression(table.RawFilter),
                    })
                    .ToArray(),
            })
            .ToArray();

        ReplaceDefinitions(publications, normalized);
    }

    private void NormalizePolicies(DatabaseModel model)
    {
        if (model.Policies is not IDictionary<string, PolicyDefinition> policies)
        {
            return;
        }

        var normalizedPolicies = policies.Values
            .Select(policy => policy with
            {
                Key = CanonicalizeIdentifier(policy.Key),
                Name = PreserveIdentifier(policy.Name),
                SchemaName = PreserveOptionalIdentifier(policy.SchemaName),
                TableName = PreserveOptionalIdentifier(policy.TableName),
                Command = CanonicalizeOptionalToken(policy.Command) ?? "all",
                // The TO clause is a set (PostgreSQL's polroles carries no meaningful order); Roles itself stays in the order
                // written, for deploy output.
                NormalizedRoles = PreserveIdentifierSet(policy.Roles),
                NormalizedUsingExpression = NormalizePolicyExpression(model, policy.SchemaName, policy.TableName, (policy.DeclarationNode as PolicyNode)?.UsingExpressionAst, policy.RawUsingExpression),
                NormalizedWithCheckExpression = NormalizePolicyExpression(model, policy.SchemaName, policy.TableName, (policy.DeclarationNode as PolicyNode)?.WithCheckExpressionAst, policy.RawWithCheckExpression),
                ReferencedTables = CanonicalizeList(policy.ReferencedTables),
            })
            .ToArray();

        ReplaceDefinitions(policies, normalizedPolicies);
    }

    /// <summary>
    /// The comparison form of a policy's <c>USING</c>/<c>WITH CHECK</c> expression (G-76): built from the parsed tree, with the
    /// column types of the policy's table, the same reasoning as <see cref="NormalizeWhenCondition"/>. Only when there is no
    /// parsed tree to read (a hand-built definition) is the raw text compared, with whitespace collapsed.
    /// </summary>
    private static string? NormalizePolicyExpression(DatabaseModel model, string? schemaName, string? tableName, string? astJson, string? rawExpression)
    {
        if (string.IsNullOrWhiteSpace(rawExpression))
        {
            return null;
        }

        return NormalizeTreeExpression(model, schemaName, tableName, astJson) ?? CanonicalizeExpression(rawExpression);
    }

    private void NormalizeTypes(DatabaseModel model)
    {
        if (model.Types is not IDictionary<string, TypeDefinition> types)
        {
            return;
        }

        var normalizedTypes = types.Values
            .Select(type => type with
            {
                Key = CanonicalizeIdentifier(type.Key),
                Name = PreserveIdentifier(type.Name),
                SchemaName = PreserveOptionalIdentifier(type.SchemaName),
                TypeKind = CanonicalizeOptionalToken(type.TypeKind),
                ReferencedTables = CanonicalizeList(type.ReferencedTables),
                ReferencedColumns = CanonicalizeList(type.ReferencedColumns),
                ReferencedTypes = CanonicalizeList(type.ReferencedTypes),
                ReferencedFunctions = CanonicalizeList(type.ReferencedFunctions),
                ResolvedTables = CanonicalizeList(type.ResolvedTables),
                ResolvedColumns = CanonicalizeList(type.ResolvedColumns),
                ResolvedTypes = CanonicalizeList(type.ResolvedTypes),
                ResolvedFunctions = CanonicalizeList(type.ResolvedFunctions),
                // A type name, compared as PostgreSQL compares names: a quoted name keeps its case (G-286).
                DomainBaseType = TrimOrNull(type.DomainBaseType),
                DomainIntervalFields = CanonicalizeOptionalToken(type.DomainIntervalFields),
                DomainCheckConstraints = type.DomainCheckConstraints
                    .Select(constraint => constraint with
                    {
                        Name = constraint.Name is null ? null : PreserveIdentifier(constraint.Name),
                        NormalizedExpression = NormalizeDomainCheckExpression(type, constraint, types.Values),
                    })
                    .ToArray(),
            })
            .ToArray();

        // TODO: Normalize composite type field ordering once composite members are represented semantically.
        ReplaceDefinitions(types, normalizedTypes);
    }

    /// <summary>
    /// Removes the cast of <c>VALUE</c> to <paramref name="rootBaseType"/> from a domain-over-domain CHECK's tree (G-184). PostgreSQL stores the CHECK of such a
    /// domain with <c>VALUE</c> cast to the root type (<c>VALUE::int4</c>); the project's has none, and both mean the same, so the comparison form leaves it out.
    /// </summary>
    private static string? StripValueCastToRootType(string? astJson, string? rootBaseType)
    {
        if (string.IsNullOrWhiteSpace(astJson) || string.IsNullOrWhiteSpace(rootBaseType))
        {
            return astJson;
        }

        var root = RoutineTypeNames.Canonicalize(rootBaseType);
        var tree = System.Text.Json.Nodes.JsonNode.Parse(astJson, documentOptions: PgProj.Parser.PgQueryJson.DocumentOptions);
        return tree is null ? astJson : Strip(tree, root)?.ToJsonString();

        static System.Text.Json.Nodes.JsonNode? Strip(System.Text.Json.Nodes.JsonNode node, string root)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                if (obj["TypeCast"] is System.Text.Json.Nodes.JsonObject cast
                    && cast["arg"] is System.Text.Json.Nodes.JsonObject argument
                    && argument["ColumnRef"]?["fields"] is System.Text.Json.Nodes.JsonArray fields
                    && fields.Count == 1
                    && string.Equals(fields[0]?["String"]?["sval"]?.GetValue<string>(), "value", StringComparison.OrdinalIgnoreCase)
                    && cast["typeName"]?["names"] is System.Text.Json.Nodes.JsonArray names
                    && names.Count > 0
                    && names[^1]?["String"]?["sval"]?.GetValue<string>() is { } target
                    && cast["typeName"]?["typmods"] is null
                    && cast["typeName"]?["arrayBounds"] is null
                    && string.Equals(RoutineTypeNames.Canonicalize(target), root, StringComparison.Ordinal))
                {
                    return argument.DeepClone();
                }

                foreach (var property in obj.ToArray())
                {
                    if (property.Value is { } child && Strip(child, root) is { } replaced && !ReferenceEquals(replaced, child))
                    {
                        obj[property.Key] = replaced;
                    }
                }

                return obj;
            }

            if (node is System.Text.Json.Nodes.JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is { } child && Strip(child, root) is { } replaced && !ReferenceEquals(replaced, child))
                    {
                        array[index] = replaced;
                    }
                }
            }

            return node;
        }
    }

    /// <summary>
    /// The type that <c>VALUE</c> has in a domain's CHECK as PostgreSQL stores it (G-184): the domain's base type, and, when that is another domain, the
    /// base type of that one, down to the first that is not a domain. PostgreSQL casts <c>VALUE</c> to that root type (<c>VALUE::int4</c> for a domain over
    /// a domain over <c>int</c>), so the project's CHECK, which has no cast, only compares equal when <c>VALUE</c> is typed the same way.
    /// </summary>
    private static string? ResolveRootDomainBaseType(IEnumerable<TypeDefinition> allTypes, TypeDefinition domain)
    {
        var domains = allTypes.Where(static candidate => candidate.DomainBaseType is not null).ToArray();
        var current = domain.DomainBaseType;
        var schema = domain.SchemaName ?? "public";
        for (var depth = 0; depth < 32 && current is not null; depth++)
        {
            var separator = current.LastIndexOf('.');
            var candidateSchema = separator < 0 ? schema : current[..separator].Trim('"');
            var candidateName = (separator < 0 ? current : current[(separator + 1)..]).Trim('"');
            var next = domains.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, candidateName, StringComparison.Ordinal)
                && string.Equals(candidate.SchemaName ?? "public", candidateSchema, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                return current;
            }

            current = next.DomainBaseType;
            schema = next.SchemaName ?? "public";
        }

        return current;
    }

    /// <summary>
    /// The comparison form of a domain's CHECK expression (G-77), built the same way a table's own CHECK is
    /// (<see cref="NormalizeTreeExpression"/>) but with no table to resolve a column against - a domain CHECK's
    /// only possible identifier is the pseudo-column <c>VALUE</c>, whose type is the domain's own base type.
    /// </summary>
    private static string? NormalizeDomainCheckExpression(TypeDefinition type, DomainCheckConstraintDefinition constraint, IEnumerable<TypeDefinition> allTypes)
    {
        if (string.IsNullOrWhiteSpace(constraint.ExpressionAst))
        {
            return null;
        }

        var rootBaseType = ResolveRootDomainBaseType(allTypes, type);
        var expressionAst = string.Equals(rootBaseType, type.DomainBaseType, StringComparison.OrdinalIgnoreCase)
            ? constraint.ExpressionAst
            : StripValueCastToRootType(constraint.ExpressionAst, rootBaseType);

        return TriggerWhenConditionCanonicalizer.Canonicalize(
            expressionAst,
            name => string.Equals(name, "value", StringComparison.OrdinalIgnoreCase) ? type.DomainBaseType : null)
            ?? CanonicalizeExpression(constraint.RawExpression);
    }

    /// <summary>
    /// The comparison form of a <c>CHECK</c> expression, an index predicate, or an index key expression (G-73), built from its parsed tree
    /// with the column types of its table so PostgreSQL's own <c>::text</c> on a <c>varchar</c> column can be told from one the author
    /// wrote. Null when there is no tree, in which case the comparison falls back to the expression text.
    /// </summary>
    private static string? NormalizeTreeExpression(DatabaseModel model, string? schemaName, string? tableName, string? astJson)
    {
        if (string.IsNullOrWhiteSpace(astJson))
        {
            return null;
        }

        var table = string.IsNullOrWhiteSpace(tableName)
            ? null
            : model.Tables.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, tableName, StringComparison.Ordinal)
                && string.Equals(candidate.SchemaName ?? "public", schemaName ?? "public", StringComparison.Ordinal));
        return TriggerWhenConditionCanonicalizer.Canonicalize(astJson, name => ColumnBaseType(table, name));
    }

    private void NormalizeStandaloneConstraints(DatabaseModel model)
    {
        if (model.Constraints is not IDictionary<string, ConstraintDefinition> constraints)
        {
            return;
        }

        var normalizedConstraints = constraints.Values
            .Select(constraint => constraint with
            {
                Key = CanonicalizeIdentifier(constraint.Key),
                Name = PreserveIdentifier(constraint.Name),
                SchemaName = PreserveOptionalIdentifier(constraint.SchemaName),
                TableName = PreserveOptionalIdentifier(constraint.TableName),
                ConstraintType = CanonicalizeOptionalToken(constraint.ConstraintType),
                PrimaryKeyColumns = PreserveOrderedIdentifierList(constraint.PrimaryKeyColumns),
                // Kept in source order: a publish emits UNIQUE (...) from this list, and the order becomes the unique index's column order.
                // The diff compares it as a set (DiffEngine's AddListDifference), so an order-only difference is not a change (G-112).
                UniqueColumns = PreserveOrderedIdentifierList(constraint.UniqueColumns),
                ForeignKeyReferencedTable = PreserveOptionalIdentifier(constraint.ForeignKeyReferencedTable),
                ForeignKeyReferencedColumns = PreserveOrderedIdentifierList(constraint.ForeignKeyReferencedColumns),
                ForeignKeyColumns = PreserveOrderedIdentifierList(constraint.ForeignKeyColumns),
                ForeignKeyReferencedSchemaName = PreserveOptionalIdentifier(constraint.ForeignKeyReferencedSchemaName),
                ForeignKeyUpdateAction = CanonicalizeOptionalToken(constraint.ForeignKeyUpdateAction),
                ForeignKeyDeleteAction = CanonicalizeOptionalToken(constraint.ForeignKeyDeleteAction),
                // Kept in the order written, which a publish emits; the diff compares it as a set (G-182).
                ForeignKeyDeleteSetColumns = PreserveOrderedIdentifierList(constraint.ForeignKeyDeleteSetColumns),
                ForeignKeyMatchType = CanonicalizeOptionalToken(constraint.ForeignKeyMatchType),
                ReferencedTables = CanonicalizeList(constraint.ReferencedTables),
                ReferencedColumns = CanonicalizeList(constraint.ReferencedColumns),
                ReferencedTypes = CanonicalizeList(constraint.ReferencedTypes),
                ReferencedFunctions = CanonicalizeList(constraint.ReferencedFunctions),
                ResolvedTables = CanonicalizeList(constraint.ResolvedTables),
                ResolvedColumns = CanonicalizeList(constraint.ResolvedColumns),
                ResolvedTypes = CanonicalizeList(constraint.ResolvedTypes),
                ResolvedFunctions = CanonicalizeList(constraint.ResolvedFunctions),
                NormalizedExpressions = CanonicalizeExpressions(constraint.NormalizedExpressions, preserveOrder: false),
                NormalizedCheckExpression = NormalizeTreeExpression(model, constraint.SchemaName, constraint.TableName, constraint.DeclarationNode?.CheckExpressionAst),
                ExclusionAccessMethod = CanonicalizeOptionalToken(constraint.ExclusionAccessMethod),
                ExclusionElements = constraint.ExclusionElements
                    .Select(element => element with
                    {
                        Name = element.Name is null ? null : PreserveIdentifier(element.Name),
                        NormalizedExpression = element.ExpressionAst is null
                            ? null
                            : NormalizeTreeExpression(model, constraint.SchemaName, constraint.TableName, element.ExpressionAst) ?? CanonicalizeExpression(element.RawExpression ?? string.Empty),
                    })
                    .ToArray(),
            })
            .ToArray();

        ReplaceDefinitions(constraints, normalizedConstraints, keepSourceOrder: true);
    }

    private void NormalizeStandaloneIndexes(DatabaseModel model)
    {
        if (model.Indexes is not IDictionary<string, IndexDefinition> indexes)
        {
            return;
        }

        var normalizedIndexes = indexes.Values
            .Select(index => index with
            {
                Key = CanonicalizeIdentifier(index.Key),
                Name = PreserveIdentifier(index.Name),
                SchemaName = PreserveOptionalIdentifier(index.SchemaName),
                TableName = PreserveOptionalIdentifier(index.TableName),
                AccessMethod = CanonicalizeOptionalToken(index.AccessMethod),
                ReferencedTables = CanonicalizeList(index.ReferencedTables),
                ReferencedColumns = CanonicalizeList(index.ReferencedColumns),
                ReferencedTypes = CanonicalizeList(index.ReferencedTypes),
                ReferencedFunctions = CanonicalizeList(index.ReferencedFunctions),
                ResolvedTables = CanonicalizeList(index.ResolvedTables),
                ResolvedColumns = CanonicalizeList(index.ResolvedColumns),
                ResolvedTypes = CanonicalizeList(index.ResolvedTypes),
                ResolvedFunctions = CanonicalizeList(index.ResolvedFunctions),
                NormalizedExpressions = CanonicalizeExpressions(index.NormalizedExpressions, preserveOrder: false),
                NormalizedPredicateExpression = NormalizeTreeExpression(model, index.SchemaName, index.TableName, index.DeclarationNode?.PredicateAst),
                IncludeColumns = PreserveOrderedIdentifierList(index.IncludeColumns),
                KeyElements = index.KeyElements
                    .Select((element, position) => element with
                    {
                        NormalizedExpression = element.RawExpression is null
                            ? null
                            : NormalizeTreeExpression(
                                model,
                                index.SchemaName,
                                index.TableName,
                                index.DeclarationNode is { } declaration && position < declaration.KeyElements.Count ? declaration.KeyElements[position].ExpressionAst : null),
                        Name = element.Name is null ? null : PreserveIdentifier(element.Name),
                        CollationName = KeyCollation(model, index, element),
                        OperatorClassName = element.OperatorClassName is null ? null : PreserveIdentifier(element.OperatorClassName),
                    })
                    .ToArray(),
            })
            .ToArray();

        ReplaceDefinitions(indexes, normalizedIndexes, keepSourceOrder: true);
    }

    /// <summary>
    /// The collation an index key states, left out when it is the collation of the column the key indexes (G-155): PostgreSQL does not print a <c>COLLATE</c> that equals the
    /// column's own, so the live index never has it and a project that repeats it would differ from the database after every publish. A key on an expression keeps its collation.
    /// </summary>
    private static string? KeyCollation(DatabaseModel model, IndexDefinition index, IndexKeyElementDefinition element)
    {
        if (element.CollationName is null)
        {
            return null;
        }

        var collation = PreserveIdentifier(element.CollationName);
        if (element.Name is not null
            && element.RawExpression is null
            && !string.IsNullOrWhiteSpace(index.TableName)
            && model.Tables.TryGetValue(DatabaseModel.CreateKey(index.SchemaName, index.TableName), out var table)
            && table.Columns.FirstOrDefault(column => string.Equals(column.Name, element.Name, StringComparison.Ordinal)) is { } indexed
            && CollationNames.Same(collation, indexed.CollationName))
        {
            return null;
        }

        return collation;
    }

    // A name is compared exactly, as PostgreSQL compares names (G-206): pg_query has already folded an unquoted one, and PostgreSQL keeps the
    // case of a quoted one, so a name is trimmed and otherwise left as it arrived (G-70).
    private static string PreserveIdentifier(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return id.Trim();
    }

    private static string? PreserveOptionalIdentifier(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : id.Trim();

    /// <summary>An ordered identifier list (key column order carries meaning) with duplicates, ignoring case, removed.</summary>
    private static IReadOnlyList<string> PreserveOrderedIdentifierList(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        return items
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .DistinctBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>An identifier set: sorted by its lower-cased form, so order never depends on how it was written, with duplicates removed.</summary>
    private static IReadOnlyList<string> PreserveIdentifierSet(IEnumerable<string> items)
        => PreserveOrderedIdentifierList(items)
            .OrderBy(static value => value.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static string CanonicalizeIdentifier(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var segments = id.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join('.', segments.Select(CanonicalizeIdentifierSegment));
    }

    private static IReadOnlyList<T> CanonicalizeList<T>(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (typeof(T) == typeof(string))
        {
            return items
                .Cast<string?>()
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => CanonicalizeIdentifier(value!))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .Cast<T>()
                .ToArray();
        }

        return items
            .Where(static item => item is not null)
            .Select(item => (Item: item, SortKey: GetCanonicalSortKey(item)))
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.SortKey))
            .OrderBy(static entry => entry.SortKey, StringComparer.Ordinal)
            .DistinctBy(static entry => entry.SortKey, StringComparer.Ordinal)
            .Select(static entry => entry.Item)
            .ToArray();
    }

    /// <summary>
    /// Canonicalizes an identifier list while preserving its original order. Use this for
    /// order-sensitive column lists (e.g. primary key and foreign key column sequences) where
    /// re-sorting via <see cref="CanonicalizeList{T}"/> would destroy meaningful key order.
    /// </summary>
    private static IReadOnlyList<string> CanonicalizeOrderedIdentifierList(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        return items
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => CanonicalizeIdentifier(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string CanonicalizeExpression(string expr)
        => ExpressionNormalizationPass.NormalizeExpression(expr);

    private static IReadOnlyList<string> CanonicalizeExpressions(IEnumerable<string> expressions, bool preserveOrder)
    {
        ArgumentNullException.ThrowIfNull(expressions);

        var canonicalized = expressions
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CanonicalizeExpression)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal);

        return preserveOrder
            ? canonicalized.ToArray()
            : canonicalized.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Re-inserts the normalized definitions, sorted by key unless <paramref name="keepSourceOrder"/> is set. Constraints and indexes keep
    /// the order the project declares them in (file order, then statement order), because a publish emits them in this order among
    /// objects that have no dependency between them, and the source order is what it must keep (G-112).
    /// </summary>
    private static void ReplaceDefinitions<TDefinition>(
        IDictionary<string, TDefinition> definitions,
        IEnumerable<TDefinition> normalizedDefinitions,
        bool keepSourceOrder = false)
        where TDefinition : ISemanticNode
    {
        var ordered = keepSourceOrder
            ? normalizedDefinitions.ToArray()
            : normalizedDefinitions
                .OrderBy(static definition => CanonicalizeIdentifier(definition.Key), StringComparer.Ordinal)
                .ToArray();

        definitions.Clear();

        foreach (var definition in ordered)
        {
            definitions[definition.Key] = definition;
        }
    }

    private static void ReportDuplicateColumns(TableDefinition table, IList<SemanticDiagnostic>? diagnostics)
    {
        if (diagnostics is null)
        {
            return;
        }

        var duplicates = table.Columns
            .GroupBy(static column => CanonicalizeIdentifier(column.Name), StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .ToArray();

        foreach (var duplicate in duplicates)
        {
            diagnostics.Add(SemanticDiagnostics.Error(
                $"Table '{table.Key}' contains duplicate columns after diff normalization: {duplicate.Key}.",
                duplicate.Skip(1).First().Location ?? table.Location,
                nameof(SemanticDiffNormalizationPass)));
        }
    }

    private static string CanonicalizeIdentifierSegment(string id)
    {
        var trimmed = id.Trim();
        // A key keeps its names exactly: pg_query has already folded an unquoted name, and PostgreSQL compares names exactly (G-206).
        return trimmed;
    }

    private static string? CanonicalizeOptionalIdentifier(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : CanonicalizeIdentifier(id);

    private static string? CanonicalizeOptionalToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? TrimOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetCanonicalSortKey<T>(T item)
        => item switch
        {
            null => string.Empty,
            ISemanticNode node => CanonicalizeIdentifier(node.Key),
            _ => item?.ToString()?.Trim() ?? string.Empty,
        };
}
