namespace PgProj.Parser;

/// <summary>
/// psql's variable syntax in a tenant's pre- and post-deploy scripts (G-290e, the user's choice of 2026-10-15): <c>:"name"</c> is the value as a quoted
/// identifier and <c>:'name'</c> as a quoted string, as <c>psql -v name=value</c> would substitute them, so a script runs in psql unchanged. Found with
/// PostgreSQL's lexer: a <c>:</c> token followed, with nothing between, by a double- or single-quoted token, so nothing inside a string, a comment, or a
/// dollar-quoted body is touched (psql does not substitute there either).
/// </summary>
public static class ScriptVariables
{
    /// <summary>The text with each known variable substituted, and the names of the variables it uses that <paramref name="values"/> does not have.</summary>
    public sealed record Result(string Text, IReadOnlyList<string> Unknown, bool UsesAny);

    /// <summary>True when <paramref name="sql"/> has anything but comments and white space (G-290e: a script whose files were all skipped has none).</summary>
    public static bool HasStatements(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return SourceText.Scan(sql) is { } tokens
            ? tokens.Any(token => sql[token.Start..token.End] is var text && !text.StartsWith("--", StringComparison.Ordinal) && !text.StartsWith("/*", StringComparison.Ordinal))
            : !string.IsNullOrWhiteSpace(sql);
    }

    public static Result Substitute(string sql, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(values);

        if (!sql.Contains(':', StringComparison.Ordinal) || SourceText.Scan(sql) is not { } tokens)
        {
            return new Result(sql, [], false);
        }

        var output = new System.Text.StringBuilder(sql.Length);
        var unknown = new List<string>();
        var usesAny = false;
        var position = 0;
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            var (start, end) = tokens[index];
            var (nextStart, nextEnd) = tokens[index + 1];
            if (end - start != 1 || sql[start] != ':' || nextStart != end || nextEnd - nextStart < 3)
            {
                continue;
            }

            var quote = sql[nextStart];
            if (quote is not ('"' or '\'') || sql[nextEnd - 1] != quote)
            {
                continue;
            }

            var name = sql[(nextStart + 1)..(nextEnd - 1)];
            if (!name.All(static character => char.IsLetterOrDigit(character) || character == '_'))
            {
                continue;
            }

            usesAny = true;
            if (!values.TryGetValue(name, out var value))
            {
                if (!unknown.Contains(name, StringComparer.Ordinal))
                {
                    unknown.Add(name);
                }

                continue;
            }

            output.Append(sql, position, start - position);
            output.Append(quote == '"'
                ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'");
            position = nextEnd;
            index++;
        }

        output.Append(sql, position, sql.Length - position);
        return new Result(output.ToString(), unknown, usesAny);
    }
}
