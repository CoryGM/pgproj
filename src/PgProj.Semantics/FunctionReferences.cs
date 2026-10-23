namespace PgProj.Semantics;

/// <summary>
/// Resolves a reference to a project function that carries no argument types, such as the <c>public.bump</c> of
/// <c>DEFAULT public.bump(1)</c> or a trigger's <c>EXECUTE FUNCTION</c> (G-54). A routine's key is overload-qualified
/// (<c>public.bump(integer)</c>, G-29), so an exact-key lookup only succeeds for a reference that spells the argument types;
/// every other reference has to be matched by schema and name. This is the one place that does it, so the binders, the
/// view pass, and the dependency analyzer agree about which function a call names.
/// </summary>
public static class FunctionReferences
{
    /// <summary>
    /// Finds the function <paramref name="reference"/> names, or null. Order of attempts: the exact key, the exact key inside
    /// <paramref name="contextSchema"/>, then a match on declared name. A schema-qualified reference matches only that
    /// schema. An unqualified one is found in the first schema of the model's search path that has it (G-93), else in
    /// <paramref name="contextSchema"/>, else in any schema. When several overloads share the name a call site cannot say which one, so the first by key is returned
    /// (deterministic); choosing by argument types would need expression type inference.
    /// </summary>
    public static FunctionDefinition? Resolve(DatabaseModel model, string? contextSchema, string? reference)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var trimmed = reference.Trim();
        if (model.TryGetFunction(trimmed, out var exact) && exact is not null)
        {
            return exact;
        }

        // An unqualified reference is found along the search path before the context's own schema, as PostgreSQL finds it (G-93).
        var nameEnd = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (!(nameEnd < 0 ? trimmed : trimmed[..nameEnd]).Contains('.', StringComparison.Ordinal))
        {
            foreach (var pathSchema in model.SearchPathFor(contextSchema))
            {
                if (model.TryGetFunction(DatabaseModel.CreateKey(pathSchema, trimmed), out var onPath) && onPath is not null)
                {
                    return onPath;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(contextSchema)
            && model.TryGetFunction(DatabaseModel.CreateKey(contextSchema, trimmed), out var scoped)
            && scoped is not null)
        {
            return scoped;
        }

        var (schema, name) = Split(trimmed);
        if (name.Length == 0)
        {
            return null;
        }

        var sameName = model.Functions.Values
            .Where(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
            .OrderBy(static candidate => candidate.Key, StringComparer.Ordinal)
            .ToArray();

        if (schema is not null)
        {
            return sameName.FirstOrDefault(candidate => string.Equals(candidate.SchemaName, schema, StringComparison.Ordinal));
        }

        // PostgreSQL finds an unqualified name in the first schema of its search path that has it (G-93); after that, the context's own schema
        // and then any schema, so a reference SearchPathValidationPass reports as off the path still gets its dependency edge.
        return model.SearchPathFor(contextSchema)
                .Select(pathSchema => sameName.FirstOrDefault(candidate => string.Equals(candidate.SchemaName, pathSchema, StringComparison.Ordinal)))
                .FirstOrDefault(static candidate => candidate is not null)
            ?? sameName.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(contextSchema)
                && string.Equals(candidate.SchemaName, contextSchema, StringComparison.Ordinal))
            ?? sameName.FirstOrDefault();
    }

    /// <summary>
    /// Finds the project procedure a <c>CALL</c> names (G-175), by the same rules as <see cref="Resolve"/>: an exact key, a
    /// schema-qualified name in that schema only, an unqualified one along the search path, then in <paramref name="contextSchema"/>,
    /// then in any schema. Among overloads the first by key is returned, since a call site does not say which one it means.
    /// </summary>
    public static ProcedureDefinition? ResolveProcedure(DatabaseModel model, string? contextSchema, string? reference)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var trimmed = reference.Trim();
        if (model.TryGetProcedure(trimmed, out var exact) && exact is not null)
        {
            return exact;
        }

        var (schema, name) = Split(trimmed);
        if (name.Length == 0)
        {
            return null;
        }

        var sameName = model.Procedures.Values
            .Where(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
            .OrderBy(static candidate => candidate.Key, StringComparer.Ordinal)
            .ToArray();

        if (schema is not null)
        {
            return sameName.FirstOrDefault(candidate => string.Equals(candidate.SchemaName, schema, StringComparison.Ordinal));
        }

        return model.SearchPathFor(contextSchema)
                .Select(pathSchema => sameName.FirstOrDefault(candidate => string.Equals(candidate.SchemaName, pathSchema, StringComparison.Ordinal)))
                .FirstOrDefault(static candidate => candidate is not null)
            ?? sameName.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(contextSchema)
                && string.Equals(candidate.SchemaName, contextSchema, StringComparison.Ordinal))
            ?? sameName.FirstOrDefault();
    }

    /// <summary>Splits <c>schema.name</c> (each part optionally double-quoted) into its parts; the schema is null when absent.</summary>
    private static (string? Schema, string Name) Split(string reference)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var character in reference)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (character == '.' && !inQuotes)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        parts.Add(current.ToString().Trim());

        // database.schema.name keeps only schema.name.
        var name = parts[^1];
        var schema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : null;
        return (schema, name);
    }
}
