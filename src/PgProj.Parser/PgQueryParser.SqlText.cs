using System.Globalization;
using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    /// <summary>
    /// Where <see cref="GetStatementSql"/>'s text starts in <paramref name="sql"/>. pg_query's <c>location</c> fields count from the start of
    /// the whole input, so a position inside a statement is <c>location</c> minus this.
    /// </summary>
    private static int GetStatementOffset(string sql, JsonElement rawStatement)
    {
        var location = TryGetDirectInt32Property(rawStatement, "stmt_location", out var statementLocation)
            ? statementLocation
            : 0;
        if (location < 0 || location >= sql.Length)
        {
            return 0;
        }

        var start = location;
        while (start < sql.Length && char.IsWhiteSpace(sql[start]))
        {
            start++;
        }

        return start;
    }

    private static string GetStatementSql(string sql, JsonElement rawStatement)
    {
        var location = TryGetDirectInt32Property(rawStatement, "stmt_location", out var statementLocation)
            ? statementLocation
            : 0;
        var length = TryGetDirectInt32Property(rawStatement, "stmt_len", out var statementLength)
            ? statementLength
            : 0;
        if (location < 0 || location >= sql.Length)
        {
            return sql;
        }

        if (length <= 0 || location + length > sql.Length)
        {
            length = sql.Length - location;
        }

        return sql.Substring(location, length).Trim();
    }

    private static bool TrySkipQuotedOrComment(string sql, ref int index)
    {
        if (sql[index] == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
        {
            index += 2;
            while (index < sql.Length && sql[index] is not '\r' and not '\n')
            {
                index++;
            }

            return true;
        }

        if (sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
        {
            index += 2;
            var nestedDepth = 1;
            while (index < sql.Length && nestedDepth > 0)
            {
                if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
                {
                    nestedDepth++;
                    index += 2;
                }
                else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
                {
                    nestedDepth--;
                    index += 2;
                }
                else
                {
                    index++;
                }
            }

            return true;
        }

        if (sql[index] is '\'' or '"')
        {
            var quote = sql[index++];
            while (index < sql.Length)
            {
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

                break;
            }

            return true;
        }

        if (sql[index] == '$')
        {
            var delimiterEnd = index + 1;
            while (delimiterEnd < sql.Length && (char.IsLetterOrDigit(sql[delimiterEnd]) || sql[delimiterEnd] == '_'))
            {
                delimiterEnd++;
            }

            if (delimiterEnd < sql.Length && sql[delimiterEnd] == '$')
            {
                var delimiter = sql[index..(delimiterEnd + 1)];
                var closingDelimiter = sql.IndexOf(delimiter, delimiterEnd + 1, StringComparison.Ordinal);
                index = closingDelimiter < 0 ? sql.Length : closingDelimiter + delimiter.Length;
                return true;
            }
        }

        return false;
    }

    private static bool ContainsOnlyTrivia(ReadOnlySpan<char> value)
    {
        var text = value.ToString();
        for (var index = 0; index < text.Length;)
        {
            if (char.IsWhiteSpace(text[index]) || text[index] == ';')
            {
                index++;
                continue;
            }

            if (TrySkipQuotedOrComment(text, ref index))
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
