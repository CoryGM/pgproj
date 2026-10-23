using static PgProj.Parser.ColumnDefaultTextReader;

namespace PgProj.Parser;

/// <summary>
/// Reads the parts of a <c>CREATE TYPE</c> body that are deployed as written (G-90 part 4): a composite field's type (<c>varchar(10)</c>, not
/// pg_query's <c>pg_catalog.varchar</c>) and a range option's value. The statement is tokenized with the tokenizer that reads column defaults,
/// so a comment, a string, or a quoted name inside the body is never split on or read as text. The field names, enum labels, and option names
/// come from pg_query's tree; only the as-written text of each type and value is sliced here. Before, the body was cut out with a regular
/// expression and split on commas character by character, so a comment inside it (<c>x integer, -- the x, y pair</c>) broke the fields or the
/// labels apart: a composite type's fields were misread and its deploy failed, and an enum label was silently dropped.
/// </summary>
internal static class TypeBodyTextReader
{
    /// <summary>The type of each composite field as written, in order, or null when the body cannot be read.</summary>
    public static IReadOnlyList<string>? ReadCompositeFieldTypes(string? statementSql)
    {
        var elements = ReadBodyElements(statementSql, afterWord: "as");
        if (elements is null)
        {
            return null;
        }

        var (sql, tokens, segments) = elements.Value;
        var types = new List<string>();
        foreach (var (first, last) in segments)
        {
            // The field's name is the first token; its type is everything after it, up to a COLLATE, which is read from the tree (G-192).
            if (first + 1 > last)
            {
                return null;
            }

            var end = last;
            for (var index = first + 2; index <= last; index++)
            {
                if (IsWord(sql, tokens[index], "collate"))
                {
                    end = index - 1;
                    break;
                }
            }

            types.Add(sql[tokens[first + 1].Start..tokens[end].End].Trim());
        }

        return types;
    }

    /// <summary>Each <c>name = value</c> option of <c>AS RANGE ( ... )</c> as written, in order, or null when the body cannot be read.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>>? ReadRangeOptions(string? statementSql)
    {
        var elements = ReadBodyElements(statementSql, afterWord: "range");
        if (elements is null)
        {
            return null;
        }

        var (sql, tokens, segments) = elements.Value;
        var options = new List<KeyValuePair<string, string>>();
        foreach (var (first, last) in segments)
        {
            if (first + 2 > last || !IsPunctuation(sql, tokens[first + 1], '='))
            {
                continue;
            }

            options.Add(new KeyValuePair<string, string>(
                sql[tokens[first].Start..tokens[first].End],
                sql[tokens[first + 2].Start..tokens[last].End].Trim()));
        }

        return options;
    }

    /// <summary>The top-level comma-separated elements of the parenthesized list that follows <paramref name="afterWord"/>, as token ranges.</summary>
    private static (string Sql, List<Token> Tokens, List<(int First, int Last)> Segments)? ReadBodyElements(string? statementSql, string afterWord)
    {
        if (string.IsNullOrEmpty(statementSql))
        {
            return null;
        }

        var tokens = Tokenize(statementSql);
        var open = -1;
        for (var i = 1; i < tokens.Count; i++)
        {
            if (IsPunctuation(statementSql, tokens[i], '(') && IsWord(statementSql, tokens[i - 1], afterWord))
            {
                open = i;
                break;
            }
        }

        if (open < 0)
        {
            return null;
        }

        var segments = new List<(int First, int Last)>();
        var depth = 0;
        var start = open + 1;
        for (var i = open + 1; i < tokens.Count; i++)
        {
            if (IsPunctuation(statementSql, tokens[i], '(') || IsPunctuation(statementSql, tokens[i], '['))
            {
                depth++;
            }
            else if (depth > 0 && (IsPunctuation(statementSql, tokens[i], ')') || IsPunctuation(statementSql, tokens[i], ']')))
            {
                depth--;
            }
            else if (depth == 0 && (IsPunctuation(statementSql, tokens[i], ',') || IsPunctuation(statementSql, tokens[i], ')')))
            {
                if (i > start)
                {
                    segments.Add((start, i - 1));
                }

                if (IsPunctuation(statementSql, tokens[i], ')'))
                {
                    return (statementSql, tokens, segments);
                }

                start = i + 1;
            }
        }

        return null;
    }
}
