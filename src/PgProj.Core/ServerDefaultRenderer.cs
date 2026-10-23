using System.Text.Json;
using Npgsql;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>
/// Gives column defaults a <see cref="ColumnDefinition.ServerRenderedDefault"/> (G-102). PostgreSQL stores a default in
/// its own rendering (<c>0::numeric</c> as <c>(0)::numeric</c>, <c>1.5::numeric</c> as <c>1.5</c>), so a project column's
/// default is compared by giving it to a temporary column of the live column's type and reading back PostgreSQL's
/// rendering. Everything happens in a transaction that is always rolled back.
/// </summary>
public sealed class ServerDefaultRenderer
{
    private readonly PgQueryParser _parser;

    public ServerDefaultRenderer(PgQueryParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parser = parser;
    }

    /// <summary>
    /// Sets <see cref="ColumnDefinition.ServerRenderedDefault"/> on each defaulted column of <paramref name="projectModel"/>
    /// whose live column also has a default. A default the server rejects (it uses an object the server doesn't have yet)
    /// is left without one and compared as before.
    /// </summary>
    public async Task RenderAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(projectModel);
        ArgumentNullException.ThrowIfNull(liveModel);

        var candidates = new List<(TableDefinition Table, ColumnDefinition Column)>();
        foreach (var table in projectModel.Tables.Values.OrderBy(static table => table.Key, StringComparer.Ordinal))
        {
            if (!liveModel.Tables.TryGetValue(table.Key, out var liveTable))
            {
                continue;
            }

            foreach (var column in table.Columns)
            {
                var liveColumn = liveTable.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, column.Name, StringComparison.Ordinal));
                if (!column.IsGenerated
                    && !string.IsNullOrWhiteSpace(column.RawDefaultExpression)
                    && liveColumn is { IsGenerated: false, ServerRenderedDefault: not null }
                    && IsDefaultExpression(column.RawDefaultExpression))
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

    /// <summary>Gives each live column the default it was read with (G-102).</summary>
    internal static void AttachLiveRenderings(DatabaseModel liveModel, IReadOnlyList<LiveTableSnapshot> snapshots)
    {
        if (!snapshots.Any(static snapshot => snapshot.Columns.Any(static column => column.DefaultExpression is not null)))
        {
            return;
        }

        var snapshotsByTable = snapshots
            .GroupBy(static snapshot => $"{snapshot.SchemaName}.{snapshot.TableName}", PgProj.Parser.SqlIdentifiers.NameComparer)
            .ToDictionary(static group => group.Key, static group => group.First(), PgProj.Parser.SqlIdentifiers.NameComparer);

        liveModel.Tables = WithRenderings(liveModel.Tables, column =>
        {
            if (column.IsGenerated
                || !snapshotsByTable.TryGetValue($"{column.SchemaName ?? "public"}.{column.TableName}", out var snapshot))
            {
                return null;
            }

            return snapshot.Columns
                .FirstOrDefault(candidate => string.Equals(candidate.ColumnName, column.Name, StringComparison.Ordinal))
                ?.DefaultExpression
                ?.Trim();
        });
    }

    /// <summary>
    /// The default is project SQL placed after <c>DEFAULT</c> in a temporary table's column, so it must be a column default
    /// and nothing else; PgQuery confirms that before anything is sent to the server.
    /// </summary>
    internal bool IsDefaultExpression(string expression)
    {
        var tree = _parser.Parse($"CREATE TABLE pgproj_default_check (c integer DEFAULT {expression})");
        if (tree.HasErrors || string.IsNullOrWhiteSpace(tree.RawJsonAst))
        {
            return false;
        }

        using var document = PgQueryJson.Parse(tree.RawJsonAst);
        if (!document.RootElement.TryGetProperty("stmts", out var statements)
            || statements.ValueKind != JsonValueKind.Array
            || statements.GetArrayLength() != 1
            || !statements[0].TryGetProperty("stmt", out var statement)
            || !statement.TryGetProperty("CreateStmt", out var create)
            || !create.TryGetProperty("tableElts", out var elements)
            || elements.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        // Exactly the one column: a default like "1, d integer" would otherwise smuggle in another.
        return elements.GetArrayLength() == 1;
    }

    private static async Task<string?> TryRenderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TableDefinition liveTable,
        ColumnDefinition column,
        int index,
        CancellationToken cancellationToken)
    {
        var columnType = await ReadLiveColumnTypeAsync(connection, transaction, liveTable, column.Name, cancellationToken);
        if (columnType is null)
        {
            return null;
        }

        var tableCopy = $"pgproj_default_{index}";
        var rendering = await RolledBackRendering.TryRenderAsync(
            connection,
            transaction,
            tableCopy,
            [$"CREATE TEMPORARY TABLE {tableCopy} (c {columnType} DEFAULT {column.RawDefaultExpression});"],
            $"SELECT pg_catalog.pg_get_expr(def.adbin, def.adrelid) FROM pg_catalog.pg_attrdef def WHERE def.adrelid = 'pg_temp.{tableCopy}'::regclass;",
            cancellationToken);
        return rendering?.Trim();
    }

    /// <summary>The live column's type as PostgreSQL spells it (<c>format_type</c>), or null when the column isn't there.</summary>
    private static async Task<string?> ReadLiveColumnTypeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TableDefinition liveTable,
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
        command.Parameters.AddWithValue("table", $"{SqlIdentifiers.Quote(liveTable.SchemaName ?? "public")}.{SqlIdentifiers.Quote(liveTable.Name)}");
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
                return column with { ServerRenderedDefault = rendering };
            }).ToArray();

            result[key] = changed ? table with { Columns = columns } : table;
        }

        return result;
    }
}
