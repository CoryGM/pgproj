namespace PgProj.Diff;

/// <summary>
/// How an operator class name is compared (G-278). The live read prints an index or partition key with <c>search_path</c> pinned to <c>public</c>, so
/// PostgreSQL leaves out the schema of a class in <c>pg_catalog</c> or <c>public</c> (pgvector's <c>vector_cosine_ops</c>) and writes any other.
/// A project that writes <c>public.vector_cosine_ops</c>, as <c>pg_dump</c> does, names the same class; it is compared without that schema, and
/// deployed as written. A class in any other schema keeps its schema, so the same name in two schemas is still two classes.
/// </summary>
public static class OperatorClassNames
{
    /// <summary>The name without a <c>pg_catalog.</c> or <c>public.</c> schema (quoted or not); empty for none.</summary>
    public static string Comparison(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();
        foreach (var schema in new[] { "pg_catalog.", "\"pg_catalog\".", "public.", "\"public\"." })
        {
            if (trimmed.StartsWith(schema, StringComparison.Ordinal))
            {
                return trimmed[schema.Length..];
            }
        }

        return trimmed;
    }

    /// <summary>Whether two operator class names, either of which may be missing, name the same class.</summary>
    public static bool Same(string? first, string? second)
        => string.Equals(Comparison(first), Comparison(second), StringComparison.Ordinal);
}
