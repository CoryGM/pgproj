using System.Globalization;
using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    /// <summary>
    /// A view's <c>WITH (...)</c> options and its <c>WITH [LOCAL | CASCADED] CHECK OPTION</c>, in the canonical form of <see cref="ViewOptions"/> (G-180).
    /// </summary>
    private static IReadOnlyList<string> ExtractViewOptions(JsonElement viewStatement)
    {
        var options = new List<(string Name, string? Value)>();
        if (TryGetDirectProperty(viewStatement, "options", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (!TryGetDirectProperty(entry, "DefElem", out var defElem) || TryGetDirectStringProperty(defElem, "defname") is not { } name)
                {
                    continue;
                }

                options.Add((name, TryGetDirectProperty(defElem, "arg", out var arg) ? ReadDefElemValue(arg) : null));
            }
        }

        switch (TryGetDirectStringProperty(viewStatement, "withCheckOption"))
        {
            case "LOCAL_CHECK_OPTION":
                options.Add((ViewOptions.CheckOption, "local"));
                break;
            case "CASCADED_CHECK_OPTION":
                options.Add((ViewOptions.CheckOption, "cascaded"));
                break;
        }

        return ViewOptions.Canonicalize(options);
    }

    /// <summary>The text of an option's value: a string, a number, or a bare word (<c>true</c> and <c>on</c> parse as one).</summary>
    private static string? ReadDefElemValue(JsonElement arg)
    {
        if (TryGetDirectProperty(arg, "String", out var text))
        {
            return TryGetDirectStringProperty(text, "sval");
        }

        if (TryGetDirectProperty(arg, "Integer", out var number))
        {
            return number.TryGetProperty("ival", out var value) && value.TryGetInt32(out var parsed) ? parsed.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
        }

        if (TryGetDirectProperty(arg, "TypeName", out var typeName) && TryGetDirectProperty(typeName, "names", out var names) && names.ValueKind == JsonValueKind.Array)
        {
            return string.Join('.', names.EnumerateArray().Select(static part => part.TryGetProperty("String", out var s) ? TryGetDirectStringProperty(s, "sval") : null).OfType<string>());
        }

        return null;
    }

    /// <summary>
    /// <c>CREATE RECURSIVE VIEW v (a, b) AS query</c> is shorthand PostgreSQL's grammar expands to <c>CREATE VIEW v (a, b) AS WITH RECURSIVE v(a, b) AS (query)
    /// SELECT a, b FROM v</c>, and a view stored that way is all the live database has (G-181). The body kept for a recursive view is that expansion, so it
    /// deploys as a plain view that works, compares with what <c>pg_get_viewdef</c> returns, and is rendered by the server like any other body.
    /// </summary>
    private static string? ExpandRecursiveViewBody(string? body, JsonElement viewStatement, string statementSql, string viewName)
    {
        if (body is null)
        {
            return null;
        }

        var isRecursive = IsRecursiveViewStatement(statementSql);
        var columns = TryGetStringListFromDirectProperty(viewStatement, "aliases");
        if (!isRecursive || columns.Count == 0)
        {
            return body;
        }

        var name = QuoteIdentifierIfNeeded(viewName);
        var columnList = string.Join(", ", columns.Select(QuoteIdentifierIfNeeded));
        return $"WITH RECURSIVE {name} ({columnList}) AS ({body}) SELECT {columnList} FROM {name}";
    }

    private static bool IsRecursiveViewStatement(string statementSql)
    {
        var tokens = GetTopLevelWordTokens(statementSql);
        var asTokenIndex = tokens.FindIndex(static token => string.Equals(token.Text, "AS", StringComparison.OrdinalIgnoreCase));
        return tokens.Take(asTokenIndex < 0 ? 0 : asTokenIndex).Any(static token => string.Equals(token.Text, "RECURSIVE", StringComparison.OrdinalIgnoreCase));
    }

    private static ViewNode? TryCreateViewNode(JsonElement element, string statementSql)
    {
        return TryGetRelationIdentity(element, "view", out var viewName, out var schemaName)
            ? new ViewNode(viewName, schemaName)
            {
                IsReplace = TryGetDirectBooleanProperty(element, "replace", out var replace) && replace,
                RawBodyExpression = ExpandRecursiveViewBody(TryExtractViewBody(statementSql, isMaterialized: false), element, statementSql, viewName),
                Options = ExtractViewOptions(element),
                WrittenOptions = ExtractStorageOptions(element, statementSql, s_context is { } context && string.Equals(context.StatementSql, statementSql, StringComparison.Ordinal) ? context.StatementOffset : 0),
                ColumnAliases = IsRecursiveViewStatement(statementSql) ? [] : TryGetStringListFromDirectProperty(element, "aliases"),
                QueryAst = TryGetDirectProperty(element, "query", out var query) ? query.GetRawText() : null,
            }
            : null;
    }

    private static ViewNode? TryCreateMaterializedViewNode(JsonElement element, string statementSql)
    {
        var objectType = TryGetDirectStringProperty(element, "objtype");
        if (!string.Equals(objectType, "OBJECT_MATVIEW", StringComparison.Ordinal))
        {
            return null;
        }

        return TryGetRelationIdentity(element, "into", out var viewName, out var schemaName)
            ? new ViewNode(viewName, schemaName, IsMaterialized: true)
            {
                RawBodyExpression = TryExtractViewBody(statementSql, isMaterialized: true),
                ColumnAliases = TryGetDirectProperty(element, "into", out var into) ? TryGetStringListFromDirectProperty(into, "colNames") : [],
                QueryAst = TryGetDirectProperty(element, "query", out var query) ? query.GetRawText() : null,
                StorageOptions = TryGetDirectProperty(element, "into", out var intoClause)
                    ? ExtractStorageOptions(intoClause, statementSql, s_context is { } context && string.Equals(context.StatementSql, statementSql, StringComparison.Ordinal) ? context.StatementOffset : 0)
                    : [],
                WithNoData = TryGetDirectProperty(element, "into", out var intoData) && TryGetDirectBooleanProperty(intoData, "skipData", out var skipData) && skipData,
                TablespaceName = TryGetDirectProperty(element, "into", out var intoTablespace) ? TryGetDirectStringProperty(intoTablespace, "tableSpaceName") : null,
                AccessMethod = TryGetDirectProperty(element, "into", out var intoMethod) ? TryGetDirectStringProperty(intoMethod, "accessMethod") : null,
            }
            : null;
    }

    /// <summary>
    /// A <c>CREATE POLICY</c> statement (G-76). Its table identity lives under the key <c>"table"</c>, unlike every other
    /// statement in this parser (<c>CreateStmt</c>/<c>AlterTableStmt</c> use <c>"relation"</c>), confirmed by dumping the real
    /// AST. <c>cmd_name</c> and <c>roles</c> are always present (pg_query itself fills in the <c>all</c>/<c>PUBLIC</c> defaults
    /// when <c>FOR</c>/<c>TO</c> are omitted); <c>permissive</c> is present as <c>true</c> for an explicit or omitted
    /// <c>PERMISSIVE</c>, and is absent entirely (not <c>false</c>) for <c>RESTRICTIVE</c>.
    /// </summary>
    /// <summary>
    /// A <c>CREATE RULE</c> statement (G-128), a <c>RuleStmt</c>: the relation it is on (<c>relation</c>), <c>rulename</c>, <c>event</c>
    /// (<c>CMD_SELECT</c>/<c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c>), <c>instead</c> (absent, not false, for <c>ALSO</c>), the condition (<c>whereClause</c>), and
    /// the <c>actions</c> (none for <c>NOTHING</c>).
    /// </summary>
    private static RuleNode? TryCreateRuleNode(JsonElement element, string statementSql)
    {
        if (!TryGetRelationIdentity(element, "relation", out var tableName, out var schemaName)
            || TryGetDirectStringProperty(element, "rulename") is not { Length: > 0 } ruleName)
        {
            return null;
        }

        var ruleEvent = TryGetDirectStringProperty(element, "event") switch
        {
            "CMD_INSERT" => "insert",
            "CMD_UPDATE" => "update",
            "CMD_DELETE" => "delete",
            _ => "select",
        };

        return new RuleNode(ruleName, schemaName, tableName)
        {
            Event = ruleEvent,
            IsInstead = TryGetDirectBooleanProperty(element, "instead", out var instead) && instead,
            RawDefinition = statementSql.Trim().TrimEnd(';').TrimEnd(),
            QueryAst = element.GetRawText(),
        };
    }

    private static string? TryExtractViewBody(string statementSql, bool isMaterialized)
    {
        var tokens = GetTopLevelWordTokens(statementSql);
        var asTokenIndex = tokens.FindIndex(static token => string.Equals(token.Text, "AS", StringComparison.OrdinalIgnoreCase));
        if (asTokenIndex < 0)
        {
            return null;
        }

        var bodyStart = tokens[asTokenIndex].End;
        var bodyEnd = statementSql.Length;

        if (isMaterialized && tokens.Count >= asTokenIndex + 3)
        {
            var lastTokenIndex = tokens.Count - 1;
            if (string.Equals(tokens[lastTokenIndex].Text, "DATA", StringComparison.OrdinalIgnoreCase))
            {
                var withTokenIndex = string.Equals(tokens[lastTokenIndex - 1].Text, "WITH", StringComparison.OrdinalIgnoreCase)
                    ? lastTokenIndex - 1
                    : lastTokenIndex >= 2
                        && string.Equals(tokens[lastTokenIndex - 1].Text, "NO", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(tokens[lastTokenIndex - 2].Text, "WITH", StringComparison.OrdinalIgnoreCase)
                            ? lastTokenIndex - 2
                            : -1;

                if (withTokenIndex > asTokenIndex
                    && ContainsOnlyTrivia(statementSql.AsSpan(tokens[lastTokenIndex].End)))
                {
                    bodyEnd = tokens[withTokenIndex].Start;
                }
            }
        }

        var body = statementSql[bodyStart..bodyEnd].Trim();
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    private static List<(string Text, int Start, int End)> GetTopLevelWordTokens(string sql)
    {
        var tokens = new List<(string Text, int Start, int End)>();
        var depth = 0;

        for (var index = 0; index < sql.Length;)
        {
            if (TrySkipQuotedOrComment(sql, ref index))
            {
                continue;
            }

            var current = sql[index];
            if (current == '(')
            {
                depth++;
                index++;
                continue;
            }

            if (current == ')')
            {
                depth = Math.Max(0, depth - 1);
                index++;
                continue;
            }

            if (depth == 0 && (char.IsLetter(current) || current == '_'))
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
                {
                    index++;
                }

                tokens.Add((sql[start..index], start, index));
                continue;
            }

            index++;
        }

        return tokens;
    }
}
