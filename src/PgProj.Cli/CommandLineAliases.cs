namespace PgProj.Cli;

/// <summary>Spellings of an option that are accepted but not documented (G-157).</summary>
public static class CommandLineAliases
{
    /// <summary>
    /// <paramref name="args"/> with <c>--excludeSchemas</c> (and <c>--excludeSchemas=value</c>) rewritten as <c>--exclude-schemas</c>. The documented spelling is the kebab-case one,
    /// like every other option; the camel-case one is accepted for people who type it.
    /// </summary>
    public static string[] Normalize(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        const string Alias = "--excludeSchemas";
        return [.. args.Select(static argument =>
            argument.StartsWith(Alias, StringComparison.Ordinal) && (argument.Length == Alias.Length || argument[Alias.Length] == '=')
                ? "--exclude-schemas" + argument[Alias.Length..]
                : argument)];
    }
}
