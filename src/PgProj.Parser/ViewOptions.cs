namespace PgProj.Parser;

/// <summary>
/// The options of a view (G-180): <c>security_barrier</c>, <c>security_invoker</c>, and <c>check_option</c> (what <c>WITH [LOCAL | CASCADED]
/// CHECK OPTION</c> is stored as). PostgreSQL keeps them in <c>pg_class.reloptions</c> as <c>name=value</c>, and writes a boolean as the author
/// wrote it (<c>on</c>, <c>true</c>, <c>1</c>), so a project and a live database are compared by the canonical form made here: the name in
/// lower case, a boolean as <c>true</c>, and an option that is off left out, since an absent option and an explicit <c>false</c> are the same view.
/// </summary>
public static class ViewOptions
{
    public const string CheckOption = "check_option";

    /// <summary>The canonical <c>name=value</c> text of one option, or null when the option is off (and so the same as not being given).</summary>
    public static string? Canonicalize(string name, string? value)
    {
        var canonicalName = name.Trim().ToLowerInvariant();
        var canonicalValue = value?.Trim();

        if (canonicalName is "security_barrier" or "security_invoker")
        {
            return ParseBoolean(canonicalValue) ? canonicalName + "=true" : null;
        }

        if (canonicalName == CheckOption)
        {
            return string.IsNullOrEmpty(canonicalValue) ? null : canonicalName + "=" + canonicalValue.ToLowerInvariant();
        }

        // Not an option PostgreSQL knows for a view; kept, so the diff reports it rather than hiding it, and PostgreSQL refuses it on deploy.
        return canonicalValue is null ? canonicalName : canonicalName + "=" + canonicalValue;
    }

    /// <summary>The canonical options, sorted by name (they are a set; PostgreSQL's own order is the order they were given).</summary>
    public static IReadOnlyList<string> Canonicalize(IEnumerable<(string Name, string? Value)> options)
        => options
            .Select(static option => Canonicalize(option.Name, option.Value))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static option => option, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Reads a stored <c>name=value</c> (or a bare <c>name</c>) entry of <c>reloptions</c>.</summary>
    public static (string Name, string? Value) Split(string entry)
    {
        var separator = entry.IndexOf('=', StringComparison.Ordinal);
        return separator < 0 ? (entry, null) : (entry[..separator], entry[(separator + 1)..]);
    }

    /// <summary>The options a <c>WITH (...)</c> header states, which is every one but the check option (that is written <c>WITH CHECK OPTION</c> after the query).</summary>
    public static IEnumerable<string> HeaderOptions(IEnumerable<string> options)
        => options.Where(static option => !option.StartsWith(CheckOption + "=", StringComparison.Ordinal));

    /// <summary>The <c>LOCAL</c> or <c>CASCADED</c> check option among <paramref name="options"/>, or null.</summary>
    public static string? CheckOptionOf(IEnumerable<string> options)
        => options.FirstOrDefault(static option => option.StartsWith(CheckOption + "=", StringComparison.Ordinal)) is { } found
            ? found[(CheckOption.Length + 1)..].ToUpperInvariant()
            : null;

    private static bool ParseBoolean(string? value)
        => value is null || value.ToLowerInvariant() is "true" or "t" or "on" or "yes" or "y" or "1";
}
