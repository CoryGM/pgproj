namespace PgProj.Semantics;

/// <summary>
/// A multirange type is created with its range type and is no type of the model's own (G-204): a column, routine, or other type that names one
/// uses the range that makes it. The multirange is in the range's schema unless its <c>MULTIRANGE_TYPE_NAME</c> names another, and has
/// PostgreSQL's default name (<see cref="RangeTypeOptionsDefinition.DefaultMultirangeName"/>) unless that option gives one.
/// </summary>
public static class MultirangeTypes
{
    /// <summary>
    /// The project's range type whose multirange <paramref name="reference"/> names, looked for in <paramref name="schemaName"/> (or
    /// <c>public</c>) when the reference has no schema; null when it names none.
    /// </summary>
    public static TypeDefinition? FindRange(DatabaseModel model, string? schemaName, string? reference)
    {
        ArgumentNullException.ThrowIfNull(model);

        // A reference is the model's unquoted dotted form (s.many spans): split on the dot alone, since the identifier parser drops spaces.
        var referenceParts = (reference ?? string.Empty).Split('.', StringSplitOptions.TrimEntries);
        if (referenceParts[^1].Length == 0)
        {
            return null;
        }

        var referenceSchema = referenceParts.Length > 1 ? referenceParts[^2] : schemaName ?? "public";
        foreach (var type in model.Types.Values)
        {
            if (type.RangeOptions is not { } range)
            {
                continue;
            }

            var parts = PgProj.Parser.SqlIdentifiers.SplitQualifiedName(range.MultirangeTypeName);
            var name = parts.Count > 0 ? parts[^1] : RangeTypeOptionsDefinition.DefaultMultirangeName(type.Name ?? string.Empty);
            var schema = parts.Count > 1 ? parts[^2] : type.SchemaName ?? "public";
            if (string.Equals(referenceParts[^1], name, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(referenceSchema, schema, StringComparison.OrdinalIgnoreCase)
                    || (referenceParts.Length == 1 && string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase))))
            {
                return type;
            }
        }

        return null;
    }
}
