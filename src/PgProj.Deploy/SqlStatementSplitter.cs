using System.Text;

namespace PgProj.Deploy;

/// <summary>
/// Splits a PostgreSQL SQL script into individual executable statements.
/// Correctly handles dollar-quoted bodies (e.g. $$ … $$ and $body$ … $body$),
/// standard single-quoted string literals, line comments (--), and block comments (/* … */).
/// </summary>
public static class SqlStatementSplitter
{
    /// <summary>
    /// Splits <paramref name="sql"/> into individual statements, trimmed of surrounding whitespace.
    /// Empty and whitespace-only statements are excluded from the result.
    /// </summary>
    public static IReadOnlyList<string> Split(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var statements = new List<string>();
        var current = new StringBuilder();

        var i = 0;
        var len = sql.Length;

        // Inside a SQL-standard routine body (BEGIN ATOMIC ... END, G-95) a semicolon ends a statement of the body, not of the script.
        // As psql does, CASE ... END is counted too, since it ends with the same word.
        var blockDepth = 0;
        string? previousWord = null;

        // As psql does, a semicolon inside parentheses does not end the statement: a rule's action list is (INSERT ...; INSERT ...) (G-230).
        var parenthesisDepth = 0;

        while (i < len)
        {
            var ch = sql[i];

            // Line comment: -- … \n  — consume to end of line, include in current statement for fidelity.
            if (ch == '-' && i + 1 < len && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                if (end == -1)
                {
                    current.Append(sql, i, len - i);
                    i = len;
                }
                else
                {
                    current.Append(sql, i, end - i + 1);
                    i = end + 1;
                }

                continue;
            }

            // Block comment: /* … */
            if (ch == '/' && i + 1 < len && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end == -1)
                {
                    // Unterminated block comment — consume to end.
                    current.Append(sql, i, len - i);
                    i = len;
                }
                else
                {
                    current.Append(sql, i, end - i + 2);
                    i = end + 2;
                }

                continue;
            }

            if (ch == '"')
            {
                // Quoted identifier ("" is an escaped quote inside): a semicolon in it is part of the name.
                current.Append(ch);
                i++;
                while (i < len)
                {
                    var qc = sql[i];
                    current.Append(qc);
                    i++;
                    if (qc == '"')
                    {
                        if (i < len && sql[i] == '"')
                        {
                            current.Append(sql[i]);
                            i++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                continue;
            }

            // Standard single-quoted string literal: '…' ('' is an escaped quote inside; after E, a backslash escapes the next character, G-230).
            if (ch == '\'')
            {
                var escapeString = i > 0 && sql[i - 1] is 'e' or 'E' && (i == 1 || !(char.IsLetterOrDigit(sql[i - 2]) || sql[i - 2] is '_' or '$'));
                current.Append(ch);
                i++;
                while (i < len)
                {
                    var sc = sql[i];
                    current.Append(sc);
                    i++;
                    if (escapeString && sc == '\\' && i < len)
                    {
                        current.Append(sql[i]);
                        i++;
                        continue;
                    }

                    if (sc == '\'')
                    {
                        // Peek: '' means escaped quote, not end of literal.
                        if (i < len && sql[i] == '\'')
                        {
                            current.Append(sql[i]);
                            i++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                continue;
            }

            // Dollar-quoting: $tag$ … $tag$
            // A dollar-quote tag starts with $ and ends with $, containing only letters, digits, or _.
            if (ch == '$')
            {
                var tagEnd = TryReadDollarQuoteTag(sql, i);
                if (tagEnd >= 0)
                {
                    var tag = sql.Substring(i, tagEnd - i + 1); // includes both $ delimiters
                    current.Append(tag);
                    i = tagEnd + 1;

                    // Scan for closing tag.
                    var closeIndex = sql.IndexOf(tag, i, StringComparison.Ordinal);
                    if (closeIndex == -1)
                    {
                        // Unterminated dollar-quote — consume to end.
                        current.Append(sql, i, len - i);
                        i = len;
                    }
                    else
                    {
                        current.Append(sql, i, closeIndex - i + tag.Length);
                        i = closeIndex + tag.Length;
                    }

                    continue;
                }
            }

            // A word: tracked for BEGIN ATOMIC ... END.
            if ((char.IsLetter(ch) || ch == '_') && (i == 0 || !(char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] is '_' or '$' or '"')))
            {
                var wordEnd = i + 1;
                while (wordEnd < len && (char.IsLetterOrDigit(sql[wordEnd]) || sql[wordEnd] is '_' or '$'))
                {
                    wordEnd++;
                }

                var word = sql[i..wordEnd];
                if (string.Equals(word, "ATOMIC", StringComparison.OrdinalIgnoreCase) && string.Equals(previousWord, "BEGIN", StringComparison.OrdinalIgnoreCase)
                    && IsRoutineStatement(current.ToString()))
                {
                    blockDepth++;
                }
                else if (blockDepth > 0 && string.Equals(word, "CASE", StringComparison.OrdinalIgnoreCase))
                {
                    blockDepth++;
                }
                else if (blockDepth > 0 && string.Equals(word, "END", StringComparison.OrdinalIgnoreCase))
                {
                    blockDepth--;
                }

                previousWord = word;
                current.Append(sql, i, wordEnd - i);
                i = wordEnd;
                continue;
            }

            if (ch == '(')
            {
                parenthesisDepth++;
            }
            else if (ch == ')' && parenthesisDepth > 0)
            {
                parenthesisDepth--;
            }

            // Statement terminator.
            if (ch == ';' && (blockDepth > 0 || parenthesisDepth > 0))
            {
                current.Append(ch);
                i++;
                continue;
            }

            if (ch == ';')
            {
                var statement = current.ToString().Trim();
                if (statement.Length > 0)
                {
                    statements.Add(statement);
                }

                current.Clear();
                i++;
                continue;
            }

            current.Append(ch);
            i++;
        }

        // Trailing content without a terminating semicolon.
        var trailing = current.ToString().Trim();
        if (trailing.Length > 0)
        {
            statements.Add(trailing);
        }

        return statements;
    }

    /// <summary>True when the statement so far is a <c>CREATE [OR REPLACE] FUNCTION|PROCEDURE</c>, the only place <c>BEGIN ATOMIC</c> opens a body.</summary>
    private static bool IsRoutineStatement(string statementSoFar)
    {
        // Leading comment lines (a reason, G-103) are not part of the statement.
        var lines = statementSoFar.Split('\n').SkipWhile(static line => line.TrimStart().StartsWith("--", StringComparison.Ordinal) || line.Trim().Length == 0);
        statementSoFar = string.Join('\n', lines);
        var words = statementSoFar.Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries);
        var index = words.Length > 0 && string.Equals(words[0], "CREATE", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
        if (index > 0 && index + 1 < words.Length && string.Equals(words[index], "OR", StringComparison.OrdinalIgnoreCase))
        {
            index += 2;
        }

        return index > 0 && index < words.Length
            && (words[index].Equals("FUNCTION", StringComparison.OrdinalIgnoreCase) || words[index].Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Attempts to read a dollar-quote opening tag starting at <paramref name="start"/>.
    /// Returns the index of the closing <c>$</c> of the tag, or <c>-1</c> if the position
    /// does not start a valid dollar-quote tag.
    /// A valid tag matches <c>$[A-Za-z0-9_]*$</c>.
    /// </summary>
    private static int TryReadDollarQuoteTag(string sql, int start)
    {
        if (sql[start] != '$')
        {
            return -1;
        }

        var i = start + 1;
        while (i < sql.Length && sql[i] != '$')
        {
            var c = sql[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return -1; // Invalid tag character — not a dollar-quote.
            }

            i++;
        }

        if (i >= sql.Length)
        {
            return -1; // No closing $ found.
        }

        return i; // sql[i] == '$' — this is the end of the opening tag.
    }
}
