using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>A construct in a project that only a server of at least <see cref="MinimumMajorVersion"/> accepts, and where it is (G-121).</summary>
public sealed record ServerFeature(int MinimumMajorVersion, string Description);

/// <summary>
/// The constructs in a project's model that need a newer server than the one it is published to (G-121). The grammar is PostgreSQL 18's and
/// reads every version's SQL (G-120), so nothing in the project stops a PostgreSQL 18-only construct from being written; the server would
/// reject it halfway through the deployment. <c>publish --execute</c> asks first, and names each construct.
/// </summary>
public static class ServerFeatureRequirements
{
    /// <summary>Every construct in <paramref name="model"/> that needs a server newer than PostgreSQL 14 (pgproj's oldest supported since G-269).</summary>
    public static IReadOnlyList<ServerFeature> Find(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var features = new List<ServerFeature>();
        foreach (var constraint in model.Constraints.Values.OrderBy(static c => c.Key, StringComparer.Ordinal))
        {
            if (!constraint.IsEnforced)
            {
                features.Add(new ServerFeature(18, $"NOT ENFORCED constraint '{constraint.Key}'"));
            }

            if (constraint.WithoutOverlaps)
            {
                features.Add(new ServerFeature(18, $"temporal key (WITHOUT OVERLAPS) '{constraint.Key}'"));
            }

            if (constraint.ForeignKeyUsesPeriod)
            {
                features.Add(new ServerFeature(18, $"temporal foreign key (PERIOD) '{constraint.Key}'"));
            }

            // PostgreSQL 15 (G-269).
            if (constraint.NullsNotDistinct)
            {
                features.Add(new ServerFeature(15, $"NULLS NOT DISTINCT constraint '{constraint.Key}'"));
            }

            if (constraint.ForeignKeyDeleteSetColumns.Count > 0)
            {
                features.Add(new ServerFeature(15, $"ON DELETE SET NULL|DEFAULT with a column list '{constraint.Key}'"));
            }
        }

        // PostgreSQL 15, 17 (G-269): an index, a view, a sequence, a publication, or a collation that names something an older server does not have.
        foreach (var index in model.Indexes.Values.Where(static i => i.NullsNotDistinct).OrderBy(static i => i.Key, StringComparer.Ordinal))
        {
            features.Add(new ServerFeature(15, $"NULLS NOT DISTINCT index '{index.Key}'"));
        }

        foreach (var view in model.Views.Values.Where(static v => v.Options.Any(static o => o.StartsWith("security_invoker", StringComparison.OrdinalIgnoreCase))).OrderBy(static v => v.Key, StringComparer.Ordinal))
        {
            features.Add(new ServerFeature(15, $"security_invoker view '{view.Key}'"));
        }

        foreach (var sequence in model.Sequences.Values.Where(static s => s.IsUnlogged).OrderBy(static s => s.Key, StringComparer.Ordinal))
        {
            features.Add(new ServerFeature(15, $"UNLOGGED sequence '{sequence.Key}'"));
        }

        foreach (var publication in model.Publications.Values.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            if (publication.Schemas.Count > 0)
            {
                features.Add(new ServerFeature(15, $"publication '{publication.Name}' FOR TABLES IN SCHEMA"));
            }

            if (publication.Tables.Any(static t => t.Columns.Count > 0 || t.RawFilter is not null))
            {
                features.Add(new ServerFeature(15, $"publication '{publication.Name}' with a column list or a row filter"));
            }
        }

        foreach (var collation in model.Collations.Values.Where(static c => string.Equals(c.Provider, "builtin", StringComparison.OrdinalIgnoreCase)).OrderBy(static c => c.Key, StringComparer.Ordinal))
        {
            features.Add(new ServerFeature(17, $"builtin collation provider '{collation.Key}'"));
        }

        foreach (var table in model.Tables.Values.OrderBy(static t => t.Key, StringComparer.Ordinal))
        {
            // STORAGE inside CREATE TABLE or ADD COLUMN, as the project wrote it (G-269); one set with ALTER TABLE ... SET STORAGE works on every supported version.
            foreach (var column in table.Columns.Where(static c => c is { Storage: not null, IsStorageSetByAlter: false, InheritedFromTable: null, CopiedByLike: null }))
            {
                features.Add(new ServerFeature(16, $"STORAGE in the definition of column '{table.Key}.{column.Name}'"));
            }

            foreach (var column in table.Columns.Where(static c => c is { IsGenerated: true, IsVirtual: true }))
            {
                features.Add(new ServerFeature(18, $"VIRTUAL generated column '{table.Key}.{column.Name}'"));
            }

            foreach (var column in table.Columns.Where(static c => !c.AllowsNull && c.NotNullIsNotValid))
            {
                features.Add(new ServerFeature(18, $"NOT VALID NOT NULL on column '{table.Key}.{column.Name}'"));
            }

            // A partition's element list is deployed as written (G-266), so a NOT NULL written there as a table constraint is in the script.
            foreach (var notNull in table.InheritedColumnNotNulls.Where(static n => n.IsTableConstraintInPartitionOfList))
            {
                features.Add(new ServerFeature(18, $"table constraint NOT NULL in the PARTITION OF list of '{table.Key}' (column {notNull.ColumnName})"));
            }
        }

        return features;
    }

    /// <summary>The constructs of <paramref name="model"/> that a server of <paramref name="serverMajorVersion"/> does not accept.</summary>
    public static IReadOnlyList<ServerFeature> FindNewerThan(DatabaseModel model, int serverMajorVersion)
        => Find(model).Where(feature => feature.MinimumMajorVersion > serverMajorVersion).ToArray();
}
