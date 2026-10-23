using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    // The text being parsed, the statement being read, and where its diagnostics go (G-207). Set by MapNodes for the length of one
    // parse, so that ReadExpressionText can find an expression's own text without every reader passing the statement down.
    [ThreadStatic]
    private static ParseContext? s_context;

    private sealed class ParseContext(string sql, ICollection<SqlDiagnostic> diagnostics)
    {
        public string Sql { get; } = sql;

        public ICollection<SqlDiagnostic> Diagnostics { get; } = diagnostics;

        public string StatementSql { get; set; } = sql;

        public int StatementOffset { get; set; }

        /// <summary>The places a near-miss rename annotation was warned about (G-285): a column can be read more than once in a parse.</summary>
        public HashSet<int> WarnedRenameComments { get; } = [];
    }

    /// <summary>
    /// The text the author wrote for <paramref name="expression"/> (G-207): what is deployed is what the project wrote, and a normalized
    /// form exists only for comparison. When it cannot be read from the statement (<see cref="SourceText"/> finds no text whose parse is
    /// the expression's tree), the expression is rebuilt from the tree instead, and a warning says so, naming where it is.
    /// </summary>
    internal static string? ReadExpressionText(JsonElement expression)
        => s_context is { } context
            ? ReadExpressionText(context.StatementSql, context.StatementOffset, expression, context.Sql, context.Diagnostics)
            : TryRenderSqlFragment(expression);

    /// <inheritdoc cref="ReadExpressionText(JsonElement)"/>
    internal static string? ReadExpressionText(string statementSql, int statementOffset, JsonElement expression)
        => s_context is { } context
            ? ReadExpressionText(statementSql, statementOffset, expression, context.Sql, context.Diagnostics)
            : ReadExpressionText(statementSql, statementOffset, expression, statementSql, diagnostics: null);

    /// <summary>
    /// <inheritdoc cref="ReadExpressionText(JsonElement)"/> <paramref name="sql"/> is the whole text the tree's positions count from, for
    /// the warning's line and column, and the warning goes to <paramref name="diagnostics"/>.
    /// </summary>
    internal static string? ReadExpressionText(string statementSql, int statementOffset, JsonElement expression, string sql, ICollection<SqlDiagnostic>? diagnostics)
    {
        if (SourceText.Read(statementSql, statementOffset, expression) is { } written)
        {
            return written;
        }

        var rebuilt = TryRenderSqlFragment(expression);
        if (rebuilt is not null && diagnostics is not null)
        {
            var message = $"The text of an expression could not be read as written, so it is deployed as pgproj rebuilt it, which may differ from what was written: {rebuilt}";
            diagnostics.Add(FirstPosition(expression) is { } first
                ? LocatedAt(SqlDiagnosticSeverity.Warning, message, nameof(PgQueryParser), sql, first, StatementEndIndex(sql, statementOffset, statementSql, first))
                : new SqlDiagnostic(SqlDiagnosticSeverity.Warning, message, nameof(PgQueryParser)));
        }

        return rebuilt;
    }

    /// <summary>
    /// The end of the statement of <paramref name="statementSql"/> that starts at <paramref name="statementOffset"/> in <paramref name="sql"/>, not
    /// before <paramref name="from"/> (G-308). The parse tree has a start for an expression and no end, so a warning about one that could not
    /// be read as written is placed from where it starts to the end of its statement.
    /// </summary>
    private static int StatementEndIndex(string sql, int statementOffset, string statementSql, int from)
        => UnmodeledStatementReader.TrimStatementEnd(sql, from, Math.Min(sql.Length, statementOffset + statementSql.Length));

    /// <summary>
    /// The text the author wrote for a type name (G-207): <c>VARCHAR(10)</c>, <c>"My S"."My T"</c>, <c>"char"</c>, which is what is deployed;
    /// the type's normalized spelling is kept beside it for comparison and identity. Null when it cannot be read: the caller then deploys
    /// pgproj's own spelling, and a warning says so (not for a <c>%TYPE</c> reference, which is not a type name and has no other text).
    /// </summary>
    internal static string? ReadTypeText(JsonElement typeName)
        => s_context is { } context ? ReadTypeText(context.StatementSql, context.StatementOffset, typeName) : null;

    /// <inheritdoc cref="ReadTypeText(JsonElement)"/>
    internal static string? ReadTypeText(string statementSql, int statementOffset, JsonElement typeName)
    {
        if (SourceText.ReadTypeName(statementSql, statementOffset, typeName) is { } written)
        {
            return written;
        }

        if (!typeName.TryGetProperty("pct_type", out _) && s_context is { } context)
        {
            const string message = "The text of a type could not be read as written, so it is deployed in pgproj's own spelling, which may differ from what was written.";
            context.Diagnostics.Add(FirstPosition(typeName) is { } first
                ? LocatedAt(SqlDiagnosticSeverity.Warning, message, nameof(PgQueryParser), context.Sql, first, StatementEndIndex(context.Sql, statementOffset, statementSql, first))
                : new SqlDiagnostic(SqlDiagnosticSeverity.Warning, message, nameof(PgQueryParser)));
        }

        return null;
    }

    /// <summary>
    /// Every type name in <paramref name="statement"/>, keyed by its normalized dotted name (<see cref="RenderCastTypeName"/>), to the text
    /// the author wrote for it (G-207): for a cast, an operator, or an aggregate, whose types are kept by their normalized names.
    /// </summary>
    private static IReadOnlyDictionary<string, string> CollectWrittenTypes(JsonElement statement)
    {
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        Visit(statement);
        return written;

        void Visit(JsonElement current)
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in current.EnumerateObject())
                {
                    if (property.Name is "TypeName" or "typeName" or "argType" or "sourcetype" or "targettype" && property.Value.ValueKind == JsonValueKind.Object
                        && ReadTypeText(property.Value) is { } text)
                    {
                        written.TryAdd(RenderCastTypeName(property.Value), text);
                    }

                    Visit(property.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
    }

    private static int? FirstPosition(JsonElement element)
    {
        int? first = null;
        Visit(element);
        return first;

        void Visit(JsonElement current)
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in current.EnumerateObject())
                {
                    if (property.Name == "location" && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var location) && location >= 0)
                    {
                        first = first is null ? location : Math.Min(first.Value, location);
                    }
                    else
                    {
                        Visit(property.Value);
                    }
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
    }
}
