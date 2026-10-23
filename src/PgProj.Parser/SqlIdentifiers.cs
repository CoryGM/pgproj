using System.Text;

namespace PgProj.Parser;

/// <summary>
/// Reading and writing PostgreSQL identifiers (G-70). PostgreSQL folds an unquoted identifier to lower case and keeps the case of a
/// quoted one, so an identifier that reaches the model with an upper-case letter (or any character outside <c>[a-z0-9_$]</c>) can only
/// have been quoted, and must be quoted again wherever pgproj writes it back out.
/// </summary>
public static class SqlIdentifiers
{
    /// <summary>
    /// How two object names are compared: exactly, as PostgreSQL compares them (G-206). PostgreSQL folds an unquoted name to lower case when it
    /// parses it and keeps a quoted one as written, and pg_query gives the names already folded, so <c>"T"</c> and <c>t</c> are two objects
    /// and a change of case is a change. A name read from text rather than from the parse tree is folded first (<see cref="SplitQualifiedName"/>).
    /// Keywords, setting names, and option names are not names in this sense, and stay case-insensitive.
    /// </summary>
    public static StringComparer NameComparer { get; } = StringComparer.Ordinal;

    /// <summary>
    /// A name a user types outside SQL (a command-line option, a <c>pgproj.json</c> setting), read the way PostgreSQL reads a name in SQL and the
    /// way <c>pg_dump -n</c> reads one (G-206): folded to lower case, unless it is double-quoted, when it is kept as written, without its quotes.
    /// </summary>
    public static string FoldName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : trimmed.ToLowerInvariant();
    }

    // The words PostgreSQL 16 does not allow as a bare identifier (pg_get_keywords() with catcode <> 'U'): they must be quoted
    // where a name is written. Later versions only add to this list, and quoting a word that did not need it is harmless.
    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "authorization", "between", "bigint", "binary", "bit",
        "boolean", "both", "case", "cast", "char", "character", "check", "coalesce", "collate", "collation", "column", "concurrently",
        "constraint", "create", "cross", "current_catalog", "current_date", "current_role", "current_schema", "current_time",
        "current_timestamp", "current_user", "dec", "decimal", "default", "deferrable", "desc", "distinct", "do", "else", "end", "except",
        "exists", "extract", "false", "fetch", "float", "for", "foreign", "freeze", "from", "full", "grant", "greatest", "group", "grouping",
        "having", "ilike", "in", "initially", "inner", "inout", "int", "integer", "intersect", "interval", "into", "is", "isnull", "join",
        "json_array", "json_arrayagg", "json_object", "json_objectagg", "lateral", "leading", "least", "left", "like", "limit", "localtime",
        "localtimestamp", "national", "natural", "nchar", "none", "normalize", "not", "notnull", "null", "nullif", "numeric", "offset", "on",
        "only", "or", "order", "out", "outer", "overlaps", "overlay", "placing", "position", "precision", "primary", "real", "references",
        "returning", "right", "row", "select", "session_user", "setof", "similar", "smallint", "some", "substring", "symmetric", "system_user",
        "table", "tablesample", "then", "time", "timestamp", "to", "trailing", "treat", "trim", "true", "union", "unique", "user", "using",
        "values", "varchar", "variadic", "verbose", "when", "where", "window", "with", "xmlattributes", "xmlconcat", "xmlelement",
        "xmlexists", "xmlforest", "xmlnamespaces", "xmlparse", "xmlpi", "xmlroot", "xmlserialize", "xmltable",
    };

    /// <summary>
    /// Splits <c>"MySchema"."Mood"</c>, <c>public.t</c>, or <c>"a.b".c</c> into its identifiers as PostgreSQL reads them: a quoted part
    /// loses its quotes (and <c>""</c> becomes <c>"</c>) and keeps its case, an unquoted part is lower-cased. Dots inside quotes do not split.
    /// </summary>
    public static IReadOnlyList<string> SplitQualifiedName(string? text)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return parts;
        }

        var current = new StringBuilder();
        var quoted = false;
        var wasQuoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    quoted = true;
                    wasQuoted = true;
                    break;
                case '.':
                    Flush(parts, current, wasQuoted);
                    wasQuoted = false;
                    break;
                default:
                    if (!char.IsWhiteSpace(character))
                    {
                        current.Append(character);
                    }

                    break;
            }
        }

        Flush(parts, current, wasQuoted);
        return parts;

        static void Flush(List<string> parts, StringBuilder current, bool wasQuoted)
        {
            if (current.Length > 0 || wasQuoted)
            {
                parts.Add(wasQuoted ? current.ToString() : current.ToString().ToLowerInvariant());
            }

            current.Clear();
        }
    }

    /// <summary>True when the identifier can be written without quotes: lower case, digits, underscore, and <c>$</c>, not starting with a digit.</summary>
    public static bool IsPlain(string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || !(identifier[0] is (>= 'a' and <= 'z') or '_'))
        {
            return false;
        }

        foreach (var character in identifier)
        {
            if (!(character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '$'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Always quotes: <c>"MySchema"</c>, with an embedded <c>"</c> doubled.</summary>
    public static string Quote(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>
    /// Quotes a name the way it must be written to mean the same thing: when it has a capital or a character that is not plain, or is a
    /// word PostgreSQL does not accept as a bare name (<c>"order"</c>). Plain names stay bare, so text that has always been right does not change.
    /// </summary>
    public static string QuoteName(string identifier)
        => IsPlain(identifier) && !ReservedWords.Contains(identifier) ? identifier : Quote(identifier);

    /// <summary>Quotes a part only when its spelling requires it (a capital or an unsafe character), never for keyword status; for function and type names.</summary>
    public static string QuotePlain(string identifier)
        => IsPlain(identifier) ? identifier : Quote(identifier);

    /// <summary>
    /// Quotes the parts of a dotted name (<c>MySchema.Mood</c> becomes <c>"MySchema"."Mood"</c>) that need it. A type name may be a
    /// built-in whose spelling is a keyword (<c>numeric</c>, <c>varchar</c>), so only a capital or an unsafe character quotes a part here,
    /// never keyword status. Text that is not a plain dotted name (already quoted, a modifier, an array suffix) is returned as it is.
    /// </summary>
    public static string QuoteTypeName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return typeName;
        }

        // An array type keeps its brackets outside the name: MySchema.Mood[] is "MySchema"."Mood"[].
        var suffixStart = typeName.Length;
        while (suffixStart >= 2 && typeName[suffixStart - 2] == '[' && typeName[suffixStart - 1] == ']')
        {
            suffixStart -= 2;
        }

        var name = typeName[..suffixStart];
        if (name.Length == 0 || name.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '$' or '.')))
        {
            return typeName;
        }

        var parts = name.Split('.');
        return parts.Any(static part => part.Length == 0 || part.Any(char.IsAsciiLetterUpper))
            ? string.Join('.', parts.Select(static part => part.Length == 0 ? part : part.Any(char.IsAsciiLetterUpper) ? Quote(part) : part)) + typeName[suffixStart..]
            : typeName;
    }
}
