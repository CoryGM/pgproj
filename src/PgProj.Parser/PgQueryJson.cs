using System.Text.Json;

namespace PgProj.Parser;

/// <summary>
/// How pgproj reads libpg_query's JSON parse trees (G-272). System.Text.Json refuses a document nested deeper than 64 levels by
/// default, which an ordinary statement passes (a sum of about 23 terms does); libpg_query returns trees thousands of levels deep before
/// it refuses a statement itself, and the reader is iterative, so every parse of a tree uses these options.
/// </summary>
public static class PgQueryJson
{
    /// <summary>Deeper than any tree libpg_query returns (its own stack limit stops it at about 20,000 levels).</summary>
    public const int MaxDocumentDepth = 1_000_000;

    /// <summary>
    /// The deepest statement pgproj reads. libpg_query refuses a statement long before this on its own (it measured about 20,000 levels at
    /// most), so this only stands between a deeper tree and the stack of <see cref="LargeStack"/>: such a statement is an error naming it,
    /// and the rest of its file is read.
    /// </summary>
    public const int MaxStatementDepth = 25_000;

    public static JsonDocumentOptions DocumentOptions { get; } = new() { MaxDepth = MaxDocumentDepth };

    public static JsonDocument Parse(string json) => JsonDocument.Parse(json, DocumentOptions);

    /// <summary>The nesting depth of a JSON value, counted without recursion.</summary>
    public static int DepthOf(JsonElement element) => DepthOf(element.GetRawText());

    /// <summary>The nesting depth of a JSON text, counted without recursion.</summary>
    public static int DepthOf(string json)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = MaxDocumentDepth });
        var depth = 0;
        while (reader.Read())
        {
            depth = Math.Max(depth, reader.CurrentDepth);
        }

        return depth;
    }
}
