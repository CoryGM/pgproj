using static PgProj.Parser.ColumnDefaultTextReader;

namespace PgProj.Parser;

/// <summary>
/// Reads a partition's <c>FOR VALUES ...</c> or <c>DEFAULT</c> bound as the author wrote it (G-24c), the same verbatim-slicing
/// reasoning as <see cref="ConstraintIndexTextReader"/> (G-73): the bound's datums are ordinary expressions, and re-rendering them
/// from the parsed tree risks the same silent corruption that bug had. pg_query's <c>location</c> for the bound points at the
/// clause's first token after <c>FOR VALUES</c> (or at <c>DEFAULT</c> itself, which has no <c>FOR VALUES</c> prefix), and the shape
/// that follows is fixed by the strategy, so it is walked directly rather than guessed at with a generic stop condition: a bound
/// can itself contain a further clause's keyword as data (<c>FOR VALUES IN ('with')</c> is valid), so scanning for "the next
/// clause keyword" is not a safe way to find where the bound ends.
/// </summary>
internal static class PartitionBoundTextReader
{
    /// <summary>
    /// The bound clause starting at <paramref name="location"/> (already adjusted to be relative to <paramref name="statementSql"/>).
    /// <paramref name="strategy"/> is the partition strategy code pg_query gives the bound (<c>"r"</c> for range, <c>"l"</c> for list,
    /// <c>"h"</c> for hash), or null for a <c>DEFAULT</c> partition (a single keyword, no further shape to walk).
    /// </summary>
    public static string? TryRead(string statementSql, int location, string? strategy)
    {
        if (string.IsNullOrEmpty(statementSql) || location < 0 || location >= statementSql.Length)
        {
            return null;
        }

        var tokens = Tokenize(statementSql);
        var start = tokens.FindIndex(token => token.Start >= location);
        if (start < 0)
        {
            return null;
        }

        var end = strategy switch
        {
            null => start,
            "h" => ConsumeKeywordAndGroup(statementSql, tokens, start, "with"),
            "l" => ConsumeKeywordAndGroup(statementSql, tokens, start, "in"),
            "r" => ConsumeRangeBound(statementSql, tokens, start),
            _ => -1,
        };

        if (end < 0)
        {
            return null;
        }

        // Every non-DEFAULT bound is introduced by "FOR VALUES", which PostgreSQL's grammar requires — it is not optional
        // punctuation the way, say, a CHECK constraint's own name is. pg_query's location points just past it, at the bound's
        // own first token, so it is included here by stepping back two tokens; without it, "PARTITION OF parent WITH (...)"
        // reaches the server missing the words that make it valid syntax (found via the Phase 6 Docker oracle: a hand-checked
        // unit test expectation had the same omission and could not catch it, since it never ran against a real server).
        var sliceStart = start;
        if (strategy is not null)
        {
            var forIndex = start - 2;
            if (forIndex >= 0 && IsWord(statementSql, tokens[forIndex], "for") && IsWord(statementSql, tokens[forIndex + 1], "values"))
            {
                sliceStart = forIndex;
            }
        }

        return statementSql[tokens[sliceStart].Start..tokens[end].End].Trim();
    }

    /// <summary><c>FROM (...) TO (...)</c>: the two halves of a range bound.</summary>
    private static int ConsumeRangeBound(string sql, List<Token> tokens, int start)
    {
        var afterFrom = ConsumeKeywordAndGroup(sql, tokens, start, "from");
        if (afterFrom < 0)
        {
            return -1;
        }

        var toIndex = afterFrom + 1;
        return toIndex < tokens.Count && IsWord(sql, tokens[toIndex], "to")
            ? ConsumeKeywordAndGroup(sql, tokens, toIndex, "to")
            : -1;
    }

    /// <summary>Verifies the token at <paramref name="index"/> is <paramref name="keyword"/>, then the balanced group that follows it; returns the closing <c>)</c>'s index.</summary>
    private static int ConsumeKeywordAndGroup(string sql, List<Token> tokens, int index, string keyword)
    {
        if (index >= tokens.Count || !IsWord(sql, tokens[index], keyword))
        {
            return -1;
        }

        var open = index + 1;
        if (open >= tokens.Count || !IsPunctuation(sql, tokens[open], '('))
        {
            return -1;
        }

        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (IsPunctuation(sql, tokens[i], '('))
            {
                depth++;
            }
            else if (IsPunctuation(sql, tokens[i], ')') && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
