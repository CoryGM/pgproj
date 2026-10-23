namespace PgProj.Semantics;

/// <summary>Where the model's objects were written (G-309e), for a diagnostic that names an object by its key alone (a dependency cycle).</summary>
internal static class SemanticPlaces
{
    /// <summary>The place of the object whose key is <paramref name="key"/>, in any of the model's collections; null when none has it or it has no place.</summary>
    public static SemanticLocation? Find(DatabaseModel model, string key)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return Find(model.Tables, key)
            ?? Find(model.Views, key)
            ?? Find(model.Functions, key)
            ?? Find(model.Procedures, key)
            ?? Find(model.Types, key)
            ?? Find(model.Sequences, key)
            ?? Find(model.Indexes, key)
            ?? Find(model.Constraints, key)
            ?? Find(model.Triggers, key)
            ?? Find(model.Policies, key)
            ?? Find(model.Rules, key)
            ?? Find(model.Aggregates, key)
            ?? Find(model.Operators, key)
            ?? Find(model.Casts, key)
            ?? Find(model.EventTriggers, key)
            ?? Find(model.Collations, key)
            ?? Find(model.Statistics, key)
            ?? Find(model.TextSearchDictionaries, key)
            ?? Find(model.TextSearchConfigurations, key)
            ?? Find(model.Publications, key);
    }

    /// <summary>
    /// Where a missing type is named (G-309e): a column's type where it is written (for a table, the first of its columns that names the type), else the
    /// object that names it.
    /// </summary>
    public static SemanticLocation? OfType(ISemanticNode? owner, string reference)
        => owner switch
        {
            null => null,
            ColumnDefinition { TypeLocation: { } typed } => typed,
            TableDefinition table => table.Columns
                .FirstOrDefault(column => column.TypeLocation is not null
                    && (column.ReferencedTypes.Contains(reference, PgProj.Parser.SqlIdentifiers.NameComparer)
                        || NamesType(column.RawType, reference) || NamesType(column.DataType, reference)))?.TypeLocation
                ?? owner.Location,
            _ => owner.Location,
        };

    /// <summary>Whether a column's written type is <paramref name="reference"/> (or an array of it), qualified or not.</summary>
    private static bool NamesType(string? written, string reference)
    {
        if (string.IsNullOrWhiteSpace(written))
        {
            return false;
        }

        var type = written.Trim();
        while (type.EndsWith("[]", StringComparison.Ordinal))
        {
            type = type[..^2].TrimEnd();
        }

        return PgProj.Parser.SqlIdentifiers.NameComparer.Equals(type, reference)
            || PgProj.Parser.SqlIdentifiers.NameComparer.Equals(type, reference[(reference.LastIndexOf('.') + 1)..]);
    }

    private static SemanticLocation? Find<TDefinition>(IReadOnlyDictionary<string, TDefinition> definitions, string key)
        where TDefinition : ISemanticNode
        => definitions.TryGetValue(key, out var definition)
            ? definition.Location
            : definitions.Values.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))?.Location;
}
