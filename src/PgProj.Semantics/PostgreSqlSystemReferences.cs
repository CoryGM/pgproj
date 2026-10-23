namespace PgProj.Semantics;

internal static class PostgreSqlSystemReferences
{
    private static readonly HashSet<string> BuiltInSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema",
        "pg_catalog",
        "pg_toast",
        "pg_temp",
        "pg_toast_temp",
    };

    public static bool IsBuiltInRelation(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var parts = reference.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            return IsBuiltInSchema(parts[0]);
        }

        var normalized = reference.Trim().Trim('"');
        return normalized.StartsWith("pg_", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBuiltInSchema(string? schemaName)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            return false;
        }

        var normalized = schemaName.Trim().Trim('"');
        return BuiltInSchemas.Contains(normalized)
            || normalized.StartsWith("pg_temp_", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("pg_toast_temp_", StringComparison.OrdinalIgnoreCase);
    }
}
