namespace PgProj.Parser;

/// <summary>
/// Turns SQL written for one tenant's schema into template SQL (G-290d: <c>init</c> scaffolds a tenant into the template). With PostgreSQL's own lexer, so
/// nothing inside a comment or a string is mistaken for a name: the tenant's schema written before a dot is dropped with the dot (<c>tenant_acme.orders</c>
/// is <c>orders</c>), anywhere else it is the stand-in schema (<c>GRANT USAGE ON SCHEMA tenant_acme</c> is <c>... ON SCHEMA tenant</c>), and a string
/// literal that starts with it, as a <c>regclass</c> does (<c>'tenant_acme.orders_id_seq'</c>), loses it. In a <c>search_path</c> setting, where PostgreSQL
/// stores <c>SET search_path FROM CURRENT</c> as the path it found (<c>SET search_path TO 'tenant_acme', 'platform'</c>), the schema is the stand-in
/// (<c>'tenant'</c>), which each tenant's copy reads as its own schema (<see cref="MapStandIn"/>). A dollar-quoted body is left as written, since
/// it is not read as SQL here; one that still names the schema is reported, so the user can look at it.
/// </summary>
public static class TenantSchemaRewriter
{
    /// <summary>The rewritten text, and whether a body or other text the lexer does not split still names the schema.</summary>
    public sealed record Result(string Text, bool NamesSchemaInBody);

    public static Result Rewrite(string sql, string schema, string standIn)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(standIn);

        if (SourceText.Scan(sql) is not { } tokens)
        {
            return new Result(sql, sql.Contains(schema, StringComparison.Ordinal));
        }

        var standInName = SqlIdentifiers.QuoteName(standIn);
        var quotedPrefixes = new[] { schema + ".", SqlIdentifiers.QuoteName(schema) + ".", "\"" + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"." };
        var output = new System.Text.StringBuilder(sql.Length);
        var namesSchemaInBody = false;
        var position = 0;
        var inSearchPath = false;
        for (var index = 0; index < tokens.Count; index++)
        {
            var (start, end) = tokens[index];
            var text = sql[start..end];

            // A search_path setting: SET search_path TO|= entry, entry, ... (each entry a name or a string).
            if (string.Equals(text, "search_path", StringComparison.OrdinalIgnoreCase))
            {
                inSearchPath = true;
                continue;
            }

            if (inSearchPath)
            {
                if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'' && string.Equals(text[1..^1], schema, StringComparison.Ordinal))
                {
                    output.Append(sql, position, start - position).Append('\'').Append(standIn.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
                    position = end;
                    continue;
                }

                inSearchPath = text is "," or "=" || string.Equals(text, "to", StringComparison.OrdinalIgnoreCase) || text.StartsWith('\'') || text.StartsWith('"') || char.IsLetter(text[0]);
            }

            if (IsSchemaName(text, schema))
            {
                output.Append(sql, position, start - position);
                var nextIsDot = index + 1 < tokens.Count && sql[tokens[index + 1].Start..tokens[index + 1].End] == ".";
                if (nextIsDot)
                {
                    // schema.name: the name alone.
                    position = tokens[index + 1].End;
                    index++;
                }
                else
                {
                    output.Append(standInName);
                    position = end;
                }

                continue;
            }

            if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
            {
                var content = text[1..^1];
                if (quotedPrefixes.FirstOrDefault(prefix => content.StartsWith(prefix, StringComparison.Ordinal)) is { } prefix)
                {
                    output.Append(sql, position, start - position);
                    output.Append('\'').Append(content[prefix.Length..]).Append('\'');
                    position = end;
                }

                continue;
            }

            if (text.StartsWith('$') && text.Contains(schema, StringComparison.Ordinal))
            {
                namesSchemaInBody = true;
            }
        }

        output.Append(sql, position, sql.Length - position);
        return new Result(output.ToString(), namesSchemaInBody);
    }

    /// <summary>
    /// The opposite, for one tenant's copy of the template (G-290): in <paramref name="text"/> (a routine's <c>search_path</c> setting as written), each entry
    /// naming the stand-in, as a name or a string, names <paramref name="schema"/> instead.
    /// </summary>
    public static string MapStandIn(string text, string standIn, string schema)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (SourceText.Scan(text) is not { } tokens)
        {
            return text;
        }

        var output = new System.Text.StringBuilder(text.Length);
        var position = 0;
        foreach (var (start, end) in tokens)
        {
            var token = text[start..end];
            string? replacement = null;
            if (token.Length >= 2 && token[0] == '\'' && token[^1] == '\'' && string.Equals(token[1..^1].Replace("''", "'", StringComparison.Ordinal), standIn, StringComparison.Ordinal))
            {
                replacement = "'" + schema.Replace("'", "''", StringComparison.Ordinal) + "'";
            }
            else if (IsSchemaName(token, standIn))
            {
                replacement = SqlIdentifiers.QuoteName(schema);
            }

            if (replacement is not null)
            {
                output.Append(text, position, start - position).Append(replacement);
                position = end;
            }
        }

        return output.Append(text, position, text.Length - position).ToString();
    }

    /// <summary>An identifier token naming <paramref name="schema"/>: unquoted (as PostgreSQL folds it) or double-quoted.</summary>
    private static bool IsSchemaName(string token, string schema)
        => token.Length >= 2 && token[0] == '"' && token[^1] == '"'
            ? string.Equals(token[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal), schema, StringComparison.Ordinal)
            : string.Equals(token.ToLowerInvariant(), schema, StringComparison.Ordinal) && token.All(static character => char.IsLetterOrDigit(character) || character is '_' or '$');
}
