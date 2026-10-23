using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// A typed table (<c>CREATE TABLE t OF some_type [(col WITH OPTIONS ...)]</c>, G-136) has the columns of its composite type and lists none of its own but the defaults,
/// <c>NOT NULL</c>s, and so on it gives some of them with <c>WITH OPTIONS</c>. Whoever reads the project therefore has to read the type to know the table's columns, and
/// the type may be in another file, so this gives each typed table the columns of its type before anything is declared: one column per field, in the type's order, with
/// the options of the column of that name merged in (a partition's columns are inherited the same way, G-24c). Everything after this sees an ordinary table, and a
/// live typed table, which is read as the same statement, goes the same way. The type is marked as having typed tables, because changing one of its fields then needs
/// <c>CASCADE</c>. A typed table whose type is not a composite type the project declares is reported, and keeps only the columns it wrote.
/// </summary>
internal static class TypedTableExpansion
{
    public static (IReadOnlyList<SqlSyntaxTree> Trees, IReadOnlyList<(string Message, SemanticLocation? Location)> Problems) Apply(IReadOnlyList<SqlSyntaxTree> trees)
    {
        ArgumentNullException.ThrowIfNull(trees);

        if (!trees.Any(static tree => tree.Nodes.OfType<TableNode>().Any(static table => table.OfTypeName is not null)))
        {
            return (trees, []);
        }

        var composites = trees
            .SelectMany(static tree => tree.Nodes.OfType<TypeNode>())
            .Where(static type => string.Equals(type.Form, "composite", StringComparison.OrdinalIgnoreCase) && type.Name is not null)
            .GroupBy(static type => DatabaseModel.CreateKey(type.SchemaName, type.Name!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), PgProj.Parser.SqlIdentifiers.NameComparer);

        var problems = new List<(string Message, SemanticLocation? Location)>();
        var usedTypes = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var expanded = trees.Select(tree => tree with { Nodes = ExpandNodes(tree.Nodes, tree.FilePath, composites, usedTypes, problems) }).ToArray();

        // A type can be in a different file from its typed tables, so it is marked once every tree has been read.
        var marked = expanded.Select(tree => tree with
        {
            Nodes = tree.Nodes
                .Select(node => node is TypeNode type && type.Name is not null && usedTypes.Contains(DatabaseModel.CreateKey(type.SchemaName, type.Name))
                    ? type with { HasTypedTables = true }
                    : node)
                .ToArray(),
        }).ToArray();

        return (marked, problems);
    }

    private static IReadOnlyList<SqlNode> ExpandNodes(
        IReadOnlyList<SqlNode> nodes,
        string? filePath,
        IReadOnlyDictionary<string, TypeNode> composites,
        ISet<string> usedTypes,
        ICollection<(string Message, SemanticLocation? Location)> problems)
    {
        var result = new List<SqlNode>(nodes.Count);
        var skipColumnsOf = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var node in nodes)
        {
            if (node is TableNode { OfTypeName: { } ofName } table)
            {
                var typeKey = DatabaseModel.CreateKey(table.OfTypeSchemaName, ofName);
                var tableKey = DatabaseModel.CreateKey(table.SchemaName, table.Name ?? "unknown_table");
                if (!composites.TryGetValue(typeKey, out var type))
                {
                    problems.Add(($"Typed table '{tableKey}' is of type '{typeKey}', which is not a composite type this project declares (CREATE TYPE ... AS (...)): its columns cannot be known.", SemanticLocation.Of(filePath, table.Range)));
                    result.Add(table);
                    continue;
                }

                usedTypes.Add(typeKey);
                var columns = ExpandColumns(table, type, filePath, problems);
                result.Add(table with { Columns = columns });
                result.AddRange(columns);
                skipColumnsOf.Add(tableKey);
                continue;
            }

            // The statement's own column nodes (one per WITH OPTIONS) are replaced by the expanded ones above.
            if (node is ColumnNode column && skipColumnsOf.Contains(DatabaseModel.CreateKey(column.SchemaName, column.TableName ?? string.Empty)))
            {
                continue;
            }

            result.Add(node);
        }

        return result;
    }

    private static IReadOnlyList<ColumnNode> ExpandColumns(TableNode table, TypeNode type, string? filePath, ICollection<(string Message, SemanticLocation? Location)> problems)
    {
        var options = table.Columns
            .Where(static column => column.Name is not null)
            .GroupBy(static column => column.Name!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var fieldNames = type.CompositeFieldTypes.Select(static field => field.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in options.Keys.Where(name => !fieldNames.Contains(name)))
        {
            problems.Add(($"Typed table '{DatabaseModel.CreateKey(table.SchemaName, table.Name ?? "unknown_table")}' gives WITH OPTIONS for column '{name}', which its type has no field for.", SemanticLocation.Of(filePath, options[name].Range ?? table.Range)));
        }

        return type.CompositeFieldTypes
            .Select(field =>
            {
                var column = options.TryGetValue(field.Name, out var written)
                    ? written
                    : new ColumnNode(field.Name, table.Name, table.SchemaName, null, null, null, null, false, null, null, null, null);
                return column with
                {
                    TableName = table.Name,
                    SchemaName = table.SchemaName,
                    // The parse's name, with the modifiers kept apart, as an ordinary column has (G-199): the field's written text carries its
                    // modifiers (character varying(10)), so it compared as character varying(10)(10), and float8 never matched double precision.
                    DataType = field.BaseType,
                    RawType = field.BaseType,
                    WrittenType = field.RawType,
                    TypeModifiers = field.Modifiers,
                    ArrayDimensions = field.ArrayDimensions,
                    IntervalFields = field.IntervalFields,
                    // The column has the field's collation (G-192), which the database reports on the typed table's column.
                    CollationName = column.CollationName ?? field.CollationName,
                };
            })
            .ToArray();
    }
}
