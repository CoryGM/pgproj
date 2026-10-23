using System.Text;
using System.Text.Json;

namespace PgProj.Parser;

/// <summary>
/// Reads the SQL a PL/pgSQL routine's body runs (G-303a). libpg_query parses the body (<c>pg_query_parse_plpgsql</c>) into its statements, each with the SQL it runs as text; that
/// text is parsed again as SQL, so <c>QueryAstReader</c> can read what it uses exactly as it does a view's query or a <c>LANGUAGE sql</c> body. Nothing here parses PL/pgSQL
/// itself: libpg_query's PostgreSQL grammar does, and the SQL fragments go through the same parser as every other statement.
/// <para>
/// libpg_query cannot write out a body that has <c>RETURN QUERY</c> or <c>RETURN NEXT</c>: the JSON stops at the statement. Such a body is read again with those two statements
/// rewritten as plain ones (<c>RETURN QUERY SELECT ...</c> as <c>SELECT ...</c>, <c>RETURN NEXT x</c> as <c>PERFORM x</c>), which keeps every query and expression and is only
/// ever read, never deployed. A body still unreadable has no references, and nothing is reported for it.
/// </para>
/// </summary>
internal static class PlpgsqlBodyReader
{
    /// <summary>
    /// The pg_query trees (each a JSON array of statements) of the SQL the body of <paramref name="statementSql"/> runs, or null when the body cannot be read.
    /// <paramref name="body"/> is the body text, which <paramref name="statementSql"/> contains verbatim when it is dollar-quoted.
    /// </summary>
    public static IReadOnlyList<string>? Read(string statementSql, string? body)
    {
        if (string.IsNullOrWhiteSpace(statementSql))
        {
            return null;
        }

        var tree = Usable(ParseFunctions(statementSql));
        if (tree is null && body is not null && statementSql.Contains(body, StringComparison.Ordinal) && Rewrite(body) is { } rewritten && rewritten != body)
        {
            tree = Usable(ParseFunctions(statementSql.Replace(body, rewritten, StringComparison.Ordinal)));
        }

        if (tree is null)
        {
            return null;
        }

        var asts = new List<string>();
        Walk(tree.RootElement, asts);
        tree.Dispose();
        return asts;
    }

    /// <summary>libpg_query's JSON for the functions in <paramref name="sql"/> as it wrote it (which is not always valid JSON, see <see cref="Usable"/>), or null when it reports an error.</summary>
    private static string? ParseFunctions(string sql)
    {
        PgQueryInterop.NativePlpgsqlParseResult result = default;
        var allocated = false;
        try
        {
            result = PgQueryInterop.ParsePlpgsql(sql);
            allocated = true;
            return PgQueryInterop.ReadError(result.Error) is not null ? null : PgQueryInterop.ReadNullableUtf8String(result.Functions);
        }
        finally
        {
            if (allocated)
            {
                PgQueryInterop.FreePlpgsqlParseResult(result);
            }
        }
    }

    /// <summary>
    /// The tree to read the statements from, or null when libpg_query's output holds none. Two things go wrong in its JSON. A trigger function's special variables (<c>tg_name</c>,
    /// <c>new</c>, <c>old</c>) are written as unbalanced braces in <c>datums</c>, which makes the document invalid but leaves the <c>action</c> object, the body's statements,
    /// intact: that object is taken on its own. And a body with <c>RETURN QUERY</c> or <c>RETURN NEXT</c> stops short, so no braces balance: that is null (see <see cref="Rewrite"/>).
    /// </summary>
    private static JsonDocument? Usable(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json, PgQueryJson.DocumentOptions);
        }
        catch (JsonException)
        {
            // Fall through to the action object.
        }

        const string marker = "\"action\":";
        var start = json.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
        {
            start++;
        }

        if (start >= json.Length || json[start] != '{')
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        for (var index = start; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                try
                {
                    return JsonDocument.Parse(json.AsMemory(start, index - start + 1), PgQueryJson.DocumentOptions);
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>The body with each <c>RETURN QUERY</c> and <c>RETURN NEXT</c> written as a statement libpg_query can write out; null when the lexer cannot read it.</summary>
    internal static string? Rewrite(string body)
    {
        if (SourceText.Scan(body) is not { } tokens)
        {
            return null;
        }

        var text = new StringBuilder(body);
        var changed = false;
        // From the end, so earlier offsets stay right.
        for (var index = tokens.Count - 2; index >= 0; index--)
        {
            var (start, end) = tokens[index];
            if (!Is(body, tokens[index], "return"))
            {
                continue;
            }

            var next = tokens[index + 1];
            if (Is(body, next, "query"))
            {
                // RETURN QUERY SELECT ... is the statement SELECT ...; RETURN QUERY EXECUTE ... is EXECUTE ...
                text.Remove(start, next.End - start).Insert(start, new string(' ', next.End - start));
                changed = true;
            }
            else if (Is(body, next, "next"))
            {
                var after = index + 2 < tokens.Count ? body.AsSpan(tokens[index + 2].Start, tokens[index + 2].End - tokens[index + 2].Start) : default;
                var bare = after.IsEmpty || after is ";";
                text.Remove(start, next.End - start).Insert(start, bare ? "perform 1" : "perform");
                changed = true;
            }
        }

        return changed ? text.ToString() : null;
    }

    private static bool Is(string text, (int Start, int End) token, string word)
        => token.End - token.Start == word.Length && string.Compare(text, token.Start, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>Every <c>PLpgSQL_expr</c> in the tree, as the tree of the SQL it holds.</summary>
    private static void Walk(JsonElement node, List<string> asts)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name == "PLpgSQL_expr" && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        AddExpression(property.Value, asts);
                    }
                    else
                    {
                        Walk(property.Value, asts);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, asts);
                }

                break;
        }
    }

    private static void AddExpression(JsonElement expression, List<string> asts)
    {
        if (!expression.TryGetProperty("query", out var query) || query.GetString() is not { } text || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var mode = expression.TryGetProperty("parseMode", out var parseMode) && parseMode.TryGetInt32(out var value) ? value : 0;
        string? sql;
        switch (mode)
        {
            case 0:
                sql = text;
                break;
            case 2:
                sql = "SELECT " + text;
                break;
            case 1:
                // A type name: the DECLARE section's types are read by the dependency analyzer.
                return;
            default:
                // An assignment: the text is "target := expression" (or "target = expression"); the expression is what is read.
                var assign = text.IndexOf(":=", StringComparison.Ordinal);
                var equals = assign >= 0 ? assign + 2 : text.IndexOf('=', StringComparison.Ordinal) + 1;
                sql = equals > 0 ? "SELECT " + text[equals..] : null;
                break;
        }

        if (sql is not null && PgQueryParser.TryParseStatementsJson(sql) is { } tree)
        {
            asts.Add(tree);
        }
    }
}
