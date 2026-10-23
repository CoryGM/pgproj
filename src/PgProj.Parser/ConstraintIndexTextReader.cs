using static PgProj.Parser.ColumnDefaultTextReader;

namespace PgProj.Parser;

/// <summary>
/// Reads the expressions of a <c>CHECK</c> constraint and of <c>CREATE INDEX</c> (key expressions and the <c>WHERE</c> predicate) as the
/// author wrote them (G-73). pg_query's SQL renderer in <see cref="PgQueryParser"/> handles only a few node kinds and drops the rest
/// (<c>BETWEEN</c>, <c>IN</c>, <c>CASE</c>, subscripts, <c>COLLATE</c>, parentheses), so its output changes what a deployed constraint means.
/// The text is sliced from the statement, using the tokenizer that reads column defaults, so strings, quoted names and comments are safe.
/// </summary>
internal static class ConstraintIndexTextReader
{
    /// <summary>
    /// The expression inside <c>CHECK ( ... )</c> for the constraint that starts at <paramref name="location"/> (its
    /// <c>CONSTRAINT name</c> when named, otherwise <c>CHECK</c>), or null when it cannot be found.
    /// </summary>
    public static string? TryReadCheckExpression(string? statementSql, int location)
    {
        if (string.IsNullOrEmpty(statementSql) || location < 0 || location >= statementSql.Length)
        {
            return null;
        }

        var tokens = Tokenize(statementSql);
        var index = tokens.FindIndex(token => token.Start >= location);
        if (index < 0)
        {
            return null;
        }

        if (IsWord(statementSql, tokens[index], "constraint"))
        {
            index += 2;
        }

        if (index + 1 >= tokens.Count
            || !IsWord(statementSql, tokens[index], "check")
            || !IsPunctuation(statementSql, tokens[index + 1], '('))
        {
            return null;
        }

        var close = FindClosingParenthesis(statementSql, tokens, index + 1);
        return close < 0 ? null : Slice(statementSql, tokens, index + 2, close - 1);
    }

    /// <summary>
    /// The expression after the <c>DEFAULT</c> that starts at <paramref name="location"/> (a domain's own default, G-77), ending at the
    /// next top-level <c>NOT</c>, <c>NULL</c>, <c>COLLATE</c>, or <c>CONSTRAINT</c>/<c>CHECK</c> keyword or the statement's end - the same
    /// boundary PostgreSQL's own restricted-expression (<c>b_expr</c>) grammar enforces for a column's <c>DEFAULT</c>
    /// (<see cref="ColumnDefaultTextReader"/>).
    /// </summary>
    public static string? TryReadDefaultExpression(string? statementSql, int location)
    {
        if (string.IsNullOrEmpty(statementSql) || location < 0 || location >= statementSql.Length)
        {
            return null;
        }

        var tokens = Tokenize(statementSql);
        var index = tokens.FindIndex(token => token.Start >= location);
        if (index < 0 || !IsWord(statementSql, tokens[index], "default"))
        {
            return null;
        }

        var start = index + 1;
        var depth = 0;
        var end = start - 1;
        for (var i = start; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (IsPunctuation(statementSql, token, '('))
            {
                depth++;
            }
            else if (IsPunctuation(statementSql, token, ')'))
            {
                depth--;
            }
            else if (depth == 0
                && (IsPunctuation(statementSql, token, ';')
                    || IsWord(statementSql, token, "not")
                    || IsWord(statementSql, token, "null")
                    || IsWord(statementSql, token, "collate")
                    || IsWord(statementSql, token, "constraint")
                    || IsWord(statementSql, token, "check")))
            {
                break;
            }

            end = i;
        }

        return start > end ? null : Slice(statementSql, tokens, start, end);
    }

    /// <summary>
    /// For each key element of a <c>CREATE INDEX</c>, in order, the text of its expression when it is one (<c>(expr)</c> or a function
    /// call such as <c>lower(n)</c>) and null for a plain column. The list is empty when the statement is not a <c>CREATE INDEX</c>.
    /// </summary>
    public static IReadOnlyList<string?> ReadKeyExpressions(string? statementSql)
    {
        var result = new List<string?>();
        if (string.IsNullOrEmpty(statementSql))
        {
            return result;
        }

        var tokens = Tokenize(statementSql);
        var open = FindElementListStart(statementSql, tokens);
        if (open < 0)
        {
            return result;
        }

        var close = FindClosingParenthesis(statementSql, tokens, open);
        if (close < 0)
        {
            return result;
        }

        var elementStart = open + 1;
        var depth = 0;
        for (var i = open + 1; i <= close; i++)
        {
            var token = tokens[i];
            if (i == close || (depth == 0 && IsPunctuation(statementSql, token, ',')))
            {
                result.Add(ReadElementExpression(statementSql, tokens, elementStart, i - 1));
                elementStart = i + 1;
            }
            else if (IsPunctuation(statementSql, token, '(') || IsPunctuation(statementSql, token, '['))
            {
                depth++;
            }
            else if (IsPunctuation(statementSql, token, ')') || IsPunctuation(statementSql, token, ']'))
            {
                depth--;
            }
        }

        return result;
    }

    /// <summary>The text after the <c>WHERE</c> that follows a <c>CREATE INDEX</c>'s element list, or null when there is none.</summary>
    public static string? TryReadIndexPredicate(string? statementSql)
    {
        if (string.IsNullOrEmpty(statementSql))
        {
            return null;
        }

        var tokens = Tokenize(statementSql);
        var open = FindElementListStart(statementSql, tokens);
        var close = open < 0 ? -1 : FindClosingParenthesis(statementSql, tokens, open);
        if (close < 0)
        {
            return null;
        }

        var depth = 0;
        for (var i = close + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (IsPunctuation(statementSql, token, '('))
            {
                depth++;
            }
            else if (IsPunctuation(statementSql, token, ')'))
            {
                depth--;
            }
            else if (depth == 0 && IsWord(statementSql, token, "where"))
            {
                var last = tokens.Count - 1;
                while (last > i && IsPunctuation(statementSql, tokens[last], ';'))
                {
                    last--;
                }

                return Slice(statementSql, tokens, i + 1, last);
            }
        }

        return null;
    }

    /// <summary>The expression inside a <c>CREATE POLICY</c> statement's <c>USING ( ... )</c> clause, or null when there is none (G-76).</summary>
    public static string? TryReadPolicyUsingExpression(string? statementSql)
        => TryReadKeywordParenthesizedExpression(statementSql, ["using"]);

    /// <summary>The expression inside a <c>CREATE POLICY</c> statement's <c>WITH CHECK ( ... )</c> clause, or null when there is none (G-76).</summary>
    public static string? TryReadPolicyWithCheckExpression(string? statementSql)
        => TryReadKeywordParenthesizedExpression(statementSql, ["with", "check"]);

    /// <summary>
    /// The text inside the parenthesized expression that follows a top-level (paren-depth 0) run of <paramref name="keywords"/>,
    /// or null when that keyword phrase never appears at the top level. <c>USING</c> and <c>WITH CHECK</c> each appear at most
    /// once, at the top level, in a <c>CREATE POLICY</c>/<c>ALTER POLICY</c> statement, so scanning the whole statement (rather
    /// than anchoring to a known start, as <see cref="TryReadIndexPredicate"/> does for <c>CREATE INDEX</c>) is safe: a nested
    /// occurrence of the same words (inside a subquery's own parentheses) is at depth &gt; 0 and skipped.
    /// </summary>
    private static string? TryReadKeywordParenthesizedExpression(string? statementSql, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrEmpty(statementSql))
        {
            return null;
        }

        var tokens = Tokenize(statementSql);
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (IsPunctuation(statementSql, tokens[i], '('))
            {
                depth++;
                continue;
            }

            if (IsPunctuation(statementSql, tokens[i], ')'))
            {
                depth--;
                continue;
            }

            if (depth != 0 || !IsWord(statementSql, tokens[i], keywords[0]))
            {
                continue;
            }

            var matchEnd = i + 1;
            var matched = true;
            for (var k = 1; k < keywords.Count; k++)
            {
                if (matchEnd >= tokens.Count || !IsWord(statementSql, tokens[matchEnd], keywords[k]))
                {
                    matched = false;
                    break;
                }

                matchEnd++;
            }

            if (!matched || matchEnd >= tokens.Count || !IsPunctuation(statementSql, tokens[matchEnd], '('))
            {
                continue;
            }

            var close = FindClosingParenthesis(statementSql, tokens, matchEnd);
            return close < 0 ? null : Slice(statementSql, tokens, matchEnd + 1, close - 1);
        }

        return null;
    }

    private static string? ReadElementExpression(string sql, List<Token> tokens, int first, int last)
    {
        if (first > last)
        {
            return null;
        }

        if (IsPunctuation(sql, tokens[first], '('))
        {
            var close = FindClosingParenthesis(sql, tokens, first);
            return close < 0 ? null : Slice(sql, tokens, first + 1, close - 1);
        }

        // A function call: name or schema.name, then its argument list.
        var index = first;
        while (index <= last && tokens[index].Kind is TokenKind.Word or TokenKind.QuotedIdentifier)
        {
            index++;
            if (index <= last && IsPunctuation(sql, tokens[index], '.'))
            {
                index++;
                continue;
            }

            break;
        }

        if (index > first && index <= last && IsPunctuation(sql, tokens[index], '('))
        {
            var close = FindClosingParenthesis(sql, tokens, index);
            return close < 0 ? null : Slice(sql, tokens, first, close);
        }

        return null;
    }

    private static int FindElementListStart(string sql, List<Token> tokens)
    {
        if (tokens.Count == 0 || !IsWord(sql, tokens[0], "create"))
        {
            return -1;
        }

        var sawIndex = false;
        for (var i = 1; i < tokens.Count; i++)
        {
            if (IsWord(sql, tokens[i], "index"))
            {
                sawIndex = true;
            }
            else if (sawIndex && IsPunctuation(sql, tokens[i], '('))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The index of the <c>)</c> that closes the <c>(</c> at <paramref name="open"/>, or -1.</summary>
    private static int FindClosingParenthesis(string sql, List<Token> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (IsPunctuation(sql, tokens[i], '('))
            {
                depth++;
            }
            else if (IsPunctuation(sql, tokens[i], ')') && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static string? Slice(string sql, List<Token> tokens, int first, int last)
        => first > last || last >= tokens.Count ? null : sql[tokens[first].Start..tokens[last].End].Trim();
}
