namespace PgProj.Parser;

/// <summary>
/// Counts the passes over a statement's text (PostgreSQL's lexer, and the tokenizer of the older text readers) made under the counter a test
/// started, so a test can tell that a statement is read in one pass and not once per column or expression (G-310). A test cannot time it
/// (the tests are deterministic), and a parse runs on a thread of its own, so the counter flows with the execution context to that thread.
/// Nothing is counted unless a test started one.
/// </summary>
internal sealed class TextPassCounter
{
    private static readonly AsyncLocal<TextPassCounter?> s_current = new();

    private int _scans;
    private int _tokenizations;
    private int _lineIndexes;

    /// <summary>The runs of PostgreSQL's lexer (<c>SourceText.Scan</c>).</summary>
    public int Scans => _scans;

    /// <summary>The runs of the older readers' tokenizer (<c>ColumnDefaultTextReader.Tokenize</c> when it did not reuse the last statement's tokens).</summary>
    public int Tokenizations => _tokenizations;

    /// <summary>The line indexes made to turn positions in a text into lines and columns (<c>SqlTextPosition.OfIndex</c>, G-315).</summary>
    public int LineIndexes => _lineIndexes;

    /// <summary>Starts counting for the code that runs from here on in this execution context, and the threads it starts.</summary>
    public static TextPassCounter Start()
    {
        var counter = new TextPassCounter();
        s_current.Value = counter;
        return counter;
    }

    public static void CountScan()
    {
        if (s_current.Value is { } counter)
        {
            Interlocked.Increment(ref counter._scans);
        }
    }

    public static void CountLineIndex()
    {
        if (s_current.Value is { } counter)
        {
            Interlocked.Increment(ref counter._lineIndexes);
        }
    }

    public static void CountTokenization()
    {
        if (s_current.Value is { } counter)
        {
            Interlocked.Increment(ref counter._tokenizations);
        }
    }
}
