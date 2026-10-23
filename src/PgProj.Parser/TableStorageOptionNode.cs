namespace PgProj.Parser;

/// <summary>
/// One entry of a <c>CREATE TABLE ... WITH (...)</c> clause, exactly as written (G-24b). PostgreSQL's storage parameters are a
/// flat <c>name[=value]</c> list (<c>fillfactor=70</c>, <c>autovacuum_enabled=false</c>, a namespaced <c>toast.autovacuum_enabled=true</c>,
/// or a bare flag with no value), so the text is kept verbatim rather than re-rendered from the parsed value, the same reasoning as
/// <c>ConstraintIndexTextReader</c> (G-73): a re-serialized value can silently stop matching what the author wrote.
/// </summary>
public sealed record TableStorageOptionNode(string Name, string RawText)
{
    /// <summary>
    /// The option with the white space around its <c>=</c> removed (<c>fillfactor = 70</c> is <c>fillfactor=70</c>), which is how PostgreSQL
    /// stores and returns it (G-177). It is what two options are compared by; <see cref="RawText"/> is still what is deployed. The value is
    /// otherwise kept exactly (it can be a case-sensitive quoted string).
    /// </summary>
    public string ComparisonText
    {
        get
        {
            var separator = RawText.IndexOf('=', StringComparison.Ordinal);
            return separator < 0 ? RawText.Trim() : RawText[..separator].Trim() + "=" + RawText[(separator + 1)..].Trim();
        }
    }

    /// <summary>
    /// <see cref="ComparisonText"/> with the name in lower case and a value in single quotes unquoted (G-195): <c>pg_get_indexdef</c> and
    /// <c>pg_get_constraintdef</c> print an index's options as <c>fillfactor='70'</c>, which is the same option as <c>fillfactor = 70</c>.
    /// </summary>
    public string UnquotedComparisonText
    {
        get
        {
            var text = ComparisonText;
            var separator = text.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                return text.ToLowerInvariant();
            }

            var value = text[(separator + 1)..];
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                value = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }

            return text[..separator].ToLowerInvariant() + "=" + value;
        }
    }
}
