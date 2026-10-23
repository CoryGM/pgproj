using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>
/// The schemas the live database has objects in that the project no longer has anything in (G-166). pgproj never drops a schema:
/// <c>diff</c> reports nothing for one and <c>publish</c> emits only <c>CREATE SCHEMA IF NOT EXISTS</c> (the user's decision, 2026-10-02:
/// "add a warning that schema are not being dropped but don't change the behaviour"). The objects inside such a schema are each
/// reported and, with <c>--allow-destructive</c>, dropped; the schema itself is left behind, so <c>diff</c> and <c>publish</c> name it.
/// </summary>
public static class UndroppedSchemas
{
    /// <summary>
    /// The schemas some node of <paramref name="liveModel"/> is in that no node of <paramref name="sourceModel"/> is in, in name order, less
    /// <c>public</c> and the system schemas, which are never the project's to drop. A schema with nothing pgproj models in it, and no comment,
    /// is not seen at all and so is not named.
    /// </summary>
    public static IReadOnlyList<string> Find(DatabaseModel liveModel, DatabaseModel sourceModel)
    {
        ArgumentNullException.ThrowIfNull(liveModel);
        ArgumentNullException.ThrowIfNull(sourceModel);

        // The live read declares every schema it reads (G-284), so there only a schema with something in it, or a comment, is named, as before; a schema the project declares is kept.
        var kept = SchemaNames(sourceModel, countDeclared: true);
        return SchemaNames(liveModel, countDeclared: false)
            .Where(schema => !kept.Contains(schema))
            .Where(static schema => !IsSystemSchema(schema))
            .OrderBy(static schema => schema, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The warning <c>diff</c> and <c>publish</c> write for <paramref name="schemas"/> (non-empty).</summary>
    public static string Describe(IReadOnlyList<string> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        return $"Warning: pgproj does not drop schemas; {(schemas.Count == 1 ? "schema" : "schemas")} {string.Join(", ", schemas)} " +
            "the database has and the project does not will be left in place (the objects in them are reported and dropped as usual). " +
            "Drop them yourself (DROP SCHEMA ... ) if they are no longer wanted.";
    }

    private static HashSet<string> SchemaNames(DatabaseModel model, bool countDeclared)
    {
        var schemas = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var node in model.GetAllNodes())
        {
            if (!string.IsNullOrWhiteSpace(node.SchemaName) && (countDeclared || node is not SchemaDefinition { IsCommentStated: false }))
            {
                schemas.Add(node.SchemaName);
            }
        }

        return schemas;
    }

    private static bool IsSystemSchema(string schema)
        => string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schema, "information_schema", StringComparison.OrdinalIgnoreCase)
            || schema.StartsWith("pg_", StringComparison.OrdinalIgnoreCase);
}
