using PgProj.Semantics;

namespace PgProj.Diff;

/// <summary>
/// A column whose type belongs to an extension the project moves to another schema (G-218). <c>ALTER EXTENSION ... SET SCHEMA</c> moves the type with it,
/// so the column is the same; only its spelling changes (<c>citext</c> becomes <c>other.citext</c>). Before the comparison, such a column on the old side
/// is given the new side's spelling, and is no change.
/// </summary>
internal static class MovedExtensionTypes
{
    /// <summary>
    /// <paramref name="oldModel"/>, with the columns that use a moved extension's type spelled as in <paramref name="newModel"/>; the same instance when no
    /// extension moves. A column qualifies when its type differs only in schema, the old schema is the extension's old one (or none written), the new one is
    /// its new one, and the type is not one the project declares itself.
    /// </summary>
    public static DatabaseModel Apply(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var moves = newModel.Types.Values
            .Where(static type => string.Equals(type.TypeKind, "extension", StringComparison.Ordinal) && type.ExtensionSchemaStated)
            .Select(type => (New: type, Old: oldModel.Types.GetValueOrDefault(type.Key)))
            .Where(static pair => pair.Old?.SchemaName is not null && !string.Equals(pair.Old.SchemaName, pair.New.SchemaName, StringComparison.Ordinal))
            .Select(static pair => (From: pair.Old!.SchemaName!, To: pair.New.SchemaName!))
            .ToArray();
        if (moves.Length == 0)
        {
            return oldModel;
        }

        var changed = false;
        var tables = new Dictionary<string, TableDefinition>(oldModel.Tables, PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var (key, oldTable) in oldModel.Tables)
        {
            if (!newModel.Tables.TryGetValue(key, out var newTable))
            {
                continue;
            }

            var newColumns = newTable.Columns.ToDictionary(static column => column.Name, PgProj.Parser.SqlIdentifiers.NameComparer);
            var columns = oldTable.Columns
                .Select(column => newColumns.TryGetValue(column.Name, out var newColumn) && IsMoved(column, newColumn, moves, newModel)
                    ? column with { DataType = newColumn.DataType, RawType = newColumn.RawType, WrittenType = newColumn.WrittenType }
                    : column)
                .ToArray();
            if (!columns.SequenceEqual(oldTable.Columns))
            {
                tables[key] = oldTable with { Columns = columns };
                changed = true;
            }
        }

        return changed ? oldModel with { Tables = tables } : oldModel;
    }

    private static bool IsMoved(ColumnDefinition oldColumn, ColumnDefinition newColumn, IReadOnlyList<(string From, string To)> moves, DatabaseModel newModel)
    {
        if (string.Equals(oldColumn.DataType, newColumn.DataType, StringComparison.Ordinal)
            || oldColumn.ArrayDimensions != newColumn.ArrayDimensions
            || !oldColumn.TypeModifiers.SequenceEqual(newColumn.TypeModifiers, StringComparer.Ordinal))
        {
            return false;
        }

        var (oldSchema, oldName) = Split(oldColumn.DataType);
        var (newSchema, newName) = Split(newColumn.DataType);
        return newSchema is not null
            && string.Equals(oldName, newName, StringComparison.Ordinal)
            && !newModel.Types.ContainsKey(DatabaseModel.CreateKey(newSchema, newName))
            && moves.Any(move => string.Equals(move.To, newSchema, StringComparison.Ordinal) && (oldSchema is null || string.Equals(move.From, oldSchema, StringComparison.Ordinal)));
    }

    /// <summary>A type name's schema (null when none is written) and name, unquoted; the split is at the last dot outside double quotes.</summary>
    private static (string? Schema, string Name) Split(string? dataType)
    {
        var text = dataType?.Trim() ?? string.Empty;
        var split = -1;
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '"')
            {
                quoted = !quoted;
            }
            else if (text[index] == '.' && !quoted)
            {
                split = index;
            }
        }

        static string Unquote(string part)
            => part.Length >= 2 && part[0] == '"' && part[^1] == '"' ? part[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : part;

        return split < 0 ? (null, Unquote(text)) : (Unquote(text[..split]), Unquote(text[(split + 1)..]));
    }
}
