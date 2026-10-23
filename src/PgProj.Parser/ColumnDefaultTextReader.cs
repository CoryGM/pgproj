namespace PgProj.Parser;

/// <summary>
/// Reads a column's <c>DEFAULT</c> expression verbatim out of a <c>CREATE TABLE</c> statement (G-56), or out of the <c>ADD COLUMN</c> action of an <c>ALTER TABLE</c> statement (G-47).
/// pg_query reports a column default as a <c>CONSTR_DEFAULT</c> entry in the column's <c>constraints</c> list, and
/// the parser's SQL renderer is lossy (it drops parentheses and cannot render <c>CASE</c>, <c>COALESCE</c>, or
/// <c>CURRENT_TIMESTAMP</c>), so the text is sliced from the statement instead, the way routine bodies are. What is
/// deployed is then exactly what the author wrote (or, for the live model, what <c>pg_get_expr</c> returned).
/// </summary>
internal static class ColumnDefaultTextReader
{
    // Words that start the next column clause. PostgreSQL parses a DEFAULT as a restricted expression (b_expr), which
    // cannot contain a bare NOT, NULL, COLLATE, or a constraint keyword, so any of these at nesting level 0 ends it.
    private static readonly HashSet<string> ClauseKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "not", "null", "constraint", "primary", "references", "unique", "check", "collate", "generated", "default",
    };

    internal enum TokenKind
    {
        Word,
        QuotedIdentifier,
        Literal,
        Punctuation,
    }

    internal readonly record struct Token(TokenKind Kind, int Start, int End);

    /// <summary>
    /// Returns the default expression of <paramref name="columnName"/> as written in <paramref name="statementSql"/>,
    /// or null when the statement is neither a <c>CREATE TABLE</c> nor an <c>ALTER TABLE</c>, the column is not found, or it has no <c>DEFAULT</c>.
    /// </summary>
    public static string? TryRead(string? statementSql, string columnName)
        => ReadColumnClause(statementSql, columnName, TryReadDefault);

    /// <summary>
    /// Returns the expression of <c>GENERATED ALWAYS AS (expression) STORED</c> for <paramref name="columnName"/> as written, without the
    /// parentheses of the clause, or null when the column has none (G-71). pg_query keeps that clause in the column's <c>constraints</c> as a
    /// <c>CONSTR_GENERATED</c> entry and its SQL renderer is lossy, so the text is sliced from the statement, as a default is (G-56).
    /// </summary>
    public static string? TryReadGeneratedExpression(string? statementSql, string columnName)
        => ReadColumnClause(statementSql, columnName, TryReadGenerated);

    private static string? ReadColumnClause(string? statementSql, string columnName, Func<string, List<Token>, string?> read)
    {
        if (string.IsNullOrWhiteSpace(statementSql) || string.IsNullOrWhiteSpace(columnName))
        {
            return null;
        }

        var statement = Analyze(statementSql);
        if (statement.IsAlter)
        {
            return TryReadFromAlterTable(statementSql, statement.Actions!, columnName, read);
        }

        // The first column definition named columnName (a quoted name exactly, an unquoted one without regard to case).
        var index = int.MaxValue;
        if (statement.ByQuotedName.TryGetValue(columnName, out var quoted))
        {
            index = quoted;
        }

        if (statement.ByWordName.TryGetValue(columnName, out var word) && word < index)
        {
            index = word;
        }

        return index == int.MaxValue ? null : read(statementSql, statement.Segments[index]);
    }

    // A table's every column asks for its clauses, and finding the column definition in the statement's tokens for each would make reading
    // the statement quadratic in its length (G-310): the definitions are split once per statement text, on this thread (the readers of
    // one statement run one after another), and found by name.
    [ThreadStatic]
    private static StatementColumns? s_columns;

    private static StatementColumns Analyze(string statementSql)
    {
        if (s_columns is { } cached && ReferenceEquals(cached.Sql, statementSql))
        {
            return cached;
        }

        return s_columns = new StatementColumns(statementSql);
    }

    private sealed class StatementColumns
    {
        public StatementColumns(string sql)
        {
            Sql = sql;
            var tokens = Tokenize(sql);
            if (tokens.Count > 0 && IsWord(sql, tokens[0], "alter"))
            {
                IsAlter = true;
                Actions = SplitTopLevel(sql, tokens, 0, tokens.Count).ToList();
                return;
            }

            if (!TryFindTableBody(sql, tokens, out var bodyStart, out var bodyEnd))
            {
                return;
            }

            Segments = SplitTopLevel(sql, tokens, bodyStart, bodyEnd).ToList();
            for (var index = 0; index < Segments.Count; index++)
            {
                if (Segments[index] is not { Count: > 0 } segment)
                {
                    continue;
                }

                var first = segment[0];
                if (first.Kind == TokenKind.QuotedIdentifier && first.End - first.Start >= 2)
                {
                    ByQuotedName.TryAdd(sql.Substring(first.Start + 1, first.End - first.Start - 2).Replace("\"\"", "\"", StringComparison.Ordinal), index);
                }
                else if (first.Kind == TokenKind.Word)
                {
                    ByWordName.TryAdd(sql[first.Start..first.End], index);
                }
            }
        }

        public string Sql { get; }

        public bool IsAlter { get; }

        public List<List<Token>>? Actions { get; }

        public List<List<Token>> Segments { get; } = [];

        public Dictionary<string, int> ByQuotedName { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> ByWordName { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The text between the parentheses of <c>GENERATED ALWAYS AS ( ... )</c> in a column definition; <c>GENERATED ... AS IDENTITY</c> is not one.</summary>
    private static string? TryReadGenerated(string sql, List<Token> segment)
    {
        var depth = 0;
        for (var index = 1; index + 3 < segment.Count; index++)
        {
            var token = segment[index];
            if (IsPunctuation(sql, token, '(') || IsPunctuation(sql, token, '['))
            {
                depth++;
                continue;
            }

            if (IsPunctuation(sql, token, ')') || IsPunctuation(sql, token, ']'))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth != 0
                || !IsWord(sql, token, "generated")
                || !IsWord(sql, segment[index + 1], "always")
                || !IsWord(sql, segment[index + 2], "as")
                || !IsPunctuation(sql, segment[index + 3], '('))
            {
                continue;
            }

            var nesting = 0;
            for (var close = index + 3; close < segment.Count; close++)
            {
                if (IsPunctuation(sql, segment[close], '('))
                {
                    nesting++;
                }
                else if (IsPunctuation(sql, segment[close], ')') && --nesting == 0)
                {
                    var text = sql[segment[index + 3].End..segment[close].Start].Trim();
                    return text.Length == 0 ? null : text;
                }
            }

            return null;
        }

        return null;
    }


    /// <summary>
    /// Reads the default of a column added by <c>ALTER TABLE ... ADD [COLUMN] [IF NOT EXISTS] name type ... DEFAULT expr</c> (G-47). One
    /// statement can add several columns, each in its own top-level comma-separated action, so the actions are split first.
    /// </summary>
    private static string? TryReadFromAlterTable(string sql, List<List<Token>> actions, string columnName, Func<string, List<Token>, string?> read)
    {
        foreach (var action in actions)
        {
            var add = action.FindIndex(token => IsWord(sql, token, "add"));
            if (add < 0)
            {
                continue;
            }

            var name = add + 1;
            if (name < action.Count && IsWord(sql, action[name], "column"))
            {
                name++;
            }

            if (name + 2 < action.Count && IsWord(sql, action[name], "if") && IsWord(sql, action[name + 1], "not") && IsWord(sql, action[name + 2], "exists"))
            {
                name += 3;
            }

            if (name < action.Count && IsColumnNamed(sql, action[name], columnName))
            {
                return read(sql, action.GetRange(name, action.Count - name));
            }
        }

        return null;
    }

    private static bool TryFindTableBody(string sql, List<Token> tokens, out int bodyStart, out int bodyEnd)
    {
        bodyStart = bodyEnd = -1;

        if (tokens.Count == 0 || !IsWord(sql, tokens[0], "create"))
        {
            return false;
        }

        var sawTable = false;
        for (var index = 1; index < tokens.Count; index++)
        {
            if (IsWord(sql, tokens[index], "table"))
            {
                sawTable = true;
                continue;
            }

            if (!IsPunctuation(sql, tokens[index], '('))
            {
                continue;
            }

            if (!sawTable)
            {
                return false;
            }

            var depth = 0;
            for (var close = index; close < tokens.Count; close++)
            {
                if (IsPunctuation(sql, tokens[close], '('))
                {
                    depth++;
                }
                else if (IsPunctuation(sql, tokens[close], ')') && --depth == 0)
                {
                    bodyStart = index + 1;
                    bodyEnd = close;
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    /// <summary>Splits the table body into its column and constraint definitions at top-level commas.</summary>
    private static IEnumerable<List<Token>> SplitTopLevel(string sql, List<Token> tokens, int start, int end)
    {
        var current = new List<Token>();
        var depth = 0;
        for (var index = start; index < end; index++)
        {
            var token = tokens[index];
            if (IsPunctuation(sql, token, '(') || IsPunctuation(sql, token, '['))
            {
                depth++;
            }
            else if (IsPunctuation(sql, token, ')') || IsPunctuation(sql, token, ']'))
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && IsPunctuation(sql, token, ','))
            {
                yield return current;
                current = [];
                continue;
            }

            current.Add(token);
        }

        yield return current;
    }

    private static bool IsColumnNamed(string sql, Token token, string columnName)
    {
        switch (token.Kind)
        {
            case TokenKind.QuotedIdentifier:
                var inner = sql.Substring(token.Start + 1, token.End - token.Start - 2).Replace("\"\"", "\"", StringComparison.Ordinal);
                return string.Equals(inner, columnName, StringComparison.Ordinal);
            case TokenKind.Word:
                return string.Equals(sql[token.Start..token.End], columnName, StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    private static string? TryReadDefault(string sql, List<Token> segment)
    {
        var depth = 0;
        for (var index = 1; index < segment.Count; index++)
        {
            var token = segment[index];
            if (IsPunctuation(sql, token, '(') || IsPunctuation(sql, token, '['))
            {
                depth++;
                continue;
            }

            if (IsPunctuation(sql, token, ')') || IsPunctuation(sql, token, ']'))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth != 0 || !IsWord(sql, token, "default"))
            {
                continue;
            }

            // GENERATED BY DEFAULT AS IDENTITY and ON DELETE/UPDATE SET DEFAULT are not column defaults.
            var previous = segment[index - 1];
            if (IsWord(sql, previous, "by") || IsWord(sql, previous, "set"))
            {
                continue;
            }

            return ReadExpression(sql, segment, index + 1);
        }

        return null;
    }

    private static string? ReadExpression(string sql, List<Token> segment, int first)
    {
        var depth = 0;
        var last = first - 1;
        for (var index = first; index < segment.Count; index++)
        {
            var token = segment[index];
            if (IsPunctuation(sql, token, '(') || IsPunctuation(sql, token, '['))
            {
                depth++;
            }
            else if (IsPunctuation(sql, token, ')') || IsPunctuation(sql, token, ']'))
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && token.Kind == TokenKind.Word && ClauseKeywords.Contains(sql[token.Start..token.End]))
            {
                // DEFAULT NULL is a valid default whose first token is the keyword; anywhere else NULL ends it.
                var isLeadingNull = index == first && IsWord(sql, token, "null");
                if (!isLeadingNull)
                {
                    break;
                }
            }

            last = index;
        }

        if (last < first)
        {
            return null;
        }

        return sql[segment[first].Start..segment[last].End].Trim();
    }

    internal static bool IsWord(string sql, Token token, string word)
        => token.Kind == TokenKind.Word
            && token.End - token.Start == word.Length
            && string.Compare(sql, token.Start, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;

    internal static bool IsPunctuation(string sql, Token token, char value)
        => token.Kind == TokenKind.Punctuation && sql[token.Start] == value;

    // The readers of one statement each tokenize its text (a table's columns, constraints, and storage options, one after another), so the
    // tokens of the last text are kept, on this thread, for the next reader (G-310). The list is shared: no reader changes it.
    [ThreadStatic]
    private static (string Sql, List<Token> Tokens)? s_tokens;

    internal static List<Token> Tokenize(string sql)
    {
        if (s_tokens is { } last && ReferenceEquals(last.Sql, sql))
        {
            return last.Tokens;
        }

        var tokens = TokenizeText(sql);
        s_tokens = (sql, tokens);
        return tokens;
    }

    private static List<Token> TokenizeText(string sql)
    {
        TextPassCounter.CountTokenization();
        var tokens = new List<Token>();
        var index = 0;
        while (index < sql.Length)
        {
            var current = sql[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not '\r' and not '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index = SkipBlockComment(sql, index);
                continue;
            }

            if (current == '\'')
            {
                var end = SkipQuoted(sql, index, '\'', backslashEscapes: false);
                tokens.Add(new Token(TokenKind.Literal, index, end));
                index = end;
                continue;
            }

            if (current == '"')
            {
                var end = SkipQuoted(sql, index, '"', backslashEscapes: false);
                tokens.Add(new Token(TokenKind.QuotedIdentifier, index, end));
                index = end;
                continue;
            }

            if (current == '$' && TryReadDollarQuoted(sql, index, out var dollarEnd))
            {
                tokens.Add(new Token(TokenKind.Literal, index, dollarEnd));
                index = dollarEnd;
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
                {
                    index++;
                }

                // A one-letter prefix directly before a quote (E'..', B'..', X'..', N'..') belongs to the literal.
                if (index - start == 1 && index < sql.Length && sql[index] == '\'' && "eEbBxXnN".Contains(sql[start], StringComparison.Ordinal))
                {
                    var end = SkipQuoted(sql, index, '\'', backslashEscapes: sql[start] is 'e' or 'E');
                    tokens.Add(new Token(TokenKind.Literal, start, end));
                    index = end;
                    continue;
                }

                tokens.Add(new Token(TokenKind.Word, start, index));
                continue;
            }

            if (char.IsDigit(current))
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '.' or '_'))
                {
                    index++;
                }

                tokens.Add(new Token(TokenKind.Literal, start, index));
                continue;
            }

            tokens.Add(new Token(TokenKind.Punctuation, index, index + 1));
            index++;
        }

        return tokens;
    }

    private static int SkipBlockComment(string sql, int index)
    {
        index += 2;
        var depth = 1;
        while (index < sql.Length && depth > 0)
        {
            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                depth++;
                index += 2;
            }
            else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
            {
                depth--;
                index += 2;
            }
            else
            {
                index++;
            }
        }

        return index;
    }

    private static int SkipQuoted(string sql, int index, char quote, bool backslashEscapes)
    {
        index++;
        while (index < sql.Length)
        {
            if (backslashEscapes && sql[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (sql[index] != quote)
            {
                index++;
                continue;
            }

            index++;
            if (index < sql.Length && sql[index] == quote)
            {
                index++;
                continue;
            }

            return index;
        }

        return sql.Length;
    }

    private static bool TryReadDollarQuoted(string sql, int index, out int end)
    {
        end = index;
        var tagEnd = index + 1;
        while (tagEnd < sql.Length && (char.IsLetterOrDigit(sql[tagEnd]) || sql[tagEnd] == '_'))
        {
            tagEnd++;
        }

        // $1 is a parameter, not a quote delimiter: a delimiter's tag cannot start with a digit.
        if (tagEnd >= sql.Length || sql[tagEnd] != '$' || (tagEnd > index + 1 && char.IsDigit(sql[index + 1])))
        {
            return false;
        }

        var delimiter = sql[index..(tagEnd + 1)];
        var close = sql.IndexOf(delimiter, tagEnd + 1, StringComparison.Ordinal);
        end = close < 0 ? sql.Length : close + delimiter.Length;
        return true;
    }
}
