namespace PgProj.Engine;

/// <summary>Which schemas' tables an entity relationship diagram draws (G-158): all, only the ones listed, or all but the ones listed.</summary>
/// <param name="Schemas">The schema names given, or empty for no filter.</param>
/// <param name="Exclude">True for <c>--erd-exclude-schemas</c>: draw every table except those in <paramref name="Schemas"/>.</param>
public sealed record ErdFilter(IReadOnlyList<string> Schemas, bool Exclude)
{
    /// <summary>No filter: every table is drawn.</summary>
    public static readonly ErdFilter None = new([], false);

    /// <summary>True when no schemas were given, so every table is drawn.</summary>
    public bool IsEmpty => Schemas.Count == 0;

    /// <summary>The schema names as PostgreSQL reads them (G-206): folded to lower case unless double-quoted, as <c>pg_dump -n</c> reads them.</summary>
    public IReadOnlyList<string> Names => [.. Schemas.Select(PgProj.Parser.SqlIdentifiers.FoldName).Distinct(PgProj.Parser.SqlIdentifiers.NameComparer)];

    /// <summary>The option the names came from, for messages and for the init report.</summary>
    public string OptionName => Exclude ? "--erd-exclude-schemas" : "--erd-schemas";

    /// <summary>Whether a table in <paramref name="schemaName"/> is drawn. A name given is read as PostgreSQL reads one (<see cref="Names"/>).</summary>
    public bool Keeps(string? schemaName)
        => IsEmpty || Names.Contains(schemaName ?? string.Empty, PgProj.Parser.SqlIdentifiers.NameComparer) != Exclude;
}
