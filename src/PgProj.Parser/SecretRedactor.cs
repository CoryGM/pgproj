using System.Text.RegularExpressions;

namespace PgProj.Parser;

/// <summary>
/// Masks a password or other secret in SQL text that a diagnostic quotes (G-142, G-141). A warning or an error reaches terminals and CI
/// logs, and the SQL it quotes can hold a credential: a <c>CREATE SUBSCRIPTION ... CONNECTION 'host=h password=secret'</c>, a
/// <c>CREATE USER MAPPING ... OPTIONS (password 'secret')</c>, a role's <c>PASSWORD 'secret'</c>, a connection URL
/// <c>postgresql://user:secret@host</c>. The value is replaced by <see cref="Mask"/>.
/// </summary>
/// <remarks>
/// This is pattern matching on the common shapes, by the user's decision (2026-09-30), not a proof that no secret remains: a
/// secret with no such name beside it (a value in an <c>INSERT</c>, a function body, a comment) is not recognized.
/// </remarks>
internal static class SecretRedactor
{
    /// <summary>What replaces a secret: five asterisks, whatever the secret's length.</summary>
    public const string Mask = "*****";

    private const string Keys = "(?:password|passwd|pwd|sslpassword|secret|secret_?key|access_?key|api_?key|auth_?token|token)";

    /// <summary><c>password=value</c> (a connection string, an option): the value runs to whitespace or a closing quote.</summary>
    private static readonly Regex KeyEqualsValue = new(
        $@"(\b{Keys}\s*=\s*)(?:''(?:[^']|'(?!'))*''|""[^""]*""|[^\s'"";,)]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary><c>PASSWORD 'value'</c>, <c>password = 'value'</c>, <c>OPTIONS (password 'value')</c>: the literal keeps its quotes.</summary>
    private static readonly Regex KeyThenLiteral = new(
        $@"(\b{Keys}\b(?:\s*=\s*|\s+))'(?:[^']|'')*'",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The password in <c>scheme://user:password@host</c>.</summary>
    private static readonly Regex UrlPassword = new(
        @"(\b[a-z][a-z0-9+.\-]*://[^\s:/@'""]+:)[^\s@/'""]+(@)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The quoted token of libpg_query's <c>syntax error at or near "token"</c>.</summary>
    private static readonly Regex NearToken = new(
        @"(at or near "")(.*)("")",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>A secret's name, then the end of the text before the cursor: <c>... PASSWORD </c> or <c>... password = </c>.</summary>
    private static readonly Regex KeyBeforeCursor = new(
        $@"\b{Keys}\s*=?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary><paramref name="text"/> with the secrets it names masked.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var redacted = KeyEqualsValue.Replace(text, $"$1{Mask}");
        redacted = KeyThenLiteral.Replace(redacted, $"$1'{Mask}'");
        return UrlPassword.Replace(redacted, $"$1{Mask}$2");
    }

    /// <summary>
    /// A libpg_query error message with its secrets masked (G-141). Besides <see cref="Redact"/>, the token a syntax error stopped at
    /// is masked when the text just before it names a secret (<c>PASSWORD hunter2</c> stops at <c>hunter2</c>); that needs the
    /// 1-based code point <paramref name="cursorPosition"/> of the error in <paramref name="sql"/>.
    /// </summary>
    public static string RedactErrorMessage(string message, string sql, int? cursorPosition)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(sql);
        var redacted = NearToken.Replace(
            message,
            match => match.Groups[1].Value
                + (IsWordOrLiteral(match.Groups[2].Value) && FollowsSecretName(sql, cursorPosition) ? MaskToken(match.Groups[2].Value) : Redact(match.Groups[2].Value))
                + match.Groups[3].Value);
        return Redact(redacted);
    }

    /// <summary>A name, a number, or a quoted text, as against the punctuation (<c>=</c>, <c>;</c>) that holds nothing secret.</summary>
    private static bool IsWordOrLiteral(string token)
        => token.Length > 0 && (char.IsLetterOrDigit(token[0]) || token[0] is '\x27' or '"' or '_');

    private static string MaskToken(string token) => token.StartsWith('\'') ? $"'{Mask}'" : Mask;

    private static bool FollowsSecretName(string sql, int? cursorPosition)
    {
        if (cursorPosition is not { } cursor || cursor < 1)
        {
            return false;
        }

        var index = 0;
        var position = 1;
        foreach (var rune in sql.EnumerateRunes())
        {
            if (position == cursor)
            {
                break;
            }

            index += rune.Utf16SequenceLength;
            position++;
        }

        return KeyBeforeCursor.IsMatch(sql.AsSpan(0, index));
    }
}
