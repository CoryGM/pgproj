namespace PgProj.Semantics;

/// <summary>
/// A child of <c>CREATE TABLE ... INHERITS (...)</c> has its parents' columns as well as its own (G-194): PostgreSQL gives the child an attribute row for each,
/// so a live child already has them, and its <c>CREATE TABLE</c> lists only its own. This adds each parent's columns to the child, first, in parent order, merging
/// a name that more than one parent has, each marked with <see cref="ColumnDefinition.InheritedFromTable"/> and re-keyed to the child. A column the child declares
/// itself with a parent's name is the merged column PostgreSQL makes of the two: the child's declaration, not marked inherited. Runs after
/// <see cref="ColumnPass"/> and <see cref="PartitionColumnInheritancePass"/>, as often as a chain of parents needs.
/// </summary>
internal sealed class TableInheritancePass
{
    public DatabaseModel Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!model.Tables.Values.Any(static table => table.InheritsFrom.Count > 0))
        {
            return model;
        }

        var tables = new Dictionary<string, TableDefinition>(model.Tables, PgProj.Parser.SqlIdentifiers.NameComparer);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A parent that inherits itself must have its columns before its children take them, so the children wait for it; a cycle never resolves and is left as written.
        bool progressed;
        do
        {
            progressed = false;
            foreach (var table in tables.Values.Where(table => table.InheritsFrom.Count > 0 && !done.Contains(table.Key)).ToArray())
            {
                var parents = table.InheritsFrom.Select(key => tables.TryGetValue(key, out var parent) ? parent : null).ToArray();
                if (parents.Any(parent => parent is not null && parent.InheritsFrom.Count > 0 && !done.Contains(parent.Key)))
                {
                    continue;
                }

                tables[table.Key] = table with { Columns = InheritColumns(table, parents.OfType<TableDefinition>()) };
                done.Add(table.Key);
                progressed = true;
            }
        }
        while (progressed);

        return model with { Tables = tables };
    }

    private static IReadOnlyList<ColumnDefinition> InheritColumns(TableDefinition child, IEnumerable<TableDefinition> parents)
    {
        var own = child.Columns.ToDictionary(static column => column.Name, PgProj.Parser.SqlIdentifiers.NameComparer);
        var columns = new List<ColumnDefinition>();
        var seen = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var parent in parents)
        {
            foreach (var column in parent.Columns)
            {
                if (!seen.Add(column.Name))
                {
                    continue;
                }

                // A column the child declares too is PostgreSQL's merge of the two: the child's own declaration, in the inherited position, with the parent's
                // default when it states none, and NOT NULL when either has it.
                columns.Add(own.TryGetValue(column.Name, out var declared)
                    ? Merge(declared, column)
                    : column with
                    {
                        Key = DatabaseModel.CreateKey(child.SchemaName, $"{child.Name}.{column.Name}"),
                        SchemaName = child.SchemaName,
                        TableName = child.Name,
                        // A NOT NULL ... NO INHERIT is the parent's own: the child's column is nullable (G-253).
                        IsNullable = column.NotNullNoInherit ? null : column.IsNullable,
                        NotNullName = column.NotNullNoInherit ? null : column.NotNullName,
                        NotNullNoInherit = false,
                        // A grandparent's column is still inherited, from the parent the child names.
                        InheritedFromTable = parent.Key,
                        // PostgreSQL does not carry a column's comment or its identity to a child.
                        Comment = null,
                        IdentityGeneration = null,
                        // The child's NOT NULL is the parent's constraint, under the parent's name, and has no comment of its own (G-235).
                        NotNullComment = null,
                        NotNullCommentStated = false,
                    });
            }
        }

        columns.AddRange(child.Columns.Where(column => !seen.Contains(column.Name)));
        return columns;
    }

    private static ColumnDefinition Merge(ColumnDefinition declared, ColumnDefinition inherited)
    {
        var merged = declared;
        if (declared.RawDefaultExpression is null && !declared.IsGenerated && inherited.RawDefaultExpression is not null)
        {
            merged = merged with { RawDefaultExpression = inherited.RawDefaultExpression, DefaultExpressionNode = inherited.DefaultExpressionNode };
        }

        // A generated column the child lists without an expression of its own is the parent's generation (G-299); the child's column is not written with it.
        if (inherited.IsGenerated && !declared.IsGenerated && declared.RawDefaultExpression is null)
        {
            merged = merged with
            {
                IsGenerated = true,
                GeneratedExpression = inherited.GeneratedExpression,
                NormalizedGeneratedExpression = inherited.NormalizedGeneratedExpression,
                IsVirtual = inherited.IsVirtual,
                DefaultExpressionNode = inherited.DefaultExpressionNode,
                // The comparison form is built from the expression's tree, which only the parent's declaration has.
                DeclarationNode = merged.DeclarationNode is { } own && inherited.DeclarationNode is { } parentNode
                    ? own with { IsGenerated = true, GeneratedExpression = parentNode.GeneratedExpression, GeneratedExpressionAst = parentNode.GeneratedExpressionAst }
                    : merged.DeclarationNode,
                GenerationIsInherited = true,
            };
        }

        // The inherited NOT NULL keeps its name unless the child states its own (G-235). A NO INHERIT one is not inherited, and a child that declares NOT NULL itself has its own
        // constraint, under its own name, not the parent's (G-253).
        if (inherited.IsNullable != false || inherited.NotNullNoInherit)
        {
            return merged;
        }

        return merged.IsNullable == false
            ? merged
            : merged with { IsNullable = false, NotNullName = inherited.NotNullName, NotNullIsInherited = true };
    }
}
