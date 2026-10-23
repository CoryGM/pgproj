using System.Text;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff;

/// <summary>
/// A column renamed with <c>@renamed-from</c> takes its constraints and indexes with it (G-277): PostgreSQL tracks their columns by number, so
/// <c>RENAME COLUMN</c> is all they need. Before the comparison, the old side's constraints and indexes on the table, and the foreign keys that
/// reference it, are given the new names, so a change that is only the rename is no change to them, and <c>publish</c> no longer drops and adds them
/// (rebuilding a unique index and re-scanning the referencing tables under lock). Only the comparison forms change; what is deployed is the project's.
/// </summary>
internal static class RenamedColumnReferences
{
    /// <summary><paramref name="oldModel"/> with those references renamed; the same instance when the project renames no column of a live table.</summary>
    public static DatabaseModel Apply(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var renames = FindRenames(oldModel, newModel);
        if (renames.Count == 0)
        {
            return oldModel;
        }

        var constraints = new Dictionary<string, ConstraintDefinition>(oldModel.Constraints, SqlIdentifiers.NameComparer);
        foreach (var (key, constraint) in oldModel.Constraints)
        {
            var own = renames.GetValueOrDefault(TableKey(constraint.SchemaName, constraint.TableName));
            var referenced = constraint.ForeignKeyReferencedTable is null
                ? null
                : renames.GetValueOrDefault(TableKey(constraint.ForeignKeyReferencedSchemaName ?? constraint.SchemaName, constraint.ForeignKeyReferencedTable));
            if (own is null && referenced is null)
            {
                continue;
            }

            var renamed = constraint;
            if (own is not null)
            {
                renamed = renamed with
                {
                    PrimaryKeyColumns = Names(renamed.PrimaryKeyColumns, own),
                    UniqueColumns = Names(renamed.UniqueColumns, own),
                    RawCheckExpression = Text(renamed.RawCheckExpression, own),
                    ReferencedColumns = ReferencedNames(renamed.ReferencedColumns, own),
                    NormalizedCheckExpression = ParseTree(renamed.NormalizedCheckExpression, own),
                    CheckExpressionNode = Tree(renamed.CheckExpressionNode, own),
                    ForeignKeyColumns = Names(renamed.ForeignKeyColumns, own),
                    ForeignKeyDeleteSetColumns = Names(renamed.ForeignKeyDeleteSetColumns, own),
                    IncludeColumns = Names(renamed.IncludeColumns, own),
                    RawExclusionPredicateExpression = Text(renamed.RawExclusionPredicateExpression, own),
                    ExclusionElements = renamed.ExclusionElements
                        .Select(element => element with
                        {
                            Name = element.Name is null ? null : Name(element.Name, own),
                            RawExpression = Text(element.RawExpression, own),
                            NormalizedExpression = ParseTree(element.NormalizedExpression, own),
                        })
                        .ToArray(),
                };
            }

            if (referenced is not null)
            {
                renamed = renamed with { ForeignKeyReferencedColumns = Names(renamed.ForeignKeyReferencedColumns, referenced) };
            }

            constraints[key] = renamed;
        }

        var indexes = new Dictionary<string, IndexDefinition>(oldModel.Indexes, SqlIdentifiers.NameComparer);
        foreach (var (key, index) in oldModel.Indexes)
        {
            if (renames.GetValueOrDefault(TableKey(index.SchemaName, index.TableName)) is not { } own)
            {
                continue;
            }

            indexes[key] = index with
            {
                ReferencedColumns = ReferencedNames(index.ReferencedColumns, own),
                KeyColumns = Names(index.KeyColumns, own),
                IncludeColumns = Names(index.IncludeColumns, own),
                RawPredicateExpression = Text(index.RawPredicateExpression, own),
                NormalizedPredicateExpression = ParseTree(index.NormalizedPredicateExpression, own),
                PredicateExpressionNode = Tree(index.PredicateExpressionNode, own),
                KeyElements = index.KeyElements
                    .Select(element => element with
                    {
                        Name = element.Name is null ? null : Name(element.Name, own),
                        RawExpression = Text(element.RawExpression, own),
                        NormalizedExpression = ParseTree(element.NormalizedExpression, own),
                    })
                    .ToArray(),
            };
        }

        var movedConstraints = PairGeneratedNames(constraints, newModel.Constraints, renames,
            static constraint => (constraint.SchemaName, constraint.TableName, constraint.Name),
            static (constraint, key) => constraint with { Key = key },
            static (schema, table, name, model) => model.TryGetValue(DatabaseModel.CreateConstraintKey(schema, table, name), out var found) && found.IsNameGenerated ? found.Key : null);
        PairGeneratedNames(indexes, newModel.Indexes, renames,
            static index => (index.SchemaName, index.TableName, index.Name),
            static (index, key) => index with { Key = key },
            static (schema, _, name, model) => model.TryGetValue(DatabaseModel.CreateKey(schema, name), out var found) && found.IsNameGenerated ? found.Key : null);

        // A table finds its constraints by key, so a moved constraint is moved in its table's list too; left out, it would vanish from the comparison and never be dropped.
        var tables = movedConstraints.Count == 0
            ? oldModel.Tables
            : oldModel.Tables.ToDictionary(
                static pair => pair.Key,
                pair => pair.Value.ConstraintKeys.Any(movedConstraints.ContainsKey)
                    ? pair.Value with { ConstraintKeys = pair.Value.ConstraintKeys.Select(key => movedConstraints.GetValueOrDefault(key, key)).ToArray() }
                    : pair.Value,
                SqlIdentifiers.NameComparer);

        return oldModel with { Tables = tables, Constraints = constraints, Indexes = indexes };
    }

    /// <summary>
    /// A constraint or index the project left unnamed is named after its columns (<c>t_email_key</c>, <c>t_code_idx</c>), so a column rename changes the name the project
    /// gives it while the database keeps the old one (G-289, approved as recommended). An old one whose name is the old column's default, with the project's
    /// generated one of the new column's name on the same table, is moved to the project's key and keeps its old <c>Name</c>, so the diff reports the name as the
    /// only change and <c>publish</c> renames it (<c>RENAME CONSTRAINT</c>, <c>ALTER INDEX ... RENAME TO</c>) instead of dropping and adding it.
    /// </summary>
    private static Dictionary<string, string> PairGeneratedNames<T>(
        Dictionary<string, T> oldObjects,
        IReadOnlyDictionary<string, T> newObjects,
        Dictionary<string, Dictionary<string, string>> renames,
        Func<T, (string? Schema, string? Table, string Name)> describe,
        Func<T, string, T> withKey,
        Func<string?, string?, string, IReadOnlyDictionary<string, T>, string?> generatedKey)
    {
        var moved = new Dictionary<string, string>(SqlIdentifiers.NameComparer);
        foreach (var (key, item) in oldObjects.ToArray())
        {
            var (schema, table, name) = describe(item);
            if (table is null || newObjects.ContainsKey(key) || renames.GetValueOrDefault(TableKey(schema, table)) is not { } own)
            {
                continue;
            }

            foreach (var (oldColumn, newColumn) in own)
            {
                var prefix = table + "_";
                var segment = "_" + oldColumn + "_";
                var index = name.StartsWith(prefix, StringComparison.Ordinal) ? name.IndexOf(segment, prefix.Length - 1, StringComparison.Ordinal) : -1;
                if (index < 0)
                {
                    continue;
                }

                var candidate = name[..index] + "_" + newColumn + "_" + name[(index + segment.Length)..];
                if (generatedKey(schema, table, candidate, newObjects) is { } newKey && !oldObjects.ContainsKey(newKey))
                {
                    oldObjects.Remove(key);
                    oldObjects[newKey] = withKey(item, newKey);
                    moved[key] = newKey;
                    break;
                }
            }
        }

        return moved;
    }

    /// <summary>
    /// Per live table, the project's renames of its columns, old name to new, matched as <c>AlterOperation</c> matches them, so the comparison assumes a
    /// rename exactly when <c>publish</c> writes one (a quoted or capitalized annotation is G-287): the old name a live column, not kept by the project,
    /// and claimed once.
    /// </summary>
    internal static Dictionary<string, Dictionary<string, string>> FindRenames(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var renames = new Dictionary<string, Dictionary<string, string>>(SqlIdentifiers.NameComparer);
        foreach (var (key, newTable) in newModel.Tables)
        {
            if (!oldModel.Tables.TryGetValue(key, out var oldTable))
            {
                continue;
            }

            var oldNames = oldTable.Columns.Select(static column => column.Name).ToHashSet(SqlIdentifiers.NameComparer);
            var newNames = newTable.Columns.Select(static column => column.Name).ToHashSet(SqlIdentifiers.NameComparer);
            var tableRenames = new Dictionary<string, string>(SqlIdentifiers.NameComparer);
            foreach (var column in newTable.Columns)
            {
                if (!string.IsNullOrWhiteSpace(column.RenamedFrom)
                    && oldNames.Contains(column.RenamedFrom)
                    && !newNames.Contains(column.RenamedFrom)
                    && !oldNames.Contains(column.Name))
                {
                    tableRenames.TryAdd(column.RenamedFrom, column.Name);
                }
            }

            if (tableRenames.Count > 0)
            {
                renames[TableKey(newTable.SchemaName, newTable.Name)] = tableRenames;
            }
        }

        return renames;
    }

    private static string TableKey(string? schemaName, string? tableName)
        => DatabaseModel.CreateKey(schemaName ?? "public", tableName ?? string.Empty);

    private static IReadOnlyList<string> Names(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> renames)
        => names.Select(name => Name(name, renames)).ToArray();

    /// <summary>
    /// The columns an expression reads, which the text reader records either exactly or, for a quoted name, squashed to its lower-case letters, digits, and
    /// underscores (<c>"Sku Code"</c> as <c>skucode</c>), on both sides alike; a renamed one is written in the form the new name gets (a plain name is its own).
    /// </summary>
    private static IReadOnlyList<string> ReferencedNames(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> renames)
    {
        var squashed = renames.ToDictionary(static rename => Squash(rename.Key), static rename => Squash(rename.Value), StringComparer.Ordinal);
        return names.Select(name => renames.TryGetValue(name, out var renamed) ? Squash(renamed) : squashed.TryGetValue(name, out var squash) ? squash : name).ToArray();
    }

    private static string Squash(string name)
        => new(name.ToLowerInvariant().Where(static character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_').ToArray());

    private static string Name(string name, IReadOnlyDictionary<string, string> renames)
        => renames.TryGetValue(name, out var renamed) ? renamed : name;

    private static ExpressionNode? Tree(ExpressionNode? node, IReadOnlyDictionary<string, string> renames)
        => node is null ? null : node with { Text = Text(node.Text, renames)!, Children = node.Children.Select(child => Tree(child, renames)!).ToArray() };

    private static readonly System.Text.RegularExpressions.Regex ColumnRefFields = new(
        @"ColumnRef:\{fields:\[(?<fields>[^\]]*)\]\}",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly System.Text.RegularExpressions.Regex LastStringField = new(
        @"\{String:\{sval:(?<name>""(?:[^""\\]|\\.)*"")\}\}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The comparison form of an expression (its parse tree, <c>{ColumnRef:{fields:[{String:{sval:"code"}}]}}</c>) with each column reference's name, its
    /// last field, written with the new name; a string constant (<c>A_Const</c>) or a function name is not a column reference and is left alone.
    /// </summary>
    internal static string? ParseTree(string? tree, IReadOnlyDictionary<string, string> renames)
    {
        if (string.IsNullOrEmpty(tree))
        {
            return tree;
        }

        return ColumnRefFields.Replace(tree, match =>
        {
            var fields = match.Groups["fields"].Value;
            var last = LastStringField.Match(fields);
            if (!last.Success
                || System.Text.Json.JsonSerializer.Deserialize<string>(last.Groups["name"].Value) is not { } name
                || !renames.TryGetValue(name, out var renamed))
            {
                return match.Value;
            }

            var group = last.Groups["name"];
            var newFields = string.Concat(fields.AsSpan(0, group.Index), System.Text.Json.JsonSerializer.Serialize(renamed), fields.AsSpan(group.Index + group.Length));
            return $"ColumnRef:{{fields:[{newFields}]}}";
        });
    }

    /// <summary>
    /// The expression text with each identifier that names a renamed column written with its new name: a bare word (PostgreSQL folds it) or a quoted
    /// one, standing alone or after a table qualifier, never inside a string literal and never one followed by <c>(</c> (a function).
    /// </summary>
    internal static string? Text(string? text, IReadOnlyDictionary<string, string> renames)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '\'')
            {
                var end = index + 1;
                while (end < text.Length)
                {
                    if (text[end] == '\'' && end + 1 < text.Length && text[end + 1] == '\'')
                    {
                        end += 2;
                        continue;
                    }

                    if (text[end] == '\'')
                    {
                        break;
                    }

                    end++;
                }

                end = Math.Min(end + 1, text.Length);
                result.Append(text, index, end - index);
                index = end;
                continue;
            }

            string? word = null;
            var start = index;
            if (character == '"')
            {
                var end = index + 1;
                var name = new StringBuilder();
                while (end < text.Length)
                {
                    if (text[end] == '"' && end + 1 < text.Length && text[end + 1] == '"')
                    {
                        name.Append('"');
                        end += 2;
                        continue;
                    }

                    if (text[end] == '"')
                    {
                        break;
                    }

                    name.Append(text[end]);
                    end++;
                }

                index = Math.Min(end + 1, text.Length);
                word = name.ToString();
            }
            else if (char.IsLetter(character) || character == '_')
            {
                var end = index;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$'))
                {
                    end++;
                }

                index = end;
                word = text[start..end].ToLowerInvariant();
            }
            else
            {
                result.Append(character);
                index++;
                continue;
            }

            var next = index;
            while (next < text.Length && char.IsWhiteSpace(text[next]))
            {
                next++;
            }

            var isCall = next < text.Length && text[next] == '(';
            var isQualifier = next < text.Length && text[next] == '.';
            if (!isCall && !isQualifier && renames.TryGetValue(word, out var renamed))
            {
                result.Append(SqlIdentifiers.QuoteName(renamed));
            }
            else
            {
                result.Append(text, start, index - start);
            }
        }

        return result.ToString();
    }
}
