namespace PgProj.Parser;

/// <summary>
/// psql's variable syntax in the pre- and post-deploy scripts (G-290e, the user's choice of 2026-10-15, for a tenant's; G-336 for the project's own, its values
/// from the environment): <c>:"name"</c> is the value as a quoted
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

        var output = new System.Text.StringBuilder(sql.Length);
        var unknown = new List<string>();
        var usesAny = false;
        var position = 0;
        foreach (var use in Find(sql))
        {
            usesAny = true;
            if (!values.TryGetValue(use.Name, out var value))
            {
                if (!unknown.Contains(use.Name, StringComparer.Ordinal))
                {
                    unknown.Add(use.Name);
                }

                continue;
            }

            output.Append(sql, position, use.Start - position);
            output.Append(use.Quote == '"'
                ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'");
            position = use.End;
        }

        output.Append(sql, position, sql.Length - position);
        return new Result(output.ToString(), unknown, usesAny);
    }

    /// <summary>The names of the variables <paramref name="sql"/> uses, each once, in the order they first appear (G-336).</summary>
    public static IReadOnlyList<string> Names(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return Find(sql).Select(static use => use.Name).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// <paramref name="sql"/> with each variable replaced by a literal of the same length (<c>:'name'</c> by <c>'xxxxx'</c>, <c>:"name"</c> by <c>"xxxxx"</c>), so
    /// PostgreSQL's parser can check a script whose values are not known (G-336: <c>build</c> never reads them) and every position it reports is still the
    /// author's.
    /// </summary>
    public static string StandIn(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var uses = Find(sql).ToArray();
        if (uses.Length == 0)
        {
            return sql;
        }

        var characters = sql.ToCharArray();
        foreach (var use in uses)
        {
            characters[use.Start] = use.Quote;
            for (var index = use.Start + 1; index < use.End - 1; index++)
            {
                characters[index] = 'x';
            }

            characters[use.End - 1] = use.Quote;
        }

        return new string(characters);
    }

    /// <summary>One use of a variable: where it starts (its <c>:</c>) and ends, its quote, and its name.</summary>
    private readonly record struct Use(int Start, int End, char Quote, string Name);

    private static IEnumerable<Use> Find(string sql)
    {
        if (!sql.Contains(':', StringComparison.Ordinal) || SourceText.Scan(sql) is not { } tokens)
        {
            yield break;
        }

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

            yield return new Use(start, nextEnd, quote, name);
            index++;
        }
    }
}
