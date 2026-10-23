using System.Globalization;
using System.Text;

namespace PgProj.Semantics;

/// <summary>
/// The comparison form of a column <c>DEFAULT</c> expression (G-56, G-55). The raw text is kept as written for
/// deployment; this form is only for deciding whether two defaults are the same.
/// </summary>
/// <remarks>
/// A project writes <c>DEFAULT 'pending'</c> and PostgreSQL reports it back (<c>pg_get_expr</c>) as
/// <c>'pending'::text</c>; <c>DEFAULT -1</c> comes back as <c>'-1'::integer</c>; <c>nextval('s')</c> as
/// <c>nextval('s'::regclass)</c>. Without folding those, every defaulted column would re-diff forever. The folds are
/// deliberately narrow so a real change is never hidden: whitespace, comments, and the case of keywords and unquoted
/// names are folded; string literals and quoted identifiers are never touched; redundant outer parentheses are
/// dropped; and a cast is dropped only when it names the column's own type on a string literal (or is a
/// <c>regclass</c> cast of a string literal, which PostgreSQL always adds to <c>nextval</c>'s argument).
/// Known limit: PostgreSQL rewrites some literals through the type's output function (a
/// <c>timestamptz</c> literal is re-rendered with an explicit offset) and schema-qualifies names that are not on
/// the search path; those still compare as different here. Since G-102, <c>diff</c>/<c>publish</c> compare
/// PostgreSQL's own rendering of both sides when they can (<see cref="ColumnDefinition.ServerRenderedDefault"/>), and
/// this form is the fallback.
/// </remarks>
internal static class DefaultExpressionCanonicalizer
{
    // The built-in aliases (G-207: one table), and the bare char that SQL text means as bpchar, which a parse-tree name never is.
    private static readonly Dictionary<string, string> TypeAliases = new(TypeNameAliases.All, StringComparer.Ordinal)
    {
        ["char"] = "bpchar",
    };

    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal)
    {
        "int2", "int4", "int8", "float4", "float8", "numeric",
    };

    private enum Kind
    {
        Word,
        QuotedIdentifier,
        StringLiteral,
        Number,
        Operator,
        Punctuation,
    }

    private readonly record struct Token(Kind Kind, string Text);

    /// <summary>Returns the comparison form of <paramref name="rawDefault"/>, or null when there is no default.</summary>
    public static string? Canonicalize(string? rawDefault, string? columnDataType, int columnArrayDimensions)
    {
        if (string.IsNullOrWhiteSpace(rawDefault))
        {
            return null;
        }

        var tokens = Tokenize(rawDefault);

        // PostgreSQL stores no default for a NULL constant (DEFAULT NULL, DEFAULT NULL::integer), because it is what
        // a column without a default already has, so the live column reads back as having none.
        if (tokens.Count > 0
            && tokens[0] is { Kind: Kind.Word, Text: "null" }
            && (tokens.Count == 1
                || (tokens[1] is { Kind: Kind.Operator, Text: "::" } && ReadCastType(tokens, 2) is not null)))
        {
            return null;
        }

        tokens = MergeUnarySigns(tokens);
        tokens = DropSearchPathSchemaQualifiers(tokens);
        tokens = StripOuterParentheses(tokens);
        tokens = DropRegclassCastsOnLiterals(tokens);
        tokens = DropDefaultSchemaFromSequenceNames(tokens);

        // public is on the search path, so PostgreSQL prints a type in it without the schema (G-70).
        var columnTypeName = ColumnTypeNormalizer.NormalizeBaseType(columnDataType);
        var columnType = TypeKey(columnTypeName.StartsWith("public.", StringComparison.OrdinalIgnoreCase) ? columnTypeName["public.".Length..] : columnTypeName, columnArrayDimensions > 0);
        tokens = FoldTypedLiteral(tokens);
        tokens = DropOwnTypeCastOnLiteral(tokens, columnType);

        return Render(tokens);
    }

    /// <summary>
    /// PostgreSQL prints a name without its schema when the schema is on the search path, which is true of <c>public</c> and
    /// <c>pg_catalog</c> in a default configuration: <c>DEFAULT public.utc_now()</c> reads back as <c>utc_now()</c>, and
    /// <c>pg_catalog.now()</c> as <c>now()</c> (G-63). Only these two are dropped, so a name in any other schema stays
    /// significant. Like the sequence-name rule, this assumes the default search path; a schema qualifier inside a string literal is
    /// never touched.
    /// </summary>
    private static List<Token> DropSearchPathSchemaQualifiers(List<Token> tokens)
    {
        var result = new List<Token>(tokens.Count);
        for (var index = 0; index < tokens.Count; index++)
        {
            var isQualifier = index + 2 < tokens.Count
                && IsSearchPathSchema(tokens[index])
                && tokens[index + 1] is { Kind: Kind.Punctuation, Text: "." }
                && tokens[index + 2].Kind is Kind.Word or Kind.QuotedIdentifier
                && !(result.Count > 0 && result[^1] is { Kind: Kind.Punctuation, Text: "." });
            if (isQualifier)
            {
                index++;
                continue;
            }

            result.Add(tokens[index]);
        }

        return result;
    }

    private static bool IsSearchPathSchema(Token token)
        => token.Kind switch
        {
            Kind.Word => token.Text is "public" or "pg_catalog",
            Kind.QuotedIdentifier => token.Text is "\"public\"" or "\"pg_catalog\"",
            _ => false,
        };

    private static List<Token> StripOuterParentheses(List<Token> tokens)
    {
        while (tokens.Count > 2
            && tokens[0] is { Kind: Kind.Punctuation, Text: "(" }
            && tokens[^1] is { Kind: Kind.Punctuation, Text: ")" }
            && ClosingIndexOf(tokens, 0) == tokens.Count - 1)
        {
            tokens = tokens.GetRange(1, tokens.Count - 2);
        }

        return tokens;
    }

    private static int ClosingIndexOf(List<Token> tokens, int openIndex)
    {
        var depth = 0;
        for (var index = openIndex; index < tokens.Count; index++)
        {
            if (tokens[index].Kind != Kind.Punctuation)
            {
                continue;
            }

            if (tokens[index].Text is "(" or "[")
            {
                depth++;
            }
            else if (tokens[index].Text is ")" or "]" && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>A leading <c>-</c> before a number is the number's sign: <c>- 1</c> and <c>-1</c> are the same default.</summary>
    private static List<Token> MergeUnarySigns(List<Token> tokens)
    {
        var merged = new List<Token>(tokens.Count);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var isSign = token is { Kind: Kind.Operator, Text: "-" }
                && index + 1 < tokens.Count
                && tokens[index + 1].Kind == Kind.Number
                && (merged.Count == 0
                    || merged[^1].Kind == Kind.Operator
                    || merged[^1] is { Kind: Kind.Punctuation, Text: "(" or "," or "[" });
            if (isSign)
            {
                merged.Add(new Token(Kind.Number, "-" + tokens[index + 1].Text));
                index++;
                continue;
            }

            merged.Add(token);
        }

        return merged;
    }

    /// <summary>
    /// <c>nextval('s')</c> is reported as <c>nextval('s'::regclass)</c>, and <c>date_trunc('day', now())</c> as
    /// <c>date_trunc('day'::text, now())</c>. A string literal has no type of its own until it is resolved, and it
    /// resolves to <c>text</c> (or is passed to a <c>regclass</c> parameter) either way, so these casts never change
    /// the meaning. Any other cast of a literal is kept: <c>'1'::bigint</c> is not <c>'1'</c>.
    /// </summary>
    private static List<Token> DropRegclassCastsOnLiterals(List<Token> tokens)
    {
        var result = new List<Token>(tokens.Count);
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == Kind.StringLiteral
                && index + 2 < tokens.Count
                && tokens[index + 1] is { Kind: Kind.Operator, Text: "::" }
                && tokens[index + 2] is { Kind: Kind.Word, Text: "regclass" or "text" }
                && !IsArrayOrQualifiedCast(tokens, index + 2))
            {
                result.Add(tokens[index]);
                index += 2;
                continue;
            }

            result.Add(tokens[index]);
        }

        return result;
    }

    /// <summary>
    /// PostgreSQL prints a sequence name without its schema when the schema is on the search path, which is true of
    /// <c>public</c> in a default configuration, so <c>nextval('public.s')</c> and <c>nextval('s')</c> read back the same.
    /// Only the first argument of <c>nextval</c>/<c>currval</c>/<c>setval</c> is a sequence name.
    /// </summary>
    private static List<Token> DropDefaultSchemaFromSequenceNames(List<Token> tokens)
    {
        var result = new List<Token>(tokens);
        for (var index = 0; index + 2 < result.Count; index++)
        {
            if (result[index].Kind == Kind.Word
                && result[index].Text is "nextval" or "currval" or "setval"
                && result[index + 1] is { Kind: Kind.Punctuation, Text: "(" }
                && result[index + 2].Kind == Kind.StringLiteral
                && TryStripPublicQualifier(result[index + 2].Text, out var stripped))
            {
                result[index + 2] = new Token(Kind.StringLiteral, stripped);
            }
        }

        return result;
    }

    private static bool TryStripPublicQualifier(string literal, out string stripped)
    {
        stripped = literal;
        if (literal.Length < 3 || literal[0] != '\'' || literal[^1] != '\'')
        {
            return false;
        }

        var inner = literal[1..^1];
        foreach (var prefix in new[] { "public.", "\"public\"." })
        {
            if (inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                stripped = "'" + inner[prefix.Length..] + "'";
                return true;
            }
        }

        return false;
    }

    /// <summary><c>'x'::text[]</c> and <c>'x'::text.y</c> are different casts from <c>'x'::text</c>.</summary>
    private static bool IsArrayOrQualifiedCast(List<Token> tokens, int typeIndex)
        => typeIndex + 1 < tokens.Count
            && tokens[typeIndex + 1] is { Kind: Kind.Punctuation, Text: "[" or "." };

    /// <summary>
    /// A default that is only a typed literal, <c>interval '1 day'</c>, is the cast PostgreSQL prints it as, <c>'1 day'::interval</c>
    /// (G-207: a routine parameter's default is now compared as written, where it used to be rebuilt in the cast form).
    /// </summary>
    private static List<Token> FoldTypedLiteral(List<Token> tokens)
    {
        if (tokens.Count < 2
            || tokens[^1].Kind != Kind.StringLiteral
            || tokens.Take(tokens.Count - 1).Any(static token => token.Kind != Kind.Word))
        {
            return tokens;
        }

        return [tokens[^1], new Token(Kind.Operator, "::"), .. tokens.Take(tokens.Count - 1)];
    }

    /// <summary>
    /// Drops <c>::type</c> from a default that is only a string literal cast to the column's own type
    /// (<c>'x'::text</c> on a text column), and unquotes a number that PostgreSQL rendered as a quoted string
    /// (<c>'-1'::integer</c> on an integer column).
    /// </summary>
    private static List<Token> DropOwnTypeCastOnLiteral(List<Token> tokens, string columnType)
    {
        if (tokens.Count < 3
            || tokens[0].Kind != Kind.StringLiteral
            || tokens[1] is not { Kind: Kind.Operator, Text: "::" })
        {
            return tokens;
        }

        var castType = ReadCastType(tokens, 2);
        if (castType is null || !string.Equals(castType, columnType, StringComparison.Ordinal))
        {
            return tokens;
        }

        var literal = tokens[0];
        var baseType = columnType.EndsWith("[]", StringComparison.Ordinal) ? string.Empty : columnType;
        if (NumericTypes.Contains(baseType) && TryUnquoteNumber(literal.Text, out var number))
        {
            return [new Token(Kind.Number, number)];
        }

        return [literal];
    }

    private static bool TryUnquoteNumber(string quotedLiteral, out string number)
    {
        number = string.Empty;
        if (quotedLiteral.Length < 3 || quotedLiteral[0] != '\'' || quotedLiteral[^1] != '\'')
        {
            return false;
        }

        var inner = quotedLiteral[1..^1];
        if (inner.Contains('\'', StringComparison.Ordinal)
            || !decimal.TryParse(inner, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        number = inner;
        return true;
    }

    /// <summary>The canonical type key of a cast (words up to any modifiers, plus <c>[]</c>), or null if it is not a plain type.</summary>
    private static string? ReadCastType(List<Token> tokens, int start)
    {
        var words = new List<string>();
        var index = start;

        // A schema-qualified name: pg_catalog.int4, public.mood, "MySchema"."Mood" (G-70). PostgreSQL prints a name on the search path
        // without its schema, so public and pg_catalog are not part of the name; any other schema is.
        var qualifiers = new List<string>();
        while (index + 2 < tokens.Count
            && tokens[index].Kind is Kind.Word or Kind.QuotedIdentifier
            && tokens[index + 1] is { Kind: Kind.Punctuation, Text: "." }
            && tokens[index + 2].Kind is Kind.Word or Kind.QuotedIdentifier)
        {
            var qualifier = NamePart(tokens[index]);
            if (qualifier is not ("pg_catalog" or "public"))
            {
                qualifiers.Add(qualifier);
            }

            index += 2;
        }

        if (index < tokens.Count && tokens[index].Kind == Kind.QuotedIdentifier)
        {
            words.Add(NamePart(tokens[index]));
            index++;
        }
        else
        {
            while (index < tokens.Count && tokens[index].Kind == Kind.Word)
            {
                words.Add(tokens[index].Text);
                index++;
            }
        }

        if (words.Count == 0)
        {
            return null;
        }

        if (index < tokens.Count && tokens[index] is { Kind: Kind.Punctuation, Text: "(" })
        {
            var close = ClosingIndexOf(tokens, index);
            if (close < 0)
            {
                return null;
            }

            index = close + 1;
        }

        var isArray = false;
        if (index + 1 < tokens.Count
            && tokens[index] is { Kind: Kind.Punctuation, Text: "[" }
            && tokens[index + 1] is { Kind: Kind.Punctuation, Text: "]" })
        {
            isArray = true;
            index += 2;
        }

        var typeName = string.Join(' ', words);
        return index == tokens.Count ? TypeKey(qualifiers.Count == 0 ? typeName : string.Join('.', qualifiers) + "." + typeName, isArray) : null;
    }

    /// <summary>The identifier a word or quoted-identifier token names, without its quotes.</summary>
    private static string NamePart(Token token)
        => token.Kind == Kind.QuotedIdentifier && token.Text.Length >= 2
            ? token.Text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : token.Text;

    private static string TypeKey(string typeName, bool isArray)
    {
        var name = typeName.Trim().ToLowerInvariant();
        if (name.EndsWith("[]", StringComparison.Ordinal))
        {
            name = name[..^2];
            isArray = true;
        }

        var baseName = TypeAliases.TryGetValue(name, out var alias) ? alias : name;
        return isArray ? baseName + "[]" : baseName;
    }

    private static string Render(List<Token> tokens)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (index > 0 && NeedsSpace(tokens[index - 1], tokens[index]))
            {
                builder.Append(' ');
            }

            builder.Append(tokens[index].Text);
        }

        return builder.ToString();
    }

    private static bool NeedsSpace(Token previous, Token next)
    {
        if (previous is { Kind: Kind.Punctuation, Text: "(" or "[" or "." }
            || previous is { Kind: Kind.Operator, Text: "::" })
        {
            return false;
        }

        if (next is { Kind: Kind.Punctuation, Text: ")" or "]" or "," or "." }
            || next is { Kind: Kind.Operator, Text: "::" })
        {
            return false;
        }

        // f (x) and f(x) are the same call; so are a [1] and a[1].
        if (next is { Kind: Kind.Punctuation, Text: "(" or "[" } && previous.Kind is Kind.Word or Kind.QuotedIdentifier)
        {
            return false;
        }

        if (previous is { Kind: Kind.Punctuation, Text: "]" } && next is { Kind: Kind.Punctuation, Text: "[" })
        {
            return false;
        }

        return true;
    }

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < sql.Length)
        {
            var current = sql[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                while (index < sql.Length && sql[index] is not '\r' and not '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index = SkipBlockComment(sql, index);
                continue;
            }

            if (current == '\'')
            {
                var end = SkipQuoted(sql, index, '\'', backslashEscapes: false);
                tokens.Add(new Token(Kind.StringLiteral, sql[index..end]));
                index = end;
                continue;
            }

            if (current == '"')
            {
                var end = SkipQuoted(sql, index, '"', backslashEscapes: false);
                tokens.Add(new Token(Kind.QuotedIdentifier, sql[index..end]));
                index = end;
                continue;
            }

            if (current == '$' && TryReadDollarQuoted(sql, index, out var dollarEnd))
            {
                tokens.Add(new Token(Kind.StringLiteral, sql[index..dollarEnd]));
                index = dollarEnd;
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
                {
                    index++;
                }

                if (index - start == 1 && index < sql.Length && sql[index] == '\'' && "eEbBxXnN".Contains(sql[start], StringComparison.Ordinal))
                {
                    var end = SkipQuoted(sql, index, '\'', backslashEscapes: sql[start] is 'e' or 'E');
                    tokens.Add(new Token(Kind.StringLiteral, sql[start..end]));
                    index = end;
                    continue;
                }

                tokens.Add(new Token(Kind.Word, sql[start..index].ToLowerInvariant()));
                continue;
            }

            if (char.IsDigit(current) || (current == '.' && index + 1 < sql.Length && char.IsDigit(sql[index + 1])))
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '.' or '_'))
                {
                    index++;
                }

                tokens.Add(new Token(Kind.Number, sql[start..index].ToLowerInvariant()));
                continue;
            }

            if (current == ':' && index + 1 < sql.Length && sql[index + 1] == ':')
            {
                tokens.Add(new Token(Kind.Operator, "::"));
                index += 2;
                continue;
            }

            if (current is '(' or ')' or '[' or ']' or ',' or '.' or ';')
            {
                tokens.Add(new Token(Kind.Punctuation, current.ToString()));
                index++;
                continue;
            }

            var operatorStart = index++;
            while (index < sql.Length && "+-*/<>=~!@#%^&|`?".Contains(sql[index], StringComparison.Ordinal)
                && !(sql[index] == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
                && !(sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*'))
            {
                index++;
            }

            tokens.Add(new Token(Kind.Operator, sql[operatorStart..index]));
        }

        return tokens;
    }

    private static int SkipBlockComment(string sql, int index)
    {
        index += 2;
        var depth = 1;
        while (index < sql.Length && depth > 0)
        {
            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                depth++;
                index += 2;
            }
            else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
            {
                depth--;
                index += 2;
            }
            else
            {
                index++;
            }
        }

        return index;
    }

    private static int SkipQuoted(string sql, int index, char quote, bool backslashEscapes)
    {
        index++;
        while (index < sql.Length)
        {
            if (backslashEscapes && sql[index] == '\\')
            {
                index += 2;
                continue;
            }

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

            return index;
        }

        return sql.Length;
    }

    private static bool TryReadDollarQuoted(string sql, int index, out int end)
    {
        end = index;
        var tagEnd = index + 1;
        while (tagEnd < sql.Length && (char.IsLetterOrDigit(sql[tagEnd]) || sql[tagEnd] == '_'))
        {
            tagEnd++;
        }

        if (tagEnd >= sql.Length || sql[tagEnd] != '$' || (tagEnd > index + 1 && char.IsDigit(sql[index + 1])))
        {
            return false;
        }

        var delimiter = sql[index..(tagEnd + 1)];
        var close = sql.IndexOf(delimiter, tagEnd + 1, StringComparison.Ordinal);
        end = close < 0 ? sql.Length : close + delimiter.Length;
        return true;
    }
}
