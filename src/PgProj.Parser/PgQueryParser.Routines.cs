using System.Globalization;
using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    private static FunctionNode? TryCreateFunctionNode(JsonElement element, string statementSql)
    {
        var qualifiedName = TryGetQualifiedName(element, "funcname");
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return null;
        }

        SplitSchemaQualifiedName(qualifiedName, out var schemaName, out var functionName);
        var (rawBody, bodyDelimiter) = TryExtractAnyRoutineBody(element, statementSql, out var isSqlStandardBody);
        var language = TryGetFunctionLanguage(element);
        return new FunctionNode(functionName, schemaName, language, isSqlStandardBody ? rawBody : TryGetFunctionBody(element))
        {
            RawBody = rawBody,
            IsSqlStandardBody = isSqlStandardBody,
            BodyDelimiter = bodyDelimiter,
            Signature = ExtractRoutineSignature(element, language),
            BodyAst = TryGetSqlBodyAst(element, language),
            PlpgsqlStatementAsts = TryGetPlpgsqlStatementAsts(language, statementSql, rawBody),
        };
    }

    private static string? TryGetFunctionLanguage(JsonElement element)
    {
        var directLanguage = TryGetStringPropertyRecursive(element, "language");
        if (!string.IsNullOrWhiteSpace(directLanguage))
        {
            return directLanguage;
        }

        if (!TryFindPropertyRecursive(element, "options", out var optionsElement) || optionsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var option in optionsElement.EnumerateArray())
        {
            if (!TryGetDirectProperty(option, "DefElem", out var definitionElement))
            {
                continue;
            }

            var definitionName = TryGetDirectStringProperty(definitionElement, "defname");
            if (!string.Equals(definitionName, "language", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var language = TryRenderDirectSqlProperty(definitionElement, "arg")
                ?? TryRenderDirectSqlProperty(definitionElement, "value")
                ?? TryRenderDirectSqlProperty(definitionElement, "val")
                ?? TryGetStringPropertyRecursive(definitionElement, "sval")
                ?? TryGetStringPropertyRecursive(definitionElement, "str");

            if (!string.IsNullOrWhiteSpace(language))
            {
                return language.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// The tree of a SQL-language routine body (G-90): a <c>BEGIN ATOMIC</c> body is already in the statement's tree; a <c>LANGUAGE sql</c>
    /// string body is parsed on its own. PostgreSQL validates either when the routine is created, so what it reads is what it depends on.
    /// </summary>
    private static string? TryGetSqlBodyAst(JsonElement element, string? language)
    {
        if (TryGetDirectProperty(element, "sql_body", out var sqlBody))
        {
            return sqlBody.GetRawText();
        }

        return string.Equals(language, "sql", StringComparison.OrdinalIgnoreCase) && TryGetFunctionBody(element) is { } body
            ? TryParseStatementsJson(body)
            : null;
    }

    /// <summary>The SQL a PL/pgSQL body runs (G-303a); empty for another language.</summary>
    private static IReadOnlyList<string> TryGetPlpgsqlStatementAsts(string? language, string statementSql, string? rawBody)
        => string.Equals(language, "plpgsql", StringComparison.OrdinalIgnoreCase) ? PlpgsqlBodyReader.Read(statementSql, rawBody) ?? [] : [];

    private static string? TryGetFunctionBody(JsonElement element)
    {
        if (TryFindPropertyRecursive(element, "sql_body", out var sqlBodyElement))
        {
            var sqlBody = TryRenderSqlFragment(sqlBodyElement);
            if (!string.IsNullOrWhiteSpace(sqlBody))
            {
                return sqlBody.Trim();
            }
        }

        if (!TryFindPropertyRecursive(element, "options", out var optionsElement) || optionsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var option in optionsElement.EnumerateArray())
        {
            if (!TryGetDirectProperty(option, "DefElem", out var definitionElement))
            {
                continue;
            }

            var definitionName = TryGetDirectStringProperty(definitionElement, "defname");
            if (!string.Equals(definitionName, "as", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetDirectProperty(definitionElement, "arg", out var argElement))
            {
                continue;
            }

            var body = TryGetFunctionBodyFromOptionArgument(argElement);
            if (!string.IsNullOrWhiteSpace(body))
            {
                return body.Trim();
            }
        }

        return null;
    }

    private static string? TryGetFunctionBodyFromOptionArgument(JsonElement argElement)
    {
        switch (argElement.ValueKind)
        {
            case JsonValueKind.String:
                return argElement.GetString();
            case JsonValueKind.Array:
                foreach (var item in argElement.EnumerateArray())
                {
                    var body = TryRenderSqlFragment(item);
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        return body;
                    }
                }

                return null;
            default:
                return TryRenderSqlFragment(argElement);
        }
    }

    /// <summary>
    /// Function/procedure parameters (<c>FunctionParameter</c> nodes) store their type under
    /// <c>argType</c> rather than <c>typeName</c>, which is only used by column definitions and
    /// casts. Without this, every parameter's data type silently resolved to <c>null</c> and
    /// <see cref="RoutineSignature.RenderArgumentTypeSignature"/> fell back to a placeholder,
    /// causing distinct overloads to collide onto the same routine key (G-29).
    /// </summary>
    private static string? TryGetArgTypeName(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "argType", out var argTypeElement))
        {
            return null;
        }

        var name = TryGetQualifiedNameFromDirectProperty(argTypeElement, "names")
            ?? TryGetDirectStringProperty(argTypeElement, "sval")
            ?? TryGetStringPropertyRecursive(argTypeElement, "sval");

        return AppendArraySuffix(name, argTypeElement);
    }

    /// <summary>
    /// Adds <c>[]</c> to a routine type name when the declared type is an array. PostgreSQL identifies an overload by
    /// whether a parameter is an array, not by the number of dimensions, so a single suffix is used (G-49). Type
    /// modifiers are ignored by PostgreSQL for routine parameters and results and are not captured.
    /// </summary>
    private static string? AppendArraySuffix(string? typeName, JsonElement typeNameElement)
        => typeName is not null && ColumnTypeDetailsReader.ReadTypeName(typeNameElement).ArrayDimensions > 0
            ? typeName + "[]"
            : typeName;

    /// <summary>
    /// The routine's body as written. A SQL-standard body (<c>BEGIN ATOMIC ... END</c>, or <c>RETURN expression</c>; pg_query's
    /// <c>sql_body</c>) is taken from its first word to the end of the statement, and has no delimiter (G-95); any other is the
    /// string after <c>AS</c>.
    /// </summary>
    private static (string? Body, string? Delimiter) TryExtractAnyRoutineBody(JsonElement element, string statementSql, out bool isSqlStandardBody)
    {
        isSqlStandardBody = TryGetDirectProperty(element, "sql_body", out _);
        return isSqlStandardBody ? (TryExtractSqlStandardBody(statementSql), null) : TryExtractRoutineBody(statementSql);
    }

    /// <summary>From the top-level <c>BEGIN ATOMIC</c>, or else <c>RETURN</c>, to the end of the statement, without its semicolon.</summary>
    private static string? TryExtractSqlStandardBody(string statementSql)
    {
        var tokens = GetTopLevelWordTokens(statementSql);
        var start = -1;
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (string.Equals(tokens[index].Text, "BEGIN", StringComparison.OrdinalIgnoreCase) && string.Equals(tokens[index + 1].Text, "ATOMIC", StringComparison.OrdinalIgnoreCase))
            {
                start = tokens[index].Start;
                break;
            }
        }

        if (start < 0 && tokens.FindIndex(static token => string.Equals(token.Text, "RETURN", StringComparison.OrdinalIgnoreCase)) is var returnIndex && returnIndex >= 0)
        {
            start = tokens[returnIndex].Start;
        }

        return start < 0 ? null : statementSql[start..].TrimEnd().TrimEnd(';').TrimEnd();
    }

    /// <summary>
    /// Slices the routine body verbatim out of the original statement text so the user's
    /// formatting, comments, and casing survive into deployment untouched.
    /// </summary>
    private static (string? Body, string? Delimiter) TryExtractRoutineBody(string statementSql)
    {
        var tokens = GetTopLevelWordTokens(statementSql);
        var asTokenIndex = tokens.FindIndex(static token => string.Equals(token.Text, "AS", StringComparison.OrdinalIgnoreCase));
        if (asTokenIndex < 0)
        {
            return (null, null);
        }

        var index = tokens[asTokenIndex].End;
        while (index < statementSql.Length && char.IsWhiteSpace(statementSql[index]))
        {
            index++;
        }

        if (index >= statementSql.Length)
        {
            return (null, null);
        }

        if (statementSql[index] == '$')
        {
            var delimiterEnd = index + 1;
            while (delimiterEnd < statementSql.Length
                && (char.IsLetterOrDigit(statementSql[delimiterEnd]) || statementSql[delimiterEnd] == '_'))
            {
                delimiterEnd++;
            }

            if (delimiterEnd < statementSql.Length && statementSql[delimiterEnd] == '$')
            {
                var delimiter = statementSql[index..(delimiterEnd + 1)];
                var bodyStart = delimiterEnd + 1;
                var closingIndex = statementSql.IndexOf(delimiter, bodyStart, StringComparison.Ordinal);
                if (closingIndex >= 0)
                {
                    return (statementSql[bodyStart..closingIndex], delimiter);
                }
            }
        }

        if (statementSql[index] == '\'')
        {
            var bodyStart = index + 1;
            var scanIndex = index;
            if (TrySkipQuotedOrComment(statementSql, ref scanIndex))
            {
                var literal = statementSql[bodyStart..(scanIndex - 1)];
                return (literal.Replace("''", "'", StringComparison.Ordinal), null);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Captures the routine signature so the CREATE header can be regenerated faithfully
    /// while the body is replayed verbatim.
    /// </summary>
    private static RoutineSignature ExtractRoutineSignature(JsonElement element, string? language)
    {
        var parameters = new List<RoutineParameter>();

        if (TryGetDirectProperty(element, "parameters", out var parametersElement)
            && parametersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var parameter in parametersElement.EnumerateArray())
            {
                if (!TryGetDirectProperty(parameter, "FunctionParameter", out var functionParameter))
                {
                    continue;
                }

                var mode = TryGetDirectStringProperty(functionParameter, "mode") switch
                {
                    "FUNC_PARAM_OUT" => "OUT",
                    "FUNC_PARAM_INOUT" => "INOUT",
                    "FUNC_PARAM_VARIADIC" => "VARIADIC",
                    // A RETURNS TABLE column (G-198): part of the result, kept so it is deployed and compared.
                    "FUNC_PARAM_TABLE" => "TABLE",
                    _ => "IN",
                };

                parameters.Add(new RoutineParameter(
                    TryGetDirectStringProperty(functionParameter, "name"),
                    TryGetArgTypeName(functionParameter),
                    mode,
                    TryGetDirectProperty(functionParameter, "defexpr", out var defaultExpression)
                        ? ReadExpressionText(defaultExpression)
                        : null)
                {
                    WrittenDataType = TryGetDirectProperty(functionParameter, "argType", out var argumentType) ? ReadTypeText(argumentType) : null,
                });
            }
        }

        var returnType = TryGetDirectProperty(element, "returnType", out var returnTypeElement)
            ? AppendArraySuffix(
                TryGetQualifiedNameFromDirectProperty(returnTypeElement, "names")
                    ?? TryGetStringPropertyRecursive(returnTypeElement, "sval"),
                returnTypeElement)
            : null;

        var returnsSet = TryGetDirectProperty(element, "returnType", out var setElement)
            && TryGetDirectBooleanProperty(setElement, "setof", out var setof)
            && setof;

        return new RoutineSignature
        {
            Parameters = parameters,
            ReturnType = returnType,
            // RETURNS TABLE's record is the parser's, not written (G-198): its columns are the TABLE parameters.
            WrittenReturnType = returnType is not null && !parameters.Any(static parameter => parameter.IsTableColumn)
                && TryGetDirectProperty(element, "returnType", out var writtenReturnType) ? ReadTypeText(writtenReturnType) : null,
            ReturnsSet = returnsSet,
            Language = language,
            Volatility = TryGetRoutineOption(element, "volatility"),
            IsStrict = string.Equals(TryGetRoutineOption(element, "strict"), "true", StringComparison.OrdinalIgnoreCase),
            IsSecurityDefiner = string.Equals(TryGetRoutineOption(element, "security"), "true", StringComparison.OrdinalIgnoreCase),
            Parallel = TryGetRoutineOption(element, "parallel")?.ToLowerInvariant(),
            IsLeakproof = string.Equals(TryGetRoutineOption(element, "leakproof"), "true", StringComparison.OrdinalIgnoreCase),
            Cost = TryGetRoutineNumberOption(element, "cost"),
            Rows = TryGetRoutineNumberOption(element, "rows"),
            IsWindow = string.Equals(TryGetRoutineOption(element, "window"), "true", StringComparison.OrdinalIgnoreCase),
            Settings = ReadRoutineSettings(element),
            LinkSymbol = ReadLinkSymbol(element),
            SupportFunction = ReadSupportFunction(element, out var supportClause),
            WrittenSupportClause = supportClause,
            TransformTypes = ReadTransformTypes(element, out var transformClause),
            WrittenTransformClause = transformClause,
        };
    }

    /// <summary>The second string of <c>AS 'library', 'symbol'</c>, a C function's link symbol (G-211), or null.</summary>
    private static string? ReadLinkSymbol(JsonElement element)
    {
        foreach (var definition in EnumerateRoutineOptions(element))
        {
            if (string.Equals(TryGetDirectStringProperty(definition, "defname"), "as", StringComparison.OrdinalIgnoreCase)
                && TryGetDirectProperty(definition, "arg", out var argument)
                && TryGetDirectProperty(argument, "List", out var list)
                && TryGetStringListFromDirectProperty(list, "items") is { Count: 2 } strings)
            {
                return strings[1];
            }
        }

        return null;
    }

    /// <summary>
    /// <c>SUPPORT name</c> (G-205): the name's parts joined with dots, and the clause as written, from <c>SUPPORT</c> to the name's last token
    /// (PostgreSQL's lexer gives the tokens; the name is identifiers joined by dots).
    /// </summary>
    private static string? ReadSupportFunction(JsonElement element, out string? writtenClause)
    {
        writtenClause = null;
        foreach (var definition in EnumerateRoutineOptions(element))
        {
            if (!string.Equals(TryGetDirectStringProperty(definition, "defname"), "support", StringComparison.OrdinalIgnoreCase)
                || !TryGetDirectProperty(definition, "arg", out var argument))
            {
                continue;
            }

            var parts = TryGetDirectProperty(argument, "List", out var list) ? TryGetStringListFromDirectProperty(list, "items") : [];
            if (parts.Count == 0)
            {
                return null;
            }

            if (s_context is { } context && TryGetDirectInt32Property(definition, "location", out var location)
                && location - context.StatementOffset is var start && start >= 0 && start < context.StatementSql.Length
                && SourceText.Scan(context.StatementSql[start..]) is { Count: > 1 } tokens)
            {
                var text = context.StatementSql[start..];
                var end = 1;
                while (end + 2 < tokens.Count && text[tokens[end + 1].Start..tokens[end + 1].End] == ".")
                {
                    end += 2;
                }

                writtenClause = text[..tokens[end].End];
            }

            return string.Join('.', parts);
        }

        return null;
    }

    /// <summary>
    /// <c>TRANSFORM FOR TYPE t [, ...]</c> (G-205): the types as the parser names them, and the clause as written, from <c>TRANSFORM</c> to the end
    /// of the last type as written.
    /// </summary>
    private static IReadOnlyList<string> ReadTransformTypes(JsonElement element, out string? writtenClause)
    {
        writtenClause = null;
        foreach (var definition in EnumerateRoutineOptions(element))
        {
            if (!string.Equals(TryGetDirectStringProperty(definition, "defname"), "transform", StringComparison.OrdinalIgnoreCase)
                || !TryGetDirectProperty(definition, "arg", out var argument)
                || !TryGetDirectProperty(argument, "List", out var list)
                || !TryGetDirectProperty(list, "items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var types = new List<string>();
            JsonElement? last = null;
            foreach (var item in items.EnumerateArray())
            {
                if (TryGetDirectProperty(item, "TypeName", out var typeName))
                {
                    types.Add(RenderCastTypeName(typeName));
                    last = typeName;
                }
            }

            if (last is { } lastType && s_context is { } context
                && TryGetDirectInt32Property(definition, "location", out var location)
                && TryGetDirectInt32Property(lastType, "location", out var typeLocation)
                && ReadTypeText(lastType) is { } lastText)
            {
                var start = location - context.StatementOffset;
                var end = typeLocation - context.StatementOffset + lastText.Length;
                if (start >= 0 && end <= context.StatementSql.Length && start < end)
                {
                    writtenClause = context.StatementSql[start..end];
                }
            }

            return types;
        }

        return [];
    }

    /// <summary>The routine's <c>DefElem</c> options (<c>LANGUAGE</c>, <c>COST</c>, <c>SET</c>, ...), in the order written.</summary>
    private static IEnumerable<JsonElement> EnumerateRoutineOptions(JsonElement element)
    {
        if (!TryFindPropertyRecursive(element, "options", out var optionsElement)
            || optionsElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var option in optionsElement.EnumerateArray())
        {
            if (TryGetDirectProperty(option, "DefElem", out var definitionElement))
            {
                yield return definitionElement;
            }
        }
    }

    /// <summary><c>COST</c> or <c>ROWS</c> as written (G-197): an <c>Integer</c> (pg_query omits a zero) or a <c>Float</c>.</summary>
    private static string? TryGetRoutineNumberOption(JsonElement element, string optionName)
    {
        foreach (var definition in EnumerateRoutineOptions(element))
        {
            if (!string.Equals(TryGetDirectStringProperty(definition, "defname"), optionName, StringComparison.OrdinalIgnoreCase)
                || !TryGetDirectProperty(definition, "arg", out var argument))
            {
                continue;
            }

            if (TryGetDirectProperty(argument, "Integer", out var integerNode))
            {
                return TryGetDirectProperty(integerNode, "ival", out var integerValue) && integerValue.ValueKind == JsonValueKind.Number
                    ? integerValue.ToString()
                    : "0";
            }

            if (TryGetDirectProperty(argument, "Float", out var floatNode))
            {
                return TryGetDirectStringProperty(floatNode, "fval");
            }
        }

        return null;
    }

    /// <summary>
    /// The routine's <c>SET</c> clauses (G-197). Each value is kept as text without quotes, whether it was written as a name
    /// (<c>pg_catalog</c>), a string (<c>'64MB'</c>), or a number; <c>SET name TO DEFAULT</c> and <c>RESET</c> set nothing, so they are left out.
    /// </summary>
    /// <summary>
    /// A <c>SET</c> clause's own text (G-207): from the <c>SET</c> keyword to the end of its last value (or of <c>FROM CURRENT</c>), with
    /// the token boundaries from PostgreSQL's lexer. Null when the statement's text is not at hand or the clause cannot be found.
    /// </summary>
    private static string? ReadSettingText(JsonElement definition, JsonElement setStatement, bool fromCurrent)
    {
        if (s_context is not { } context
            || !TryGetDirectInt32Property(definition, "location", out var location))
        {
            return null;
        }

        var start = location - context.StatementOffset;
        if (start < 0 || start >= context.StatementSql.Length)
        {
            return null;
        }

        // The tokens come from the statement's one run of the lexer, and only as far as the clause ends: a routine with many SET clauses would
        // otherwise lex the rest of the statement for each (G-310).
        var sql = context.StatementSql;
        if (!sql.AsSpan(start).StartsWith("set", StringComparison.OrdinalIgnoreCase) || SourceText.TokensFromOrNull(sql, start) is not { } tokens)
        {
            return null;
        }

        if (fromCurrent)
        {
            var afterSet = false;
            foreach (var (tokenStart, tokenEnd) in tokens)
            {
                if (!afterSet)
                {
                    afterSet = true;
                    continue;
                }

                if (sql.AsSpan(tokenStart, tokenEnd - tokenStart).Equals("current", StringComparison.OrdinalIgnoreCase))
                {
                    return sql[start..tokenEnd];
                }
            }

            return null;
        }

        var last = -1;
        if (TryGetDirectProperty(setStatement, "args", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
        {
            foreach (var argument in arguments.EnumerateArray())
            {
                if (TryGetDirectProperty(argument, "A_Const", out var constant) && TryGetDirectInt32Property(constant, "location", out var argumentLocation))
                {
                    last = Math.Max(last, argumentLocation - location);
                }
            }
        }

        using var enumerator = tokens.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var (tokenStart, tokenEnd) = enumerator.Current;
            if (tokenStart - start != last)
            {
                continue;
            }

            // A signed number's position is its sign: the value runs to the number after it.
            var end = tokenEnd;
            if (tokenEnd - tokenStart == 1 && sql[tokenStart] is '-' or '+' && enumerator.MoveNext())
            {
                end = enumerator.Current.End;
            }

            return sql[start..end];
        }

        return null;
    }

    private static IReadOnlyList<RoutineSetting> ReadRoutineSettings(JsonElement element)
    {
        var settings = new List<RoutineSetting>();
        foreach (var definition in EnumerateRoutineOptions(element))
        {
            if (!string.Equals(TryGetDirectStringProperty(definition, "defname"), "set", StringComparison.OrdinalIgnoreCase)
                || !TryGetDirectProperty(definition, "arg", out var argument)
                || !TryGetDirectProperty(argument, "VariableSetStmt", out var setStatement))
            {
                continue;
            }

            var name = TryGetDirectStringProperty(setStatement, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            switch (TryGetDirectStringProperty(setStatement, "kind"))
            {
                case "VAR_SET_CURRENT":
                    settings.Add(new RoutineSetting(name.ToLowerInvariant(), [], IsFromCurrent: true) { WrittenText = ReadSettingText(definition, setStatement, fromCurrent: true) });
                    break;
                case "VAR_SET_VALUE":
                    var values = new List<string>();
                    if (TryGetDirectProperty(setStatement, "args", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var value in arguments.EnumerateArray())
                        {
                            values.Add(ReadSettingValue(value));
                        }
                    }

                    settings.Add(new RoutineSetting(name.ToLowerInvariant(), values, IsFromCurrent: false) { WrittenText = ReadSettingText(definition, setStatement, fromCurrent: false) });
                    break;
            }
        }

        return settings;
    }

    private static string ReadSettingValue(JsonElement value)
    {
        if (!TryGetDirectProperty(value, "A_Const", out var constant))
        {
            return TryRenderSqlFragment(value) ?? string.Empty;
        }

        if (TryGetDirectProperty(constant, "sval", out var stringNode))
        {
            return TryGetDirectStringProperty(stringNode, "sval") ?? string.Empty;
        }

        if (TryGetDirectProperty(constant, "ival", out var integerNode))
        {
            return TryGetDirectProperty(integerNode, "ival", out var integerValue) && integerValue.ValueKind == JsonValueKind.Number
                ? integerValue.ToString()
                : "0";
        }

        if (TryGetDirectProperty(constant, "fval", out var floatNode))
        {
            return TryGetDirectStringProperty(floatNode, "fval") ?? string.Empty;
        }

        if (TryGetDirectProperty(constant, "boolval", out var booleanNode))
        {
            return TryGetDirectBooleanProperty(booleanNode, "boolval", out var booleanValue) && booleanValue ? "true" : "false";
        }

        return string.Empty;
    }

    private static string? TryGetRoutineOption(JsonElement element, string optionName)
    {
        if (!TryFindPropertyRecursive(element, "options", out var optionsElement)
            || optionsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var option in optionsElement.EnumerateArray())
        {
            if (!TryGetDirectProperty(option, "DefElem", out var definitionElement))
            {
                continue;
            }

            if (!string.Equals(TryGetDirectStringProperty(definitionElement, "defname"), optionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetDirectProperty(definitionElement, "arg", out var argElement))
            {
                if (TryGetDirectProperty(argElement, "Boolean", out var booleanElement))
                {
                    return TryGetDirectBooleanProperty(booleanElement, "boolval", out var boolValue) && boolValue
                        ? "true"
                        : "false";
                }

                return TryGetStringPropertyRecursive(argElement, "sval") ?? TryRenderSqlFragment(argElement);
            }
        }

        return null;
    }

    private static ProcedureNode? TryCreateProcedureNode(JsonElement element, string statementSql)
    {
        var qualifiedName = TryGetQualifiedName(element, "funcname");
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return null;
        }

        SplitSchemaQualifiedName(qualifiedName, out var schemaName, out var procedureName);
        var (rawBody, bodyDelimiter) = TryExtractAnyRoutineBody(element, statementSql, out var isSqlStandardBody);
        var language = TryGetFunctionLanguage(element);
        return new ProcedureNode(procedureName, schemaName, language, isSqlStandardBody ? rawBody : TryGetFunctionBody(element))
        {
            RawBody = rawBody,
            IsSqlStandardBody = isSqlStandardBody,
            BodyDelimiter = bodyDelimiter,
            Signature = ExtractRoutineSignature(element, language),
            BodyAst = TryGetSqlBodyAst(element, language),
            PlpgsqlStatementAsts = TryGetPlpgsqlStatementAsts(language, statementSql, rawBody),
        };
    }

    private static TriggerNode? TryCreateTriggerNode(JsonElement element, string statementSql)
    {
        var triggerName = TryGetDirectStringProperty(element, "trigname");
        if (string.IsNullOrWhiteSpace(triggerName))
        {
            return null;
        }

        TryGetRelationIdentity(element, "relation", out var tableName, out var schemaName);

        var isRowLevel = TryGetDirectBooleanProperty(element, "row", out var row) && row;
        var timing = TryGetTriggerTiming(element);
        var events = GetTriggerEvents(element);

        var whenCondition = TryGetDirectProperty(element, "whenClause", out var whenClause) ? whenClause : (JsonElement?)null;

        return new TriggerNode(triggerName, schemaName, tableName)
        {
            FunctionName = TryGetQualifiedName(element, "funcname"),
            Timing = timing,
            Events = events,
            IsRowLevel = isRowLevel,
            RawStatement = string.IsNullOrWhiteSpace(statementSql) ? null : statementSql.Trim(),
            IsConstraintTrigger = TryGetDirectBooleanProperty(element, "isconstraint", out var isConstraint) && isConstraint,
            IsDeferrable = TryGetDirectBooleanProperty(element, "deferrable", out var deferrable) && deferrable,
            IsInitiallyDeferred = TryGetDirectBooleanProperty(element, "initdeferred", out var initiallyDeferred) && initiallyDeferred,
            ConstraintTableName = TryGetRelationIdentity(element, "constrrel", out var constraintTable, out var constraintSchema)
                ? string.IsNullOrWhiteSpace(constraintSchema) ? constraintTable : $"{constraintSchema}.{constraintTable}"
                : null,
            UpdateColumns = GetTriggerStringList(element, "columns"),
            FunctionArguments = GetTriggerStringList(element, "args"),
            OldTransitionTable = GetTriggerTransitionTable(element, isNew: false),
            NewTransitionTable = GetTriggerTransitionTable(element, isNew: true),
            RawWhenCondition = whenCondition is null ? null : TriggerWhenConditionReader.TryRead(statementSql),
            WhenConditionAst = whenCondition?.GetRawText(),
        };
    }

    private static IReadOnlyList<string> GetTriggerStringList(JsonElement element, string propertyName)
    {
        if (!TryGetDirectProperty(element, propertyName, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (TryGetDirectProperty(item, "String", out var text) && TryGetDirectStringProperty(text, "sval") is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    // REFERENCING OLD TABLE AS x NEW TABLE AS y: libpg_query omits false values, so a transition without isNew is the OLD one.
    private static string? GetTriggerTransitionTable(JsonElement element, bool isNew)
    {
        if (!TryGetDirectProperty(element, "transitionRels", out var transitions) || transitions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var wrapper in transitions.EnumerateArray())
        {
            if (!TryGetDirectProperty(wrapper, "TriggerTransition", out var transition))
            {
                continue;
            }

            _ = TryGetDirectBooleanProperty(transition, "isNew", out var transitionIsNew);
            if (transitionIsNew == isNew)
            {
                return TryGetDirectStringProperty(transition, "name");
            }
        }

        return null;
    }

    // libpg_query encodes trigger timing and events as the TRIGGER_TYPE_* bitmask in "timing" and "events".
    private const int TriggerTypeBefore = 1 << 1;

    private const int TriggerTypeInsert = 1 << 2;

    private const int TriggerTypeDelete = 1 << 3;

    private const int TriggerTypeUpdate = 1 << 4;

    private const int TriggerTypeTruncate = 1 << 5;

    private const int TriggerTypeInsteadOf = 1 << 6;

    private static string? TryGetTriggerTiming(JsonElement element)
    {
        // libpg_query omits zero-valued fields, so an absent "timing" means AFTER.
        if (!TryGetDirectInt32Property(element, "timing", out var timing))
        {
            return "AFTER";
        }

        if ((timing & TriggerTypeBefore) != 0)
        {
            return "BEFORE";
        }

        return (timing & TriggerTypeInsteadOf) != 0 ? "INSTEAD OF" : "AFTER";
    }

    private static IReadOnlyList<string> GetTriggerEvents(JsonElement element)
    {
        if (!TryGetDirectInt32Property(element, "events", out var events))
        {
            return [];
        }

        var triggerEvents = new List<string>();
        if ((events & TriggerTypeInsert) != 0)
        {
            triggerEvents.Add("INSERT");
        }

        if ((events & TriggerTypeUpdate) != 0)
        {
            triggerEvents.Add("UPDATE");
        }

        if ((events & TriggerTypeDelete) != 0)
        {
            triggerEvents.Add("DELETE");
        }

        if ((events & TriggerTypeTruncate) != 0)
        {
            triggerEvents.Add("TRUNCATE");
        }

        return triggerEvents;
    }
}
