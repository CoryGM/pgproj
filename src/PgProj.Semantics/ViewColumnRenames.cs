namespace PgProj.Semantics;

/// <summary>
/// The <c>ALTER VIEW ... RENAME COLUMN</c> statements that turn a view's live output columns into the project's, when the project's are the live ones
/// renamed (G-276). <c>CREATE OR REPLACE VIEW</c> cannot rename an output column (<c>42P16</c>), which is what renaming a table column that a view
/// selects needs; so the view's columns are renamed first and then it is replaced. The column lists are <see cref="ViewDefinition.ServerOutputColumns"/>:
/// <c>name type</c>, the name as <c>quote_ident</c> writes it.
/// </summary>
public static class ViewColumnRenames
{
    /// <summary>
    /// The renames, in position order, when every live column keeps its position and type in <paramref name="newColumns"/> (columns may be added after
    /// them, which <c>CREATE OR REPLACE VIEW</c> does itself): empty when no name changes. Null when the change is anything else (a column dropped,
    /// moved, or retyped, or a new name that another live column has), or when either list is unknown.
    /// </summary>
    public static IReadOnlyList<(string From, string To)>? Plan(IReadOnlyList<string> oldColumns, IReadOnlyList<string> newColumns)
    {
        ArgumentNullException.ThrowIfNull(oldColumns);
        ArgumentNullException.ThrowIfNull(newColumns);
        if (oldColumns.Count == 0 || newColumns.Count == 0 || newColumns.Count < oldColumns.Count)
        {
            return null;
        }

        var oldNames = oldColumns.Select(static column => Split(column).Name).ToArray();
        var renames = new List<(string From, string To)>();
        for (var index = 0; index < oldColumns.Count; index++)
        {
            var (oldName, oldType) = Split(oldColumns[index]);
            var (newName, newType) = Split(newColumns[index]);
            if (!string.Equals(oldType, newType, StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                continue;
            }

            // Two columns trading names, or a name another live column keeps, would need a rename through a third name; the view is rebuilt instead.
            if (oldNames.Contains(newName, StringComparer.Ordinal))
            {
                return null;
            }

            renames.Add((oldName, newName));
        }

        return renames;
    }

    /// <summary>An entry's name, as <c>quote_ident</c> wrote it (quotes kept), and its type.</summary>
    public static (string Name, string Type) Split(string column)
    {
        if (column.StartsWith('"'))
        {
            for (var index = 1; index < column.Length; index++)
            {
                if (column[index] != '"')
                {
                    continue;
                }

                if (index + 1 < column.Length && column[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                return (column[..(index + 1)], column[(index + 1)..].Trim());
            }

            return (column, string.Empty);
        }

        var space = column.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (column, string.Empty) : (column[..space], column[(space + 1)..].Trim());
    }

    /// <summary>A name as <c>quote_ident</c> wrote it, without its quotes, for the model's column comment map.</summary>
    public static string Unquote(string name)
        => name.Length >= 2 && name[0] == '"' && name[^1] == '"' ? name[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : name;
}
