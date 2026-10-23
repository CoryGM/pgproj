namespace PgProj.Semantics;

/// <summary>
/// Finds the project's aggregates that a call in an expression can mean (G-130). A call names an aggregate without its argument types, and an aggregate shares
/// PostgreSQL's function namespace, so a reference that is no project function is looked up here: the aggregates of that name in the schema it is qualified with,
/// or in the first schema of the search path that has one (else the context's own schema). Every overload of the name is returned, since the call does not say
/// which one it means.
/// </summary>
public static class AggregateReferences
{
    public static IReadOnlyList<AggregateDefinition> Find(DatabaseModel model, string? contextSchema, string? reference)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Aggregates.Count == 0 || string.IsNullOrWhiteSpace(reference))
        {
            return [];
        }

        var trimmed = reference.Trim();
        var nameEnd = trimmed.IndexOf('(', StringComparison.Ordinal);
        trimmed = nameEnd < 0 ? trimmed : trimmed[..nameEnd];
        var dot = trimmed.LastIndexOf('.');
        var schema = dot < 0 ? null : trimmed[..dot].Trim('"');
        var name = (dot < 0 ? trimmed : trimmed[(dot + 1)..]).Trim('"');

        IEnumerable<AggregateDefinition> sameName = model.Aggregates.Values.Where(aggregate => string.Equals(aggregate.Name, name, StringComparison.Ordinal));
        if (schema is not null)
        {
            return sameName.Where(aggregate => string.Equals(aggregate.SchemaName, schema, StringComparison.Ordinal)).ToArray();
        }

        var candidates = sameName.ToArray();
        foreach (var pathSchema in model.SearchPathFor(contextSchema).Append(contextSchema ?? string.Empty))
        {
            var found = candidates.Where(aggregate => string.Equals(aggregate.SchemaName, pathSchema, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length > 0)
            {
                return found;
            }
        }

        return [];
    }
}
