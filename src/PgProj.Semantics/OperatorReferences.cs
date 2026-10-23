namespace PgProj.Semantics;

/// <summary>
/// Which of the project's operators (G-129) an expression uses (G-138). A reader finds an operator by name (<c>===</c>, <c>OPERATOR(app.===)</c>): the
/// operand types are not known without typing the expression, so a name stands for every operator of that name the project defines in a schema the name can
/// reach (the one it is qualified with, or a schema on the search path). An operator the project does not define (every built-in) is not a reference.
/// </summary>
internal static class OperatorReferences
{
    public static IReadOnlyList<string> Resolve(DatabaseModel model, IEnumerable<string> written, string? contextSchema = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(written);

        if (model.Operators.Count == 0)
        {
            return [];
        }

        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in written)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var (schema, operatorName) = OperatorNames.Split(name.Trim());
            foreach (var candidate in model.Operators.Values)
            {
                if (string.Equals(candidate.OperatorName, operatorName, StringComparison.Ordinal)
                    && (schema is null
                        ? model.SearchPathFor(contextSchema).Contains(candidate.SchemaName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        : string.Equals(candidate.SchemaName, schema, StringComparison.Ordinal)))
                {
                    keys.Add(candidate.Key);
                }
            }
        }

        return keys.ToArray();
    }

    /// <summary>The keys already on an object together with newly found ones, without repeats.</summary>
    public static IReadOnlyList<string> Merge(IReadOnlyList<string> existing, IEnumerable<string> found)
        => existing.Concat(found).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
}
