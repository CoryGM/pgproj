namespace PgProj.Semantics;

/// <summary>
/// The comments on the columns of a view, a materialized view, or a composite type (G-216), by column name: <c>COMMENT ON COLUMN</c> names them the way it
/// names a table's. A column with no comment is not in the map, so <c>IS NULL</c> removes one.
/// </summary>
public static class ColumnCommentMap
{
    public static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary><paramref name="comments"/> with <paramref name="column"/>'s comment set, or removed when <paramref name="comment"/> is null.</summary>
    public static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> comments, string column, string? comment)
    {
        var result = new Dictionary<string, string>(comments, PgProj.Parser.SqlIdentifiers.NameComparer);
        if (comment is null)
        {
            result.Remove(column);
        }
        else
        {
            result[column] = comment;
        }

        return result;
    }

    /// <summary>True when both maps hold the same comments on the same columns.</summary>
    public static bool AreEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var other) && string.Equals(pair.Value, other, StringComparison.Ordinal));

    /// <summary>The columns whose comment differs between <paramref name="before"/> and <paramref name="after"/>, in name order, with the new comment (null for none).</summary>
    public static IReadOnlyList<(string Column, string? Comment)> Changes(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
        => [.. before.Keys.Concat(after.Keys)
            .Distinct(PgProj.Parser.SqlIdentifiers.NameComparer)
            .Order(StringComparer.Ordinal)
            .Select(column => (Column: column, Before: before.GetValueOrDefault(column), After: after.GetValueOrDefault(column)))
            .Where(static change => !string.Equals(change.Before, change.After, StringComparison.Ordinal))
            .Select(static change => (change.Column, change.After))];

    /// <summary>A short form of the map for a diff's details: <c>a: 'x'; b: 'y'</c>.</summary>
    public static string Describe(IReadOnlyDictionary<string, string> comments)
        => string.Join("; ", comments.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}: '{pair.Value}'"));
}
