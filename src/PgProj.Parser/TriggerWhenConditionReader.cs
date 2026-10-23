namespace PgProj.Parser;

/// <summary>
/// Reads the text of a trigger's <c>WHEN (...)</c> condition out of its <c>CREATE TRIGGER</c> statement (G-40), as written, so the
/// canonical comparison form never replaces it. pg_query keeps the condition as a syntax tree, which cannot give the text back.
/// </summary>
internal static class TriggerWhenConditionReader
{
    /// <summary>Returns what lies between the parentheses of <c>WHEN ( ... )</c>, trimmed, or null when the statement has no such clause.</summary>
    public static string? TryRead(string? statementSql)
    {
        if (string.IsNullOrWhiteSpace(statementSql))
        {
            return null;
        }

        var tokens = ColumnDefaultTextReader.Tokenize(statementSql);
        var depth = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (ColumnDefaultTextReader.IsPunctuation(statementSql, token, '('))
            {
                depth++;
                continue;
            }

            if (ColumnDefaultTextReader.IsPunctuation(statementSql, token, ')'))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            // The first WHEN outside every parenthesis: strings and quoted identifiers are not words, so a 'when' inside one never matches.
            if (depth != 0 || !ColumnDefaultTextReader.IsWord(statementSql, token, "when")
                || index + 1 >= tokens.Count || !ColumnDefaultTextReader.IsPunctuation(statementSql, tokens[index + 1], '('))
            {
                continue;
            }

            var nesting = 0;
            for (var close = index + 1; close < tokens.Count; close++)
            {
                if (ColumnDefaultTextReader.IsPunctuation(statementSql, tokens[close], '('))
                {
                    nesting++;
                }
                else if (ColumnDefaultTextReader.IsPunctuation(statementSql, tokens[close], ')') && --nesting == 0)
                {
                    var start = tokens[index + 1].End;
                    var text = statementSql[start..tokens[close].Start].Trim();
                    return text.Length == 0 ? null : text;
                }
            }

            return null;
        }

        return null;
    }
}
