using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PgProj.Parser;

/// <summary>
/// libpg_query is given the SQL as UTF-8 (G-125), so every position in its JSON tree (<c>location</c>, <c>stmt_location</c>, the list bounds,
/// <c>stmt_len</c>) counts UTF-8 bytes. The rest of pgproj indexes the .NET string, whose characters are UTF-16 code units, so the
/// tree is rewritten once, where it is read, to count those instead. For ASCII text the two agree and the tree is returned as is.
/// </summary>
public static partial class Utf8Offsets
{
    [GeneratedRegex("(?<!\\\\)\"(location|stmt_location|stmt_len|list_start|list_end|rexpr_list_start|rexpr_list_end)\":(-?[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetProperty();

    /// <summary>
    /// For each UTF-8 byte offset into <paramref name="sql"/> (0 to its byte count), the index of the character that byte belongs to;
    /// null when the text is all ASCII, where bytes and characters are the same.
    /// </summary>
    public static int[]? ByteToCharacterMap(string sql)
    {
        if (Ascii.IsValid(sql))
        {
            return null;
        }

        var map = new int[Encoding.UTF8.GetByteCount(sql) + 1];
        var bytePosition = 0;
        var index = 0;
        while (index < sql.Length)
        {
            var width = char.IsHighSurrogate(sql[index]) && index + 1 < sql.Length && char.IsLowSurrogate(sql[index + 1]) ? 2 : 1;
            var byteCount = Encoding.UTF8.GetByteCount(sql.AsSpan(index, width));
            for (var i = 0; i < byteCount; i++)
            {
                map[bytePosition + i] = index;
            }

            bytePosition += byteCount;
            index += width;
        }

        map[bytePosition] = sql.Length;
        return map;
    }

    /// <summary><paramref name="json"/> with its byte offsets in <paramref name="sql"/> turned into character indices.</summary>
    public static string? ToCharacterOffsets(string sql, string? json)
    {
        if (json is null || ByteToCharacterMap(sql) is not { } map)
        {
            return json;
        }

        var statementStart = 0;
        return OffsetProperty().Replace(json, match =>
        {
            var name = match.Groups[1].Value;
            if (!int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                || value < 0
                || value >= map.Length)
            {
                return match.Value;
            }

            int converted;
            if (name == "stmt_len")
            {
                // A length is converted through its end, from where its statement starts (the property before it).
                var end = Math.Min(map.Length - 1, statementStart + value);
                converted = map[end] - map[Math.Min(statementStart, map.Length - 1)];
            }
            else
            {
                converted = map[value];
                if (name == "stmt_location")
                {
                    statementStart = value;
                }
            }

            return $"\"{name}\":{converted.ToString(CultureInfo.InvariantCulture)}";
        });
    }
}
