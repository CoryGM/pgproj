using System.Text.Json;
using Npgsql;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>
/// Gives generated columns a <see cref="ColumnDefinition.ServerRenderedGeneratedExpression"/> (G-97). PostgreSQL stores a
/// generated column's expression with the implicit casts it inserted, so a project column's expression is compared by adding
/// it as a generated column to a temporary copy of the live table (<c>LIKE</c> copies the live columns as ordinary ones) and
/// reading back PostgreSQL's rendering. Everything happens in a transaction that is always rolled back.
/// </summary>
public sealed class ServerGeneratedExpressionRenderer
{
    private const string RenderedColumn = "pgproj_generated";

    private readonly PgQueryParser _parser;

    public ServerGeneratedExpressionRenderer(PgQueryParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parser = parser;
    }

    /// <summary>
    /// Sets <see cref="ColumnDefinition.ServerRenderedGeneratedExpression"/> on each generated column of <paramref name="projectModel"/>
    /// whose live column is generated too. An expression the server rejects (it uses a column the live table doesn't have yet) is
    /// left without one and compared as before.
    /// </summary>
    public async Task RenderAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(projectModel);
        ArgumentNullException.ThrowIfNull(liveModel);

        var candidates = new List<(TableDefinition LiveTable, ColumnDefinition Column)>();
        foreach (var table in projectModel.Tables.Values.OrderBy(static table => table.Key, StringComparer.Ordinal))
        {
            if (!liveModel.Tables.TryGetValue(table.Key, out var liveTable))
            {
                continue;
            }

            foreach (var column in table.Columns)
            {
                var liveColumn = liveTable.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, column.Name, StringComparison.Ordinal));
                if (column.IsGenerated
                    && !string.IsNullOrWhiteSpace(column.GeneratedExpression)
                    && liveColumn is { IsGenerated: true, ServerRenderedGeneratedExpression: not null }
                    && IsGeneratedExpression(column.GeneratedExpression))
                {
                    candidates.Add((liveTable, column));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var renderings = new Dictionary<string, string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                for (var index = 0; index < candidates.Count; index++)
                {
                    var (liveTable, column) = candidates[index];
                    var rendering = await TryRenderAsync(connection, transaction, liveTable, column, index, cancellationToken);
                    if (rendering is not null)
                    {
                        renderings[column.Key] = rendering;
                    }
                }
            }
            finally
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }

        if (renderings.Count > 0)
        {
            projectModel.Tables = WithRenderings(projectModel.Tables, column => renderings.GetValueOrDefault(column.Key));
        }
    }

    /// <summary>Gives each live generated column the expression it was read with (G-97).</summary>
    internal static void AttachLiveRenderings(DatabaseModel liveModel, IReadOnlyList<LiveTableSnapshot> snapshots)
    {
        if (!snapshots.Any(static snapshot => snapshot.Columns.Any(static column => column.IsGenerated && column.GeneratedExpression is not null)))
        {
            return;
        }

        var snapshotsByTable = snapshots
            .GroupBy(static snapshot => $"{snapshot.SchemaName}.{snapshot.TableName}", PgProj.Parser.SqlIdentifiers.NameComparer)
            .ToDictionary(static group => group.Key, static group => group.First(), PgProj.Parser.SqlIdentifiers.NameComparer);

        liveModel.Tables = WithRenderings(liveModel.Tables, column =>
        {
            if (!column.IsGenerated
                || !snapshotsByTable.TryGetValue($"{column.SchemaName ?? "public"}.{column.TableName}", out var snapshot))
            {
                return null;
            }

            var liveColumn = snapshot.Columns.FirstOrDefault(candidate => string.Equals(candidate.ColumnName, column.Name, StringComparison.Ordinal));
            return liveColumn is { IsGenerated: true } ? liveColumn.GeneratedExpression?.Trim() : null;
        });
    }

    /// <summary>
    /// The expression is project SQL placed inside <c>GENERATED ALWAYS AS (...)</c>, so it must be one column's generation expression
    /// and nothing else; PgQuery confirms that before anything is sent to the server.
    /// </summary>
    internal bool IsGeneratedExpression(string expression)
    {
        var tree = _parser.Parse($"CREATE TABLE pgproj_generated_check (c integer GENERATED ALWAYS AS ({expression}) STORED)");
        if (tree.HasErrors || string.IsNullOrWhiteSpace(tree.RawJsonAst))
        {
            return false;
        }

        using var document = PgQueryJson.Parse(tree.RawJsonAst);
        return document.RootElement.TryGetProperty("stmts", out var statements)
            && statements.ValueKind == JsonValueKind.Array
            && statements.GetArrayLength() == 1
            && statements[0].TryGetProperty("stmt", out var statement)
            && statement.TryGetProperty("CreateStmt", out var create)
            && create.TryGetProperty("tableElts", out var elements)
            && elements.ValueKind == JsonValueKind.Array
            && elements.GetArrayLength() == 1;
    }

    private static async Task<string?> TryRenderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TableDefinition liveTable,
        ColumnDefinition column,
        int index,
        CancellationToken cancellationToken)
    {
        var liveName = $"{SqlIdentifiers.Quote(liveTable.SchemaName ?? "public")}.{SqlIdentifiers.Quote(liveTable.Name)}";
        var columnType = await ReadLiveColumnTypeAsync(connection, transaction, liveName, column.Name, cancellationToken);
        if (columnType is null)
        {
            return null;
        }

        var tableCopy = $"pgproj_generated_{index}";
        var rendering = await RolledBackRendering.TryRenderAsync(
            connection,
            transaction,
            tableCopy,
            [
                $"CREATE TEMPORARY TABLE {tableCopy} (LIKE {liveName});",
                $"ALTER TABLE {tableCopy} ADD COLUMN {RenderedColumn} {columnType} GENERATED ALWAYS AS ({column.GeneratedExpression}) STORED;",
            ],
            $"""
            SELECT pg_catalog.pg_get_expr(def.adbin, def.adrelid)
            FROM pg_catalog.pg_attrdef def
            JOIN pg_catalog.pg_attribute att ON att.attrelid = def.adrelid AND att.attnum = def.adnum
            WHERE def.adrelid = 'pg_temp.{tableCopy}'::regclass AND att.attname = '{RenderedColumn}';
            """,
            cancellationToken);
        return rendering?.Trim();
    }

    /// <summary>The live column's type as PostgreSQL spells it (<c>format_type</c>), or null when the column isn't there.</summary>
    private static async Task<string?> ReadLiveColumnTypeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string liveName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_catalog.format_type(att.atttypid, att.atttypmod)
            FROM pg_catalog.pg_attribute att
            WHERE att.attrelid = pg_catalog.to_regclass(@table) AND att.attnum > 0 AND NOT att.attisdropped AND att.attname = @column;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("table", liveName);
        command.Parameters.AddWithValue("column", columnName);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static Dictionary<string, TableDefinition> WithRenderings(
        IReadOnlyDictionary<string, TableDefinition> tables,
        Func<ColumnDefinition, string?> renderingFor)
    {
        var result = new Dictionary<string, TableDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var (key, table) in tables)
        {
            var changed = false;
            var columns = table.Columns.Select(column =>
            {
                var rendering = renderingFor(column);
                if (rendering is null)
                {
                    return column;
                }

                changed = true;
                return column with { ServerRenderedGeneratedExpression = rendering };
            }).ToArray();

            result[key] = changed ? table with { Columns = columns } : table;
        }

        return result;
    }
}
