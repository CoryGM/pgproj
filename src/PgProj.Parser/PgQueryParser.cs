using System.Globalization;
using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    private readonly string _defaultSchema;

    public PgQueryParser()
        : this(DefaultSchema.Name)
    {
    }

    /// <summary>
    /// A parser that gives a name written without a schema <paramref name="defaultSchema"/>: the first schema of a project's
    /// <c>searchPath</c> (G-93), <c>public</c> by default (G-114).
    /// </summary>
    public PgQueryParser(string defaultSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultSchema);
        _defaultSchema = defaultSchema;
    }

    /// <summary>Parses on <see cref="LargeStack"/>, since the readers walk the tree recursively (G-272).</summary>
    public SqlSyntaxTree Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return LargeStack.Run(() => ParseOnLargeStack(sql));
    }

    private SqlSyntaxTree ParseOnLargeStack(string sql)
    {

        if (string.IsNullOrWhiteSpace(sql))
        {
            return new SqlSyntaxTree(
                sql,
                null,
                [],
                [],
                [new SqlDiagnostic(SqlDiagnosticSeverity.Warning, "SQL text was empty.", nameof(PgQueryParser))]);
        }

        PgQueryInterop.NativeParseResult parseResult = default;
        PgQueryInterop.NativeProtobufParseResult protobufResult = default;
        var parseAllocated = false;
        var protobufAllocated = false;
        var diagnostics = new List<SqlDiagnostic>();

        try
        {
            try
            {
                parseResult = PgQueryInterop.Parse(sql);
                parseAllocated = true;

                protobufResult = PgQueryInterop.ParseProtobuf(sql);
                protobufAllocated = true;
            }
            catch (DllNotFoundException exception)
            {
                return CreateInteropFailureTree(sql, exception);
            }
            catch (BadImageFormatException exception)
            {
                return CreateInteropFailureTree(sql, exception);
            }
            catch (EntryPointNotFoundException exception)
            {
                return CreateInteropFailureTree(sql, exception);
            }

            AddStandardErrorDiagnostic(PgQueryInterop.ReadNullableUtf8String(parseResult.StandardErrorBuffer), diagnostics);
            AddStandardErrorDiagnostic(PgQueryInterop.ReadNullableUtf8String(protobufResult.StandardErrorBuffer), diagnostics);
            // Both calls parse the same text, so they report the same error; it is reported once (G-123).
            var parseError = PgQueryInterop.ReadError(parseResult.Error);
            AddNativeErrorDiagnostic(sql, parseError, "pg_query_parse", diagnostics);
            if (parseError is null)
            {
                AddNativeErrorDiagnostic(sql, PgQueryInterop.ReadError(protobufResult.Error), "pg_query_parse_protobuf", diagnostics);
            }

            var rawJsonAst = PgQueryInterop.ReadParseTree(sql, parseResult);
            var rawProtobufAst = PgQueryInterop.ReadBytes(protobufResult.ParseTree);
            var nodes = diagnostics.Any(static diagnostic => diagnostic.Severity == SqlDiagnosticSeverity.Error)
                ? []
                : DefaultSchema.Apply(MapNodes(sql, rawJsonAst, diagnostics), _defaultSchema);

            return new SqlSyntaxTree(sql, rawJsonAst, rawProtobufAst, nodes, diagnostics);
        }
        finally
        {
            if (protobufAllocated)
            {
                PgQueryInterop.FreeProtobufParseResult(protobufResult);
            }

            if (parseAllocated)
            {
                PgQueryInterop.FreeParseResult(parseResult);
            }
        }
    }

    private static SqlSyntaxTree CreateInteropFailureTree(string sql, Exception exception)
    {
        return new SqlSyntaxTree(
            sql,
            null,
            [],
            [],
            [new SqlDiagnostic(SqlDiagnosticSeverity.Error, $"Unable to load libpg_query: {exception.Message}", exception.GetType().Name)]);
    }

    private static void AddStandardErrorDiagnostic(string? standardErrorBuffer, ICollection<SqlDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(standardErrorBuffer))
        {
            return;
        }

        diagnostics.Add(new SqlDiagnostic(SqlDiagnosticSeverity.Warning, standardErrorBuffer.Trim(), "libpg_query stderr"));
    }

    /// <summary>
    /// A libpg_query error, placed in <paramref name="sql"/> by its cursor position (G-123). The error's own line number is the line of
    /// libpg_query's C source that raised it, the same for every syntax error, so it is not used.
    /// </summary>
    private static void AddNativeErrorDiagnostic(string sql, PgQueryInterop.NativeError? nativeError, string source, ICollection<SqlDiagnostic> diagnostics)
    {
        if (nativeError is null)
        {
            return;
        }

        var cursorPosition = nativeError.CursorPosition > 0 ? nativeError.CursorPosition : (int?)null;
        var position = cursorPosition is { } cursor ? SqlTextPosition.OfCharacter(sql, cursor) : null;
        var end = cursorPosition is { } endCursor && SqlTextPosition.IndexOfCharacter(sql, endCursor) is { } startIndex
            ? SqlTextPosition.OfIndex(sql, FindErrorEnd(sql, startIndex))
            : ((int Line, int Column)?)null;
        diagnostics.Add(new SqlDiagnostic(
            SqlDiagnosticSeverity.Error,
            nativeError.Message is { } nativeMessage
                ? SecretRedactor.RedactErrorMessage(nativeMessage, sql, cursorPosition)
                : "libpg_query reported an unknown error.",
            source,
            position?.Line,
            cursorPosition)
        {
            Column = position?.Column,
            EndLine = end?.Line,
            EndColumn = end?.Column,
        });
    }

    /// <summary>
    /// Where the error that libpg_query places at the UTF-16 index <paramref name="start"/> ends (G-308). The cursor is the start of the
    /// token the parser stopped at, so the end is the end of that token, from PostgreSQL's lexer. At the end of the text the error is
    /// zero-width. When the lexer cannot read the text (an unterminated string, quoted name, or comment, which run to the end of it) or
    /// has no token there, the end is that of the unterminated construct, or of the run of non-blank characters.
    /// </summary>
    private static int FindErrorEnd(string sql, int start)
    {
        if (start >= sql.Length)
        {
            return sql.Length;
        }

        if (SourceText.Scan(sql) is { } tokens)
        {
            foreach (var (tokenStart, tokenEnd) in tokens)
            {
                if (tokenStart == start && tokenEnd > start)
                {
                    return tokenEnd;
                }

                if (tokenStart > start)
                {
                    break;
                }
            }
        }

        if (sql[start] is '\'' or '"' || sql.AsSpan(start).StartsWith("/*", StringComparison.Ordinal))
        {
            return UnmodeledStatementReader.TrimStatementEnd(sql, start, sql.Length);
        }

        var end = start;
        while (end < sql.Length && !char.IsWhiteSpace(sql[end]))
        {
            end++;
        }

        return Math.Max(end, start + (char.IsHighSurrogate(sql[start]) && start + 1 < sql.Length ? 2 : 1));
    }

    /// <summary>
    /// A diagnostic placed from the UTF-16 index <paramref name="startIndex"/> to <paramref name="endIndex"/> of <paramref name="sql"/> (G-308);
    /// an end before the start is taken as the start.
    /// </summary>
    private static SqlDiagnostic LocatedAt(SqlDiagnosticSeverity severity, string message, string source, string sql, int startIndex, int endIndex)
    {
        var start = SqlTextPosition.OfIndex(sql, startIndex);
        var end = SqlTextPosition.OfIndex(sql, Math.Max(startIndex, endIndex));
        return new SqlDiagnostic(severity, message, source, start.Line)
        {
            Column = start.Column,
            EndLine = end.Line,
            EndColumn = end.Column,
        };
    }

    private static IReadOnlyList<SqlNode> MapNodes(string sql, string? rawJsonAst, ICollection<SqlDiagnostic> diagnostics)
    {
        var previousContext = s_context;
        s_context = new ParseContext(sql, diagnostics);
        try
        {
            return MapStatements(sql, rawJsonAst, diagnostics);
        }
        finally
        {
            s_context = previousContext;
        }
    }

    private static IReadOnlyList<SqlNode> MapStatements(string sql, string? rawJsonAst, ICollection<SqlDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(rawJsonAst))
        {
            return [];
        }

        try
        {
            using var document = PgQueryJson.Parse(rawJsonAst);
            var nodes = new List<SqlNode>();
            var checkDepth = PgQueryJson.DepthOf(rawJsonAst) > PgQueryJson.MaxStatementDepth;

            if (TryGetDirectProperty(document.RootElement, "stmts", out var statements)
                && statements.ValueKind == JsonValueKind.Array)
            {
                foreach (var statement in statements.EnumerateArray())
                {
                    if (checkDepth && PgQueryJson.DepthOf(statement) > PgQueryJson.MaxStatementDepth)
                    {
                        ReportTooDeepStatement(sql, statement, diagnostics);
                        continue;
                    }

                    if (TryGetDirectProperty(statement, "stmt", out var statementBody)
                        && UnmodeledStatementReader.Classify(statementBody) is { } unmodeled)
                    {
                        ReportUnmodeledStatement(sql, statement, unmodeled, diagnostics);
                        if (!unmodeled.ReadStatement)
                        {
                            continue;
                        }
                    }

                    if (TryGetDirectProperty(statement, "stmt", out var modeledBody))
                    {
                        ReportUnreadClauses(sql, statement, modeledBody, diagnostics);
                    }

                    var statementSql = GetStatementSql(sql, statement);
                    var statementOffset = GetStatementOffset(sql, statement);
                    s_context!.StatementSql = statementSql;
                    s_context.StatementOffset = statementOffset;
                    var firstNode = nodes.Count;
                    VisitElement(
                        statement,
                        nodes,
                        currentTableName: null,
                        currentSchemaName: null,
                        currentColumnName: null,
                        statementSql,
                        statementOffset);
                    PlaceNodes(sql, statement, nodes, firstNode);
                }
            }
            else
            {
                VisitElement(document.RootElement, nodes, currentTableName: null, currentSchemaName: null, currentColumnName: null, sql);
            }

            RemoveRedundantCreateTableKeys(nodes);
            return nodes;
        }
        catch (JsonException exception)
        {
            // Never a warning: the file's statements are lost, so build must fail rather than deploy without them (G-272).
            diagnostics.Add(new SqlDiagnostic(
                SqlDiagnosticSeverity.Error,
                $"pgproj could not read the parse tree of this file, so none of its statements were read: {exception.Message}",
                nameof(PgQueryParser)));

            return [];
        }
    }

    /// <summary>
    /// Gives each node the statement made (from <paramref name="firstNode"/> on) the statement's range, without its <c>;</c> (G-309e), unless it has a place
    /// of its own already (a column, a constraint).
    /// </summary>
    private static void PlaceNodes(string sql, JsonElement rawStatement, List<SqlNode> nodes, int firstNode)
    {
        if (nodes.Count == firstNode)
        {
            return;
        }

        var location = TryGetDirectInt32Property(rawStatement, "stmt_location", out var statementLocation) ? statementLocation : 0;
        var length = TryGetDirectInt32Property(rawStatement, "stmt_len", out var statementLength) ? statementLength : 0;
        var range = UnmodeledStatementReader.SourceRangeOf(sql, location, length);
        for (var index = firstNode; index < nodes.Count; index++)
        {
            if (nodes[index].Range is null)
            {
                nodes[index] = nodes[index] with { Range = range };
            }
        }
    }

    /// <summary>
    /// The range of the name, qualified or not (<c>app.customer</c>, <c>"My T"</c>), that starts at the parse tree's <paramref name="location"/> in the
    /// statement being read (G-309e); null when the location is not in it.
    /// </summary>
    private static SourceRange? NameRangeAt(int? location)
        => RangeAt(location, static (statementSql, tokens) =>
        {
            var end = -1;
            var expectName = true;
            foreach (var (start, tokenEnd) in tokens)
            {
                if (expectName)
                {
                    end = tokenEnd;
                    expectName = false;
                }
                else if (tokenEnd - start == 1 && statementSql[start] == '.')
                {
                    expectName = true;
                }
                else
                {
                    break;
                }
            }

            return end;
        });

    /// <summary>
    /// The range of a constraint that starts at <paramref name="location"/> (G-309e): <c>CONSTRAINT name</c> when it is named, else the word that starts it
    /// (<c>REFERENCES</c>, <c>PRIMARY</c>, <c>CHECK</c>, ...).
    /// </summary>
    private static SourceRange? ConstraintRangeAt(int? location)
        => RangeAt(location, static (statementSql, tokens) =>
        {
            var end = -1;
            var named = false;
            foreach (var (start, tokenEnd) in tokens)
            {
                if (end < 0)
                {
                    end = tokenEnd;
                    named = statementSql.AsSpan(start, tokenEnd - start).Equals("constraint", StringComparison.OrdinalIgnoreCase);
                    if (!named)
                    {
                        break;
                    }
                }
                else
                {
                    end = tokenEnd;
                    break;
                }
            }

            return end;
        });

    /// <summary>The parse tree's <c>location</c> of <paramref name="element"/>, or of the <paramref name="wrapper"/> object it holds (<c>{"ColumnDef": {...}}</c>).</summary>
    private static int? LocationOf(JsonElement element, string wrapper)
        => TryGetDirectInt32Property(Unwrap(element, wrapper), "location", out var location) && location >= 0 ? location : null;

    /// <summary>The object <paramref name="element"/> wraps as <paramref name="wrapper"/>, or <paramref name="element"/> itself.</summary>
    private static JsonElement Unwrap(JsonElement element, string wrapper)
        => element.ValueKind == JsonValueKind.Object && TryGetDirectProperty(element, wrapper, out var inner) ? inner : element;

    /// <summary>A range from <paramref name="location"/> (an index in the file) to the end <paramref name="findEnd"/> finds in the statement's tokens from there.</summary>
    private static SourceRange? RangeAt(int? location, Func<string, IEnumerable<(int Start, int End)>, int> findEnd)
    {
        if (location is not int at || s_context is not { } context)
        {
            return null;
        }

        var position = at - context.StatementOffset;
        if (position < 0 || position >= context.StatementSql.Length)
        {
            return null;
        }

        var end = findEnd(context.StatementSql, SourceText.TokensFrom(context.StatementSql, position));
        return end <= position ? null : SourceRange.Of(context.Sql, at, context.StatementOffset + end);
    }

    /// <summary>A statement nested deeper than pgproj reads is an error naming it; the rest of the file is read (G-272).</summary>
    private static void ReportTooDeepStatement(string sql, JsonElement rawStatement, ICollection<SqlDiagnostic> diagnostics)
    {
        var location = TryGetDirectInt32Property(rawStatement, "stmt_location", out var statementLocation) ? statementLocation : 0;
        var length = TryGetDirectInt32Property(rawStatement, "stmt_len", out var statementLength) ? statementLength : 0;
        var range = UnmodeledStatementReader.DescribeWithRange(sql, location, length);
        diagnostics.Add(new SqlDiagnostic(
            SqlDiagnosticSeverity.Error,
            $"This statement is nested more than {PgQueryJson.MaxStatementDepth:N0} levels deep, deeper than pgproj reads, so it was not read: {range.Snippet}",
            nameof(PgQueryParser),
            range.Line)
        {
            Column = range.Column,
            EndLine = range.EndLine,
            EndColumn = range.EndColumn,
        });
    }

    /// <summary>A warning naming a top-level statement pgproj does not model (G-106), quoted and with its line.</summary>
    /// <summary>A clause of a modeled statement that no reader asks for is dropped from what is deployed (G-227): warn, as for a statement that is not modeled.</summary>
    private static void ReportUnreadClauses(string sql, JsonElement rawStatement, JsonElement statementBody, ICollection<SqlDiagnostic> diagnostics)
    {
        foreach (var message in ClauseCoverage.Find(statementBody))
        {
            var location = TryGetDirectInt32Property(rawStatement, "stmt_location", out var statementLocation) ? statementLocation : 0;
            var length = TryGetDirectInt32Property(rawStatement, "stmt_len", out var statementLength) ? statementLength : 0;
            var range = UnmodeledStatementReader.DescribeWithRange(sql, location, length);
            diagnostics.Add(new SqlDiagnostic(SqlDiagnosticSeverity.Warning, $"{message} {range.Snippet}", ClauseCoverage.DiagnosticSource, range.Line)
            {
                Column = range.Column,
                EndLine = range.EndLine,
                EndColumn = range.EndColumn,
            });
        }
    }

    private static void ReportUnmodeledStatement(
        string sql,
        JsonElement rawStatement,
        UnmodeledStatementReader.Finding unmodeled,
        ICollection<SqlDiagnostic> diagnostics)
    {
        var location = TryGetDirectInt32Property(rawStatement, "stmt_location", out var statementLocation) ? statementLocation : 0;
        var length = TryGetDirectInt32Property(rawStatement, "stmt_len", out var statementLength) ? statementLength : 0;
        var range = UnmodeledStatementReader.DescribeWithRange(sql, location, length);
        diagnostics.Add(new SqlDiagnostic(
            SqlDiagnosticSeverity.Warning,
            $"{unmodeled.Kind} {unmodeled.Consequence}: {range.Snippet}",
            UnmodeledStatementReader.DiagnosticSource,
            range.Line)
        {
            Column = range.Column,
            EndLine = range.EndLine,
            EndColumn = range.EndColumn,
        });
    }

    private static void VisitElement(
        JsonElement element,
        ICollection<SqlNode> nodes,
        string? currentTableName,
        string? currentSchemaName,
        string? currentColumnName,
        string statementSql,
        int statementOffset = 0)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "CreateStmt":
                            var tableNode = TryCreateTableNode(property.Value, statementSql, statementOffset);
                            if (tableNode is not null)
                            {
                                nodes.Add(tableNode);
                                var firstChildIndex = nodes.Count;
                                VisitElement(property.Value, nodes, tableNode.Name, tableNode.SchemaName, currentColumnName: null, statementSql, statementOffset);
                                MarkCreateTableConstraints(nodes, firstChildIndex, isPartitionOfList: tableNode.PartitionElementsText is not null);
                                continue;
                            }

                            break;
                        case "AlterTableStmt":
                            if (TryGetRelationIdentity(property.Value, "relation", out var alterTableName, out var alterSchemaName))
                            {
                                AddAlterTableCommandNodes(property.Value, nodes, alterTableName, alterSchemaName, statementSql);
                                VisitElement(property.Value, nodes, alterTableName, alterSchemaName, currentColumnName: null, statementSql, statementOffset);
                                continue;
                            }

                            break;
                        case "AlterSeqStmt":
                            // ALTER SEQUENCE ... OWNED BY is modeled (G-236); any other option is reported as before.
                            AddIfNotNull(nodes, ReadAlterSequenceOwnedBy(property.Value));
                            continue;
                        case "AlterOwnerStmt":
                            AddIfNotNull(nodes, OwnerStatementReader.ReadAlterOwner(property.Value));

                            // Its object and role are names, not statements of the file.
                            continue;
                        case "CreateSchemaStmt":
                            // The schema is declared, so it is created even with nothing in it (G-284); `CREATE SCHEMA name AUTHORIZATION role` states the
                            // schema's owner; its elements are read below as before.
                            AddIfNotNull(nodes, OwnerStatementReader.ReadCreateSchemaName(property.Value));
                            AddIfNotNull(nodes, OwnerStatementReader.ReadCreateSchema(property.Value));
                            break;
                        case "ColumnDef":
                            var columnNode = TryCreateColumnNode(property.Value, currentTableName, currentSchemaName, statementSql);
                            // A column with no type is options for a column the table takes from its type or parent (WITH OPTIONS), which its table node carries (G-136, G-266).
                            if (columnNode is { DataType: not null })
                            {
                                nodes.Add(columnNode);
                            }

                            VisitElement(property.Value, nodes, currentTableName, currentSchemaName, columnNode?.Name ?? currentColumnName, statementSql, statementOffset);
                            continue;
                        case "ViewStmt":
                            var viewNode = TryCreateViewNode(property.Value, statementSql);
                            if (viewNode is not null)
                            {
                                nodes.Add(viewNode);
                            }

                            break;
                        case "CreateTableAsStmt":
                            var materializedViewNode = TryCreateMaterializedViewNode(property.Value, statementSql);
                            if (materializedViewNode is not null)
                            {
                                nodes.Add(materializedViewNode);
                            }

                            break;
                        case "CreateFunctionStmt":
                            if (TryGetDirectBooleanProperty(property.Value, "is_procedure", out var isProcedure) && isProcedure)
                            {
                                var procedureNode = TryCreateProcedureNode(property.Value, statementSql);
                                if (procedureNode is not null)
                                {
                                    nodes.Add(procedureNode);
                                }

                                break;
                            }

                            var functionNode = TryCreateFunctionNode(property.Value, statementSql);
                            if (functionNode is not null)
                            {
                                nodes.Add(functionNode);
                            }

                            break;
                        case "CreateTrigStmt":
                            var triggerNode = TryCreateTriggerNode(property.Value, statementSql);
                            if (triggerNode is not null)
                            {
                                nodes.Add(triggerNode);
                            }

                            break;
                        case "CreateEventTrigStmt":
                            if (TryCreateEventTriggerNode(property.Value) is { } eventTriggerNode)
                            {
                                nodes.Add(eventTriggerNode);
                            }

                            break;
                        case "CreateCastStmt":
                            if (TryCreateCastNode(property.Value) is { } castNode)
                            {
                                nodes.Add(castNode);
                            }

                            break;
                        case "AlterEventTrigStmt":
                            if (TryGetDirectStringProperty(property.Value, "trigname") is { Length: > 0 } alteredEventTrigger)
                            {
                                nodes.Add(new AlterEventTriggerNode(alteredEventTrigger, TryGetDirectStringProperty(property.Value, "tgenabled") ?? "O"));
                            }

                            break;
                        case "CreatePublicationStmt":
                            if (TryCreatePublicationNode(property.Value) is { } publicationNode)
                            {
                                nodes.Add(publicationNode);
                            }

                            // Its row filters are expressions of the publication, not statements of the file.
                            continue;
                        case "AlterPublicationStmt":
                            if (TryCreateAlterPublicationNode(property.Value) is { } alterPublicationNode)
                            {
                                nodes.Add(alterPublicationNode);
                            }

                            continue;
                        case "AlterTSConfigurationStmt":
                            if (TryCreateTextSearchMappingNode(property.Value) is { } mappingNode)
                            {
                                nodes.Add(mappingNode);
                            }

                            continue;
                        case "AlterTSDictionaryStmt":
                            if (TryCreateTextSearchDictionaryAlterNode(property.Value) is { } dictionaryAlterNode)
                            {
                                nodes.Add(dictionaryAlterNode);
                            }

                            continue;
                        case "CreateStatsStmt":
                            if (TryCreateStatisticsNode(property.Value) is { } statisticsNode)
                            {
                                nodes.Add(statisticsNode);
                            }

                            // Its expressions are expressions of the statistics, not statements of the file.
                            continue;
                        case "RuleStmt":
                            if (TryCreateRuleNode(property.Value, statementSql) is { } ruleNode)
                            {
                                nodes.Add(ruleNode);
                            }

                            // Its actions are statements of their own that are part of the rule, not statements of the file.
                            continue;

                        case "CreatePolicyStmt":
                            var policyNode = TryCreatePolicyNode(property.Value, statementSql);
                            if (policyNode is not null)
                            {
                                nodes.Add(policyNode);
                            }

                            break;
                        case "IndexStmt":
                            var indexNode = TryCreateIndexNode(property.Value, statementSql, statementOffset);
                            if (indexNode is not null)
                            {
                                nodes.Add(indexNode);
                            }

                            break;
                        case "Constraint":
                            if (TryApplyConstraintAttribute(property.Value, nodes))
                            {
                                break;
                            }

                            // A NOT NULL is the column's nullability, never a constraint of the model (G-126): one written on the column already
                            // is that, so it makes no node; one that names its column (a PostgreSQL 18 table constraint) is folded onto it later.
                            if (string.Equals(TryGetConstraintType(property.Value), "CONSTR_NOTNULL", StringComparison.OrdinalIgnoreCase))
                            {
                                if (TryCreateNotNullConstraintNode(property.Value, currentTableName, currentSchemaName) is { } notNullNode)
                                {
                                    nodes.Add(notNullNode);
                                }

                                break;
                            }

                            var constraintNode = TryCreateConstraintNode(property.Value, currentTableName, currentSchemaName, currentColumnName, statementSql, statementOffset);
                            if (constraintNode is not null)
                            {
                                nodes.Add(constraintNode);
                            }

                            break;
                        case "CreateSeqStmt":
                            var sequenceNode = TryCreateSequenceNode(property.Value);
                            if (sequenceNode is not null)
                            {
                                nodes.Add(sequenceNode);
                            }

                            break;
                        case "CommentStmt":
                            var objectCommentNode = TryCreateObjectCommentNode(property.Value);
                            if (objectCommentNode is not null)
                            {
                                nodes.Add(objectCommentNode);
                            }

                            break;
                        case "GrantStmt":
                            AddIfNotNull(nodes, GrantStatementReader.ReadGrantStatement(property.Value));
                            break;
                        case "GrantRoleStmt":
                            AddIfNotNull(nodes, GrantStatementReader.ReadRoleStatement(property.Value));
                            break;
                        case "AlterDefaultPrivilegesStmt":
                            AddIfNotNull(nodes, GrantStatementReader.ReadDefaultPrivilegesStatement(property.Value));
                            break;
                        case "CompositeTypeStmt":
                            AddIfNotNull(nodes, TypeStatementReader.ReadCompositeType(property.Value, statementSql));
                            // coldeflist entries are ColumnDef nodes; without this, the generic recursive descent
                            // below would visit them too and misread a composite type's fields as table columns.
                            continue;
                        case "CreateEnumStmt":
                            AddIfNotNull(nodes, TypeStatementReader.ReadEnumType(property.Value, statementSql));
                            break;
                        case "CreateRangeStmt":
                            AddIfNotNull(nodes, TypeStatementReader.ReadRangeType(property.Value, statementSql));
                            break;
                        case "DefineStmt":
                            AddIfNotNull(nodes, TypeStatementReader.ReadBaseType(property.Value));
                            if (string.Equals(TryGetDirectStringProperty(property.Value, "kind"), "OBJECT_OPERATOR", StringComparison.Ordinal)
                                && TryCreateOperatorNode(property.Value) is { } operatorNode)
                            {
                                nodes.Add(operatorNode);
                            }
                            if (string.Equals(TryGetDirectStringProperty(property.Value, "kind"), "OBJECT_AGGREGATE", StringComparison.Ordinal)
                                && TryCreateAggregateNode(property.Value) is { } aggregateNode)
                            {
                                nodes.Add(aggregateNode);
                            }
                            if (string.Equals(TryGetDirectStringProperty(property.Value, "kind"), "OBJECT_COLLATION", StringComparison.Ordinal)
                                && TryCreateCollationNode(property.Value) is { } collationNode)
                            {
                                nodes.Add(collationNode);
                            }
                            if (string.Equals(TryGetDirectStringProperty(property.Value, "kind"), "OBJECT_TSDICTIONARY", StringComparison.Ordinal)
                                && TryCreateTextSearchDictionaryNode(property.Value) is { } dictionaryNode)
                            {
                                nodes.Add(dictionaryNode);
                            }
                            if (string.Equals(TryGetDirectStringProperty(property.Value, "kind"), "OBJECT_TSCONFIGURATION", StringComparison.Ordinal)
                                && TryCreateTextSearchConfigurationNode(property.Value) is { } configurationNode)
                            {
                                nodes.Add(configurationNode);
                            }

                            break;
                        case "CreateDomainStmt":
                            AddIfNotNull(nodes, TypeStatementReader.ReadDomain(property.Value, statementSql, statementOffset));
                            // constraints entries are Constraint nodes (the domain's own DEFAULT/NOT NULL/CHECK,
                            // read directly by TypeStatementReader.ReadDomain - G-77); without this the generic
                            // recursive descent below would also visit them and misread one as a table's own
                            // constraint with no owning table.
                            continue;
                        case "AlterDomainStmt":
                            // ADD CONSTRAINT ... CHECK is modeled (G-233); the constraint inside is read here, not as a table's own.
                            AddIfNotNull(nodes, TypeStatementReader.ReadAlterDomainCheck(property.Value, statementSql, statementOffset));
                            continue;
                        case "CreateExtensionStmt":
                            AddIfNotNull(nodes, TypeStatementReader.ReadExtension(property.Value, statementSql));
                            break;
                    }

                    VisitElement(property.Value, nodes, currentTableName, currentSchemaName, currentColumnName, statementSql, statementOffset);
                }

                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    VisitElement(child, nodes, currentTableName, currentSchemaName, currentColumnName, statementSql, statementOffset);
                }

                break;
        }
    }

    private static void AddIfNotNull(ICollection<SqlNode> nodes, SqlNode? node)
    {
        if (node is not null)
        {
            nodes.Add(node);
        }
    }

    /// <summary>The <c>stmts</c> array of pg_query's tree for <paramref name="sql"/>, or null when it does not parse.</summary>
    internal static string? TryParseStatementsJson(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        PgQueryInterop.NativeParseResult result = default;
        var allocated = false;
        try
        {
            result = PgQueryInterop.Parse(sql);
            allocated = true;
            if (PgQueryInterop.ReadError(result.Error) is not null
                || PgQueryInterop.ReadParseTree(sql, result) is not { } json)
            {
                return null;
            }

            using var document = PgQueryJson.Parse(json);
            return TryGetDirectProperty(document.RootElement, "stmts", out var statements) ? statements.GetRawText() : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            if (allocated)
            {
                PgQueryInterop.FreeParseResult(result);
            }
        }
    }
}
