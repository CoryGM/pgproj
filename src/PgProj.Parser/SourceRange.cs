namespace PgProj.Parser;

/// <summary>
/// Where something was written in its file (G-309e): the 1-based line and column of its first character, and the line and column just past its last
/// (an exclusive end, as an editor's range has it, G-308). The file is the syntax tree's.
/// </summary>
public sealed record SourceRange(int Line, int Column, int EndLine, int EndColumn)
{
    /// <summary>The range from the UTF-16 index <paramref name="startIndex"/> to <paramref name="endIndex"/> of <paramref name="sql"/>; an end before the start is the start.</summary>
    public static SourceRange Of(string sql, int startIndex, int endIndex)
    {
        ArgumentNullException.ThrowIfNull(sql);
        startIndex = Math.Clamp(startIndex, 0, sql.Length);
        var (line, column) = SqlTextPosition.OfIndex(sql, startIndex);
        var (endLine, endColumn) = SqlTextPosition.OfIndex(sql, Math.Clamp(endIndex, startIndex, sql.Length));
        return new SourceRange(line, column, endLine, endColumn);
    }
}

/// <summary>
/// Holds a place on a record without making it part of the record's equality (G-309e): two nodes or definitions that differ only in where they were
/// written are equal, so a place can never make the model, the diff, or a comparison differ.
/// </summary>
/// <typeparam name="T">What is held: a <see cref="SourceRange"/> on a parse-tree node, a file and range on a definition.</typeparam>
public sealed class PlaceSlot<T> : IEquatable<PlaceSlot<T>>
    where T : class
{
    public static readonly PlaceSlot<T> Empty = new(null);

    public PlaceSlot(T? value) => Value = value;

    public T? Value { get; }

    /// <summary>Always true: a place is never compared.</summary>
    public bool Equals(PlaceSlot<T>? other) => other is not null;

    public override bool Equals(object? obj) => obj is PlaceSlot<T>;

    public override int GetHashCode() => 0;
}
