using System.Text;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-315: the line and column of a position in a file used to be counted from the start of the file for every diagnostic, which is quadratic in a
/// file with many of them (20,000 unmodeled statements took 23 seconds). They are found from the text's line starts, made once. These check that
/// the answer is the one the old count gave at every index of text with the awkward cases, and that a file of warnings is positioned from one
/// line index (counted, not timed: the tests are deterministic).
/// </summary>
[TestClass]
public sealed class TextPositionTests
{
    private static readonly string[] Texts =
    [
        "",
        "\n",
        "SELECT 1;",
        "a\nb\n\nc",
        "line one\r\nline two\r\n\r\nlast",
        "trailing newline\n",
        "emoji 😀 and 𝒳 here\nsecond 😀😀 line\n😀",
        "tabs\tand ü ö\n\tindented\n",

        // G-322: the column counts surrogate pairs by binary search, so lone surrogates, a high one before a pair, and pairs at a line's ends.
        "lone high \uD83D here\nlone low \uDE00 there\n",
        "high before a pair \uD83D😀 x\n😀start\nend😀",
        "\uDE00\uD83D",
    ];

    [TestMethod]
    public void EveryIndexIsPositionedAsTheCountFromTheStartPositionedIt()
    {
        foreach (var text in Texts)
        {
            for (var index = -2; index <= text.Length + 2; index++)
            {
                Assert.AreEqual(CountFromTheStart(text, index), SqlTextPosition.OfIndex(text, index), $"index {index} of: {text.ReplaceLineEndings("\\n")}");
            }
        }
    }

    [TestMethod]
    public void TwoTextsAskedAboutInTurnAreEachPositionedCorrectly()
    {
        const string first = "a\nbb\nccc";
        const string second = "x\n\ny";

        Assert.AreEqual((3, 2), SqlTextPosition.OfIndex(first, 6));
        Assert.AreEqual((3, 1), SqlTextPosition.OfIndex(second, 3));
        Assert.AreEqual((3, 2), SqlTextPosition.OfIndex(first, 6));
    }

    [TestMethod]
    public void AFileOfManyWarningsIsPositionedFromOneLineIndex()
    {
        var sql = string.Concat(Enumerable.Range(0, 400).Select(static i => $"INSERT INTO t VALUES ({i});\n"));
        var counter = TextPassCounter.Start();

        var tree = new PgQueryParser().Parse(sql);

        Assert.HasCount(400, tree.Diagnostics);
        Assert.IsLessThan(5, counter.LineIndexes, $"the line index was made {counter.LineIndexes} times");
        var last = tree.Diagnostics[^1];
        Assert.AreEqual(400, last.LineNumber);
        Assert.AreEqual(1, last.Column);
        Assert.AreEqual(400, last.EndLine);
    }

    /// <summary>What <c>OfIndex</c> did before: count the characters from the start of the text.</summary>
    private static (int Line, int Column) CountFromTheStart(string sql, int index)
    {
        var end = Math.Clamp(index, 0, sql.Length);
        var line = 1;
        var column = 1;
        foreach (var rune in sql.AsSpan(0, end).EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }
}
