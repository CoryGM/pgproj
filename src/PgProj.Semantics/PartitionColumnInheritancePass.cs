namespace PgProj.Semantics;

/// <summary>
/// A partition's <c>CREATE TABLE ... PARTITION OF parent ...</c> never lists its own columns (G-24c) — they are inherited from
/// the parent, which <see cref="ColumnPass"/> has already populated by the time this runs. PostgreSQL's own catalog treats a
/// partition the same way a live-loaded table already does here: each partition physically has its own attribute rows (verified
/// on a real server), not a reference to the parent's. This copies the parent's columns onto a childless partition, re-keyed to
/// the child's own identity, so every later pass (defaults, dependency resolution, the binder, diff) sees a table with real
/// columns without needing its own partition-awareness.
/// </summary>
internal sealed class PartitionColumnInheritancePass
{
    public DatabaseModel Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var tables = new Dictionary<string, TableDefinition>(model.Tables, PgProj.Parser.SqlIdentifiers.NameComparer);

        // A sub-partitioned child (itself further partitioned into partitions) needs its own columns filled in from its
        // immediate parent before its children can inherit from it, so this may need more than one pass over the whole set.
        bool filledAny;
        var remainingPasses = tables.Count;
        do
        {
            filledAny = false;
            foreach (var table in tables.Values.ToArray())
            {
                if (string.IsNullOrWhiteSpace(table.PartitionOfTableName) || table.Columns.Count > 0)
                {
                    continue;
                }

                var parentKey = DatabaseModel.CreateKey(table.PartitionOfSchemaName, table.PartitionOfTableName);
                if (!tables.TryGetValue(parentKey, out var parent) || parent.Columns.Count == 0)
                {
                    continue;
                }

                tables[table.Key] = table with { Columns = InheritColumns(table, parent) };
                filledAny = true;
            }
        }
        while (filledAny && remainingPasses-- > 0);

        // A partition that lists its own columns (made first and attached, G-236) and names a NOT NULL constraint other than its parent's keeps that name only if it is created that way (G-264).
        foreach (var table in tables.Values.ToArray())
        {
            if (string.IsNullOrWhiteSpace(table.PartitionOfTableName) || table.Columns.Count == 0
                || !tables.TryGetValue(DatabaseModel.CreateKey(table.PartitionOfSchemaName, table.PartitionOfTableName), out var parent))
            {
                continue;
            }

            // Or lists its columns in an order of its own, which PARTITION OF cannot give it (G-297): a partition made first has the order it was written in, and PostgreSQL cannot reorder columns.
            if (table.Columns.Any(column => NamesAnotherNotNull(column, parent)) || ColumnOrderDiffers(table, parent))
            {
                tables[table.Key] = table with { PartitionKeepsOwnColumns = true };
            }
        }

        return model with { Tables = tables };
    }

    /// <summary>True when the partition lists the same columns as its parent in another order. A partition that lists fewer or other columns is not compared (PostgreSQL refuses to attach it).</summary>
    private static bool ColumnOrderDiffers(TableDefinition table, TableDefinition parent)
    {
        var own = table.Columns.Select(static column => column.Name).ToArray();
        var inherited = parent.Columns.Select(static column => column.Name).ToArray();
        return own.Length == inherited.Length
            && own.OrderBy(static name => name, StringComparer.Ordinal).SequenceEqual(inherited.OrderBy(static name => name, StringComparer.Ordinal), StringComparer.Ordinal)
            && !own.SequenceEqual(inherited, StringComparer.Ordinal);
    }

    private static bool NamesAnotherNotNull(ColumnDefinition column, TableDefinition parent)
    {
        if (column.AllowsNull || column.NotNullName is not { Length: > 0 } name)
        {
            return false;
        }

        var parentColumn = parent.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, column.Name, StringComparison.Ordinal));
        var parentName = parentColumn is { AllowsNull: false } ? parentColumn.NotNullName ?? $"{parent.Name}_{column.Name}_not_null" : null;
        return !string.Equals(name, parentName, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ColumnDefinition> InheritColumns(TableDefinition child, TableDefinition parent)
        => parent.Columns
            .Select(column => column with
            {
                Key = DatabaseModel.CreateKey(child.SchemaName, $"{child.Name}.{column.Name}"),
                SchemaName = child.SchemaName,
                TableName = child.Name,
                // A partition's NOT NULL is its parent's constraint under the parent's name, with no comment of its own (G-235).
                NotNullComment = null,
                NotNullCommentStated = false,
            })
            .ToArray();
}
