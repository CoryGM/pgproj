namespace PgProj.Parser;

public enum SqlDiagnosticSeverity
{
    Warning,
    Error,
}

public sealed record SqlDiagnostic(
    SqlDiagnosticSeverity Severity,
    string Message,
    string? Source = null,
    int? LineNumber = null,
    int? CursorPosition = null)
{
    /// <summary>The 1-based column on <see cref="LineNumber"/>, in characters, when known (G-123).</summary>
    public int? Column { get; init; }

    /// <summary>
    /// The 1-based line where the diagnostic ends, and <see cref="EndColumn"/> the 1-based column just past its last character (an
    /// exclusive end, as an editor's range has it), when known (G-308). Both are set together, and only with a start.
    /// </summary>
    public int? EndLine { get; init; }

    /// <inheritdoc cref="EndLine"/>
    public int? EndColumn { get; init; }
}

/// <summary>Where a character offset falls in SQL text, as a 1-based line and column (G-123).</summary>
public static class SqlTextPosition
{
    /// <summary>
    /// The line and column of the character at the 1-based <paramref name="characterPosition"/>, counted in characters (Unicode code
    /// points) as PostgreSQL counts a syntax error's cursor position; null when the position is outside the text.
    /// </summary>
    public static (int Line, int Column)? OfCharacter(string sql, int characterPosition)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (characterPosition < 1)
        {
            return null;
        }

        var line = 1;
        var column = 1;
        var position = 1;
        foreach (var rune in sql.EnumerateRunes())
        {
            if (position == characterPosition)
            {
                return (line, column);
            }

            if (rune.Value == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }

            position++;
        }

        // PostgreSQL points one past the end for an error at the end of the text ("syntax error at end of input").
        return position == characterPosition ? (line, column) : null;
    }

    /// <summary>
    /// The UTF-16 index of the character at the 1-based <paramref name="characterPosition"/> (counted in Unicode code points, as
    /// <see cref="OfCharacter"/> counts it), or the length of the text for the position just past it; null when it is outside the text (G-308).
    /// </summary>
    public static int? IndexOfCharacter(string sql, int characterPosition)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (characterPosition < 1)
        {
            return null;
        }

        var index = 0;
        var position = 1;
        foreach (var rune in sql.EnumerateRunes())
        {
            if (position == characterPosition)
            {
                return index;
            }

            index += rune.Utf16SequenceLength;
            position++;
        }

        return position == characterPosition ? sql.Length : null;
    }

    /// <summary>The line and column of the UTF-16 index <paramref name="index"/> in <paramref name="sql"/>.</summary>
    public static (int Line, int Column) OfIndex(string sql, int index)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var end = Math.Clamp(index, 0, sql.Length);

        // A line is found by binary search in the text's line starts, which are made once for the text: counting from the start of the text for every
        // diagnostic is quadratic in a file with many of them (G-315). The column is the number of characters before the index on its line, less one
        // for each surrogate pair wholly among them (a pair is one code point; a lone surrogate counts as one, as EnumerateRunes counts it), the pairs
        // found by binary search too: counting a line's characters one by one is quadratic in a line with many positions on it, such as a table
        // written on one line (G-322).
        var (lineStarts, pairStarts) = IndexOf(sql);
        var line = CountAtOrBefore(lineStarts, end);
        var lineStart = lineStarts[line - 1];
        var pairs = CountAtOrBefore(pairStarts, end - 2) - CountAtOrBefore(pairStarts, lineStart - 1);
        return (line, end - lineStart - pairs + 1);
    }

    /// <summary>How many of the ascending <paramref name="values"/> are at most <paramref name="limit"/>.</summary>
    private static int CountAtOrBefore(int[] values, int limit)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (values[middle] <= limit)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    // The positions of one text are asked for one after another (every diagnostic of a file), so the index of the last text asked about is kept, on
    // this thread: nothing outlives the parse.
    [ThreadStatic]
    private static (string Sql, int[] LineStarts, int[] PairStarts)? s_lastIndex;

    /// <summary>
    /// The index of the first character of each line of <paramref name="sql"/> (0, then the index after each <c>\n</c>), and of the first character of
    /// each surrogate pair (G-322).
    /// </summary>
    private static (int[] LineStarts, int[] PairStarts) IndexOf(string sql)
    {
        if (s_lastIndex is { } last && ReferenceEquals(last.Sql, sql))
        {
            return (last.LineStarts, last.PairStarts);
        }

        var starts = new List<int> { 0 };
        for (var position = sql.IndexOf('\n'); position >= 0; position = sql.IndexOf('\n', position + 1))
        {
            starts.Add(position + 1);
        }

        var pairs = new List<int>();
        for (var position = 0; position < sql.Length - 1; position++)
        {
            if (char.IsHighSurrogate(sql[position]) && char.IsLowSurrogate(sql[position + 1]))
            {
                pairs.Add(position);
                position++;
            }
        }

        TextPassCounter.CountLineIndex();
        s_lastIndex = (sql, starts.ToArray(), pairs.ToArray());
        return (s_lastIndex.Value.LineStarts, s_lastIndex.Value.PairStarts);
    }
}
