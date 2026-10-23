using static PgProj.Parser.ColumnDefaultTextReader;

namespace PgProj.Parser;

/// <summary>
/// Reads a <c>CREATE TABLE ... WITH (...)</c> storage-parameter list as the author wrote it (G-24b). pg_query gives each
/// option's name and the character offset where it starts (<c>DefElem.location</c>) but not its value rendered back reliably for
/// every kind (a bare word, a quoted string, a number), so each option's text is sliced from the statement instead, the same
/// reasoning as <see cref="ConstraintIndexTextReader"/> (G-73).
/// </summary>
internal static class TableStorageTextReader
{
    /// <summary>
    /// The text of the option that starts at each of <paramref name="locations"/> (already adjusted to be relative to
    /// <paramref name="statementSql"/>), in the same order, up to the next top-level comma or the closing parenthesis of the
    /// <c>WITH (...)</c> clause. A location past the end of the text, or where no token starts, yields no entry for it.
    /// </summary>
    public static IReadOnlyList<string> ReadOptions(string statementSql, IReadOnlyList<int> locations)
    {
        var result = new List<string>();
        if (locations.Count == 0 || string.IsNullOrEmpty(statementSql))
        {
            return result;
        }

        var tokens = Tokenize(statementSql);
        foreach (var location in locations)
        {
            var start = tokens.FindIndex(token => token.Start >= location);
            if (start < 0)
            {
                continue;
            }

            result.Add(SliceOption(statementSql, tokens, start));
        }

        return result;
    }

    private static string SliceOption(string sql, List<Token> tokens, int start)
    {
        var depth = 0;
        var last = start;
        for (var i = start; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (IsPunctuation(sql, token, '('))
            {
                depth++;
            }
            else if (IsPunctuation(sql, token, ')'))
            {
                if (depth == 0)
                {
                    // The WITH clause's own closing parenthesis: this option ends just before it.
                    break;
                }

                depth--;
            }
            else if (depth == 0 && IsPunctuation(sql, token, ','))
            {
                break;
            }

            last = i;
        }

        return sql[tokens[start].Start..tokens[last].End].Trim();
    }
}
