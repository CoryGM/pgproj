namespace PgProj.Semantics;

/// <summary>
/// A child's own default for a column it only inherits (G-237): <c>ALTER TABLE ONLY child ALTER COLUMN c SET DEFAULT x</c>. The child lists no such column, so
/// <see cref="TableDefinition.InheritedColumnDefaults"/> carries the default until the child has its parents' columns; this sets it on the inherited column (or clears it, for
/// <c>DROP DEFAULT</c>) and marks the column <see cref="ColumnDefinition.OverridesInheritedDefault"/>. A child's own NOT NULL constraint on an inherited column
/// (<c>CONSTRAINT foo NOT NULL a</c>, PostgreSQL 18; G-253) is applied the same way and marks <see cref="ColumnDefinition.OwnsInheritedNotNull"/>. Runs after <see cref="TableInheritancePass"/>, before the passes that read defaults.
/// </summary>
internal sealed class InheritedColumnDefaultPass
{
    public DatabaseModel Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!model.Tables.Values.Any(static table => table.InheritedColumnDefaults.Count > 0 || table.InheritedColumnNotNulls.Count > 0))
        {
            return model;
        }

        var tables = new Dictionary<string, TableDefinition>(model.Tables, PgProj.Parser.SqlIdentifiers.NameComparer);
        var diagnostics = new List<SemanticDiagnostic>();
        foreach (var table in tables.Values.Where(static table => table.InheritedColumnDefaults.Count > 0 || table.InheritedColumnNotNulls.Count > 0).ToArray())
        {
            var columns = table.Columns.ToArray();
            foreach (var notNull in table.InheritedColumnNotNulls)
            {
                // A partition's columns are its parent's too (G-266).
                var notNullIndex = Array.FindIndex(columns, column => string.Equals(column.Name, notNull.ColumnName, StringComparison.Ordinal)
                    && (column.InheritedFromTable is not null || !string.IsNullOrWhiteSpace(table.PartitionOfTableName)));
                if (notNullIndex < 0)
                {
                    diagnostics.Add(SemanticDiagnostics.Warning(
                        $"NOT NULL on {table.SchemaName}.{table.Name}.{notNull.ColumnName}: the table does not inherit that column, so the constraint is ignored.",
                        table.Location,
                        nameof(InheritedColumnDefaultPass)));
                    continue;
                }

                // The child's own constraint on the inherited column (G-253): its own name, and NO INHERIT when written.
                columns[notNullIndex] = columns[notNullIndex] with
                {
                    IsNullable = false,
                    NotNullName = notNull.Name ?? columns[notNullIndex].NotNullName,
                    NotNullNoInherit = notNull.NoInherit,
                    OwnsInheritedNotNull = true,
                };
            }

            foreach (var change in table.InheritedColumnDefaults)
            {
                var index = Array.FindIndex(columns, column => string.Equals(column.Name, change.ColumnName, StringComparison.Ordinal)
                    && (column.InheritedFromTable is not null || !string.IsNullOrWhiteSpace(table.PartitionOfTableName)));
                if (index < 0)
                {
                    diagnostics.Add(SemanticDiagnostics.Warning(
                        change.IsInPartitionOfList
                            ? $"PARTITION OF list of {table.SchemaName}.{table.Name}: column {change.ColumnName} is not a column of its parent, so its DEFAULT is ignored."
                            : $"ALTER TABLE {table.SchemaName}.{table.Name} ALTER COLUMN {change.ColumnName} SET/DROP DEFAULT: the table does not inherit that column, so the action is ignored.",
                        table.Location,
                        nameof(InheritedColumnDefaultPass)));
                    continue;
                }

                var column = columns[index];
                columns[index] = column with
                {
                    RawDefaultExpression = change.RawExpression,
                    OverridesInheritedDefault = true,
                    DefaultExpressionNode = null,
                    DeclarationNode = column.DeclarationNode is null
                        ? null
                        : column.DeclarationNode with { RawDefaultExpression = change.RawExpression, DefaultExpressionAst = change.ExpressionAst },
                };
            }

            tables[table.Key] = table with { Columns = columns };
        }

        return model with { Tables = tables, Diagnostics = [.. model.Diagnostics, .. diagnostics] };
    }
}
