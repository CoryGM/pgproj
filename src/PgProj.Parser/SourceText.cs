using System.Text;
using System.Text.Json;

namespace PgProj.Parser;

/// <summary>
/// The author's own text for an expression in the parse tree (G-207). What is deployed is what the project wrote; a normalized form
/// exists only for comparison. pg_query gives an expression's nodes their start positions but not where the expression ends, so the
/// text is found this way:
/// <list type="number">
/// <item>it starts at the earliest position of any node in the expression, or at a token just before it;</item>
/// <item>PostgreSQL's own lexer (<c>pg_query_scan</c>) gives the token boundaries after the last node's position;</item>
/// <item>each candidate is parsed again, as <c>SELECT &lt;candidate&gt;</c>, and the first whose tree is the expression's own tree
/// (positions aside) is the text.</item>
/// </list>
/// So a slice is never a guess: PostgreSQL's parser confirms it. When no candidate matches, the result is null and the caller falls
/// back to the rebuilt text, with a warning.
/// </summary>
internal static class SourceText
{
    private const int MaximumCandidates = 64;
    private const int MaximumEarlierTokens = 8;

    /// <summary>
    /// The text of <paramref name="expression"/> (a node such as <c>{"A_Expr": {...}}</c>) in <paramref name="statementSql"/>, whose
    /// first character is at <paramref name="statementOffset"/> in the text the tree's positions count from; null when it cannot be found.
    /// </summary>
    public static string? Read(string statementSql, int statementOffset, JsonElement expression)
        => Read(statementSql, statementOffset, expression, Kind.Expression);

    /// <summary>
    /// The text of a type name (a <c>TypeName</c> node's content, such as <c>{"names": [...], "typmods": [...]}</c>) as the author wrote it:
    /// <c>VARCHAR(10)</c>, <c>"My S"."My T"[]</c>, <c>double precision</c>. A candidate is confirmed by parsing <c>SELECT NULL::&lt;candidate&gt;</c>.
    /// A <c>SETOF</c> is not part of the text. Null when it cannot be found, or for a <c>%TYPE</c> reference, which is not a type name.
    /// </summary>
    public static string? ReadTypeName(string statementSql, int statementOffset, JsonElement typeName)
        => typeName.TryGetProperty("pct_type", out _) ? null : Read(statementSql, statementOffset, typeName, Kind.TypeName);

    private enum Kind
    {
        Expression,
        TypeName,
    }

    private static string? Read(string statementSql, int statementOffset, JsonElement node, Kind kind)
    {
        ArgumentNullException.ThrowIfNull(statementSql);
        var locations = new List<int>();
        CollectLocations(node, locations);
        if (locations.Count == 0)
        {
            return null;
        }

        var first = locations.Min() - statementOffset;
        var last = locations.Max() - statementOffset;
        if (first < 0 || last >= statementSql.Length || first > last)
        {
            return null;
        }

        var expected = WithoutLocations(node, kind);
        var tokens = TokensOf(statementSql);
        foreach (var start in StartCandidates(statementSql, tokens, first, kind))
        {
            if (FindEnd(statementSql, tokens, start, last, expected, kind) is { } text)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>
    /// The expression's first node, then the start of each token written just before it, nearest first. A node's position is not always
    /// its first token's (<c>ARRAY[...]</c> is at the bracket), and an opening parenthesis has no node; a token that is not part of the
    /// expression gives a different tree, so it never matches.
    /// </summary>
    private static IEnumerable<int> StartCandidates(string sql, StatementTokens tokens, int first, Kind kind)
    {
        yield return first;

        // RETURNS SETOF type: the type's position may be SETOF's, and SETOF is not part of the type.
        if (kind == Kind.TypeName)
        {
            var after = TokensFrom(sql, tokens, first).Take(2).ToList();
            if (after.Count > 1 && string.Equals(sql[after[0].Start..after[0].End], "setof", StringComparison.OrdinalIgnoreCase))
            {
                yield return after[1].Start;
            }
        }

        foreach (var start in StartsBefore(sql, tokens, first))
        {
            yield return start;
        }
    }

    private static string? FindEnd(string sql, StatementTokens statementTokens, int start, int last, string expected, Kind kind)
    {
        var depth = 0;
        var candidates = 0;
        foreach (var (tokenStart, tokenEnd) in TokensFrom(sql, statementTokens, start))
        {
            // A bracket, a semicolon, and a comma are one character; no other token is one of them.
            var token = tokenEnd - tokenStart == 1 ? sql[tokenStart] : '\0';
            if (token is '(' or '[')
            {
                depth++;
            }
            else if (token is ')' or ']')
            {
                depth--;
                if (depth < 0)
                {
                    return null;
                }
            }
            else if (depth == 0 && token is ';')
            {
                return null;
            }

            if (tokenStart < last || depth > 0)
            {
                continue;
            }

            var candidate = sql[start..tokenEnd];
            if (Matches(candidate, expected, kind))
            {
                return candidate;
            }

            if (++candidates >= MaximumCandidates || (depth == 0 && token is ','))
            {
                return null;
            }
        }

        return null;
    }

    // One statement's text is read many times (a table's every column has a type and perhaps a default, and each is an expression to find),
    // and lexing it for each would make the work quadratic in its length (G-310). The lexer runs once per statement text, the last one
    // read on this thread (a statement's reads follow one another), and every read finds its tokens by position.
    [ThreadStatic]
    private static (string Sql, StatementTokens Tokens)? s_lastStatement;

    private static StatementTokens TokensOf(string statementSql)
    {
        if (s_lastStatement is { } last && ReferenceEquals(last.Sql, statementSql))
        {
            return last.Tokens;
        }

        var tokens = new StatementTokens(Scan(statementSql));
        s_lastStatement = (statementSql, tokens);
        return tokens;
    }

    /// <summary>The tokens of a whole statement, from one run of the lexer; null tokens when it reported an error.</summary>
    private sealed class StatementTokens(IReadOnlyList<(int Start, int End)>? tokens)
    {
        public IReadOnlyList<(int Start, int End)>? Tokens { get; } = tokens;

        /// <summary>
        /// Whether <paramref name="position"/> is between two tokens or at the start of one, so that lexing the text from there, or up to
        /// there, gives these tokens: <paramref name="index"/> is that of the first token that ends after it. A position inside a token is
        /// where a lexer started at the position would find a different token, so the caller lexes that text itself.
        /// </summary>
        public bool TryAlign(int position, out int index)
        {
            index = 0;
            if (Tokens is null)
            {
                return false;
            }

            var low = 0;
            var high = Tokens.Count;
            while (low < high)
            {
                var middle = (low + high) >>> 1;
                if (Tokens[middle].End > position)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            index = low;
            return low == Tokens.Count || Tokens[low].Start >= position;
        }
    }

    /// <summary>
    /// The tokens of the statement <paramref name="sql"/> from <paramref name="position"/> on, as character ranges in it, from the one run
    /// of the lexer over the statement, so a reader that asks for them once per column does not make the work quadratic (G-310). The same
    /// tokens as <c>Scan(sql[position..])</c> gives, shifted by the position. Read lazily.
    /// </summary>
    internal static IEnumerable<(int Start, int End)> TokensFrom(string sql, int position)
        => TokensFrom(sql, TokensOf(sql), position);

    /// <summary>
    /// <see cref="TokensFrom(string, int)"/>, but null when the lexer cannot read the text from <paramref name="position"/>, as <see cref="Scan"/>
    /// is null for it: a reader that treats that as "not found" (and not as a statement with no more tokens) uses this.
    /// </summary>
    internal static IEnumerable<(int Start, int End)>? TokensFromOrNull(string sql, int position)
        => TokensFromOrNull(sql, TokensOf(sql), position);

    private static IEnumerable<(int Start, int End)> TokensFrom(string sql, StatementTokens statementTokens, int position)
        => TokensFromOrNull(sql, statementTokens, position) ?? [];

    /// <summary>The tokens of the text from <paramref name="position"/> on, as character ranges in <paramref name="sql"/>.</summary>
    private static IEnumerable<(int Start, int End)>? TokensFromOrNull(string sql, StatementTokens statementTokens, int position)
    {
        if (statementTokens.TryAlign(position, out var index))
        {
            return Following(statementTokens.Tokens!, index);
        }

        return Scan(sql[position..]) is { } scanned ? scanned.Select(token => (position + token.Start, position + token.End)) : null;

        static IEnumerable<(int Start, int End)> Following(IReadOnlyList<(int Start, int End)> tokens, int first)
        {
            for (var index = first; index < tokens.Count; index++)
            {
                yield return tokens[index];
            }
        }
    }

    /// <summary>The start of each of the last <see cref="MaximumEarlierTokens"/> tokens before <paramref name="position"/> in the statement <paramref name="sql"/>, nearest first.</summary>
    internal static IEnumerable<int> StartsBefore(string sql, int position)
        => StartsBefore(sql, TokensOf(sql), position);

    private static IEnumerable<int> StartsBefore(string sql, StatementTokens statementTokens, int position)
    {
        if (statementTokens.TryAlign(position, out var index))
        {
            for (var earlier = index - 1; earlier >= 0 && earlier >= index - MaximumEarlierTokens; earlier--)
            {
                yield return statementTokens.Tokens![earlier].Start;
            }

            yield break;
        }

        if (Scan(sql[..position]) is not { } before)
        {
            yield break;
        }

        for (var earlier = before.Count - 1; earlier >= 0 && earlier >= before.Count - MaximumEarlierTokens; earlier--)
        {
            yield return before[earlier].Start;
        }
    }

    private static bool Matches(string candidate, string expected, Kind kind)
    {
        PgQueryInterop.NativeParseResult result = default;
        var allocated = false;
        try
        {
            result = PgQueryInterop.Parse(kind == Kind.TypeName ? "SELECT NULL::" + candidate : "SELECT " + candidate);
            allocated = true;
            if (result.Error != IntPtr.Zero || PgQueryInterop.ReadNullableUtf8String(result.ParseTree) is not { } json)
            {
                return false;
            }

            using var document = PgQueryJson.Parse(json);
            if (!document.RootElement.TryGetProperty("stmts", out var statements)
                || statements.GetArrayLength() != 1
                || !statements[0].TryGetProperty("stmt", out var statement)
                || !statement.TryGetProperty("SelectStmt", out var select))
            {
                return false;
            }

            // Only a target list: anything else (FROM, WHERE, a second target, an alias) means the candidate ran past the expression.
            foreach (var property in select.EnumerateObject())
            {
                if (property.Name is not ("targetList" or "limitOption" or "op"))
                {
                    return false;
                }
            }

            if (!select.TryGetProperty("targetList", out var targets)
                || targets.GetArrayLength() != 1
                || !targets[0].TryGetProperty("ResTarget", out var target)
                || target.TryGetProperty("name", out _)
                || !target.TryGetProperty("val", out var value))
            {
                return false;
            }

            if (kind == Kind.TypeName)
            {
                return value.TryGetProperty("TypeCast", out var cast)
                    && cast.TryGetProperty("typeName", out var castType)
                    && string.Equals(WithoutLocations(castType, kind), expected, StringComparison.Ordinal);
            }

            return string.Equals(WithoutLocations(value, kind), expected, StringComparison.Ordinal);
        }
        finally
        {
            if (allocated)
            {
                PgQueryInterop.FreeParseResult(result);
            }
        }
    }

    /// <summary>The tokens of <paramref name="text"/> as character ranges, from PostgreSQL's lexer; null when it reports an error.</summary>
    internal static IReadOnlyList<(int Start, int End)>? Scan(string text)
    {
        TextPassCounter.CountScan();
        PgQueryInterop.NativeProtobufParseResult result = default;
        var allocated = false;
        try
        {
            result = PgQueryInterop.Scan(text);
            allocated = true;
            if (result.Error != IntPtr.Zero)
            {
                return null;
            }

            var map = Utf8Offsets.ByteToCharacterMap(text);
            var tokens = new List<(int Start, int End)>();
            foreach (var (start, end) in ReadScanTokens(PgQueryInterop.ReadBytes(result.ParseTree)))
            {
                tokens.Add(map is null ? (start, end) : (map[start], map[end]));
            }

            return tokens;
        }
        finally
        {
            if (allocated)
            {
                PgQueryInterop.FreeScanResult(result);
            }
        }
    }

    // ScanResult { int32 version = 1; repeated ScanToken tokens = 2; }, ScanToken { int32 start = 1; int32 end = 2; Token token = 4;
    // KeywordKind keyword_kind = 5; }: only the offsets are needed.
    private static IEnumerable<(int Start, int End)> ReadScanTokens(byte[] bytes)
    {
        var tokens = new List<(int Start, int End)>();
        var position = 0;
        while (position < bytes.Length)
        {
            var (field, wireType) = ReadKey(bytes, ref position);
            if (field == 2 && wireType == 2)
            {
                var length = (int)ReadVarint(bytes, ref position);
                var end = position + length;
                int tokenStart = 0, tokenEnd = 0;
                while (position < end)
                {
                    var (tokenField, tokenWireType) = ReadKey(bytes, ref position);
                    if (tokenWireType == 0)
                    {
                        var value = (int)ReadVarint(bytes, ref position);
                        if (tokenField == 1)
                        {
                            tokenStart = value;
                        }
                        else if (tokenField == 2)
                        {
                            tokenEnd = value;
                        }
                    }
                    else
                    {
                        Skip(bytes, ref position, tokenWireType);
                    }
                }

                tokens.Add((tokenStart, tokenEnd));
            }
            else
            {
                Skip(bytes, ref position, wireType);
            }
        }

        return tokens;
    }

    private static (int Field, int WireType) ReadKey(byte[] bytes, ref int position)
    {
        var key = ReadVarint(bytes, ref position);
        return ((int)(key >> 3), (int)(key & 7));
    }

    private static ulong ReadVarint(byte[] bytes, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; position < bytes.Length; shift += 7)
        {
            var current = bytes[position++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                break;
            }
        }

        return value;
    }

    private static void Skip(byte[] bytes, ref int position, int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint(bytes, ref position);
                break;
            case 1:
                position += 8;
                break;
            case 2:
                position += (int)ReadVarint(bytes, ref position);
                break;
            case 5:
                position += 4;
                break;
            default:
                position = bytes.Length;
                break;
        }
    }

    private static void CollectLocations(JsonElement element, List<int> locations)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsPosition(property.Name) && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var location) && location >= 0)
                    {
                        locations.Add(location);
                    }
                    else
                    {
                        CollectLocations(property.Value, locations);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectLocations(item, locations);
                }

                break;
        }
    }

    /// <summary>
    /// A property that holds a position in the text rather than part of the expression: <c>location</c>, and the <c>list_start</c> and
    /// <c>list_end</c> PostgreSQL 18 gives a list such as <c>ARRAY[...]</c>'s.
    /// </summary>
    private static bool IsPosition(string propertyName)
        => propertyName is "location" or "list_start" or "list_end" || propertyName.EndsWith("_location", StringComparison.Ordinal);

    /// <summary>The node as JSON with every position left out, for comparing two trees of the same text.</summary>
    private static string WithoutLocations(JsonElement element, Kind kind)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, element, kind == Kind.TypeName);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());

        // A type's SETOF is part of a routine's result, not of the type, and NULL::type has none.
        static void Write(Utf8JsonWriter writer, JsonElement element, bool skipSetof)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        if (IsPosition(property.Name) || (skipSetof && property.Name == "setof"))
                        {
                            continue;
                        }

                        writer.WritePropertyName(property.Name);
                        Write(writer, property.Value, skipSetof: false);
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        Write(writer, item, skipSetof: false);
                    }

                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
