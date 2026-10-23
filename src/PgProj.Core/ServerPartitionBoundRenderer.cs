using System.Text.Json;
using Npgsql;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>
/// Gives partitions a <see cref="TableDefinition.ServerRenderedPartitionBound"/> (G-101). PostgreSQL stores a
/// partition's bound with its literals rewritten in the key's type, so a project partition's bound is compared by
/// attaching it to a temporary copy of the live parent (<c>LIKE</c> the parent, <c>PARTITION BY</c> the parent's own
/// key) and reading back PostgreSQL's rendering. Everything happens in a transaction that is always rolled back.
/// </summary>
public sealed class ServerPartitionBoundRenderer
{
    private readonly PgQueryParser _parser;

    public ServerPartitionBoundRenderer(PgQueryParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parser = parser;
    }

    /// <summary>
    /// Sets <see cref="TableDefinition.ServerRenderedPartitionBound"/> on each partition of <paramref name="projectModel"/>
    /// that is also a partition in <paramref name="liveModel"/>. A bound the server rejects (its parent doesn't exist live,
    /// or the bound doesn't fit the live key) is left without one and compared as written.
    /// </summary>
    public async Task RenderAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(projectModel);
        ArgumentNullException.ThrowIfNull(liveModel);

        var candidates = projectModel.Tables.Values
            .Where(table => table.PartitionOfTableName is not null
                && !string.IsNullOrWhiteSpace(table.PartitionBoundRaw)
                && liveModel.Tables.TryGetValue(table.Key, out var liveTable)
                && liveTable.ServerRenderedPartitionBound is not null
                && IsPartitionBound(table.PartitionBoundRaw!))
            .OrderBy(static table => table.Key, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            return;
        }

        var renderings = new Dictionary<string, string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                for (var index = 0; index < candidates.Length; index++)
                {
                    var rendering = await TryRenderAsync(connection, transaction, candidates[index], index, cancellationToken);
                    if (rendering is not null)
                    {
                        renderings[candidates[index].Key] = rendering;
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
            projectModel.Tables = WithRenderings(projectModel.Tables, table => renderings.GetValueOrDefault(table.Key));
        }
    }

    /// <summary>Gives each live partition the bound it was read with (G-101).</summary>
    internal static void AttachLiveRenderings(DatabaseModel liveModel, IReadOnlyList<LiveTableSnapshot> snapshots)
    {
        if (!snapshots.Any(static snapshot => snapshot.PartitionBoundRaw is not null))
        {
            return;
        }

        liveModel.Tables = WithRenderings(liveModel.Tables, table =>
            snapshots.FirstOrDefault(candidate =>
                    candidate.PartitionBoundRaw is not null
                    && string.Equals(candidate.TableName, table.Name, StringComparison.Ordinal)
                    && string.Equals(candidate.SchemaName, table.SchemaName ?? "public", StringComparison.Ordinal))
                ?.PartitionBoundRaw
                ?.Trim());
    }

    /// <summary>
    /// The bound is project SQL placed after <c>PARTITION OF ...</c>, so it must be a bound and nothing else; PgQuery confirms
    /// that before anything is sent to the server.
    /// </summary>
    internal bool IsPartitionBound(string bound)
    {
        var tree = _parser.Parse($"CREATE TABLE pgproj_bound_check PARTITION OF pgproj_bound_parent {bound}");
        if (tree.HasErrors || string.IsNullOrWhiteSpace(tree.RawJsonAst))
        {
            return false;
        }

        using var document = PgQueryJson.Parse(tree.RawJsonAst);
        if (!document.RootElement.TryGetProperty("stmts", out var statements)
            || statements.ValueKind != JsonValueKind.Array
            || statements.GetArrayLength() != 1)
        {
            return false;
        }

        return statements[0].TryGetProperty("stmt", out var statement)
            && statement.TryGetProperty("CreateStmt", out var create)
            && create.TryGetProperty("partbound", out _);
    }

    private static async Task<string?> TryRenderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TableDefinition partition,
        int index,
        CancellationToken cancellationToken)
    {
        var parent = $"{SqlIdentifiers.Quote(partition.PartitionOfSchemaName ?? partition.SchemaName ?? "public")}.{SqlIdentifiers.Quote(partition.PartitionOfTableName!)}";
        var keyDefinition = await ReadPartitionKeyDefinitionAsync(connection, transaction, parent, cancellationToken);
        if (keyDefinition is null)
        {
            return null;
        }

        var parentCopy = $"pgproj_bound_parent_{index}";
        var partitionCopy = $"pgproj_bound_{index}";
        var rendering = await RolledBackRendering.TryRenderAsync(
            connection,
            transaction,
            partitionCopy,
            [
                $"CREATE TEMPORARY TABLE {parentCopy} (LIKE {parent}) PARTITION BY {keyDefinition};",
                $"CREATE TEMPORARY TABLE {partitionCopy} PARTITION OF {parentCopy} {partition.PartitionBoundRaw};",
            ],
            $"SELECT pg_catalog.pg_get_expr(relpartbound, oid) FROM pg_catalog.pg_class WHERE oid = 'pg_temp.{partitionCopy}'::regclass;",
            cancellationToken);
        return rendering?.Trim();
    }

    /// <summary>The live parent's own <c>PARTITION BY</c> key (<c>pg_get_partkeydef</c>), or null when it isn't a partitioned table.</summary>
    private static async Task<string?> ReadPartitionKeyDefinitionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string parent, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_get_partkeydef(cls.oid) FROM pg_catalog.pg_class cls WHERE cls.oid = pg_catalog.to_regclass(@parent) AND cls.relkind = 'p';",
            connection,
            transaction);
        command.Parameters.AddWithValue("parent", parent);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static Dictionary<string, TableDefinition> WithRenderings(
        IReadOnlyDictionary<string, TableDefinition> tables,
        Func<TableDefinition, string?> renderingFor)
    {
        var result = new Dictionary<string, TableDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var (key, table) in tables)
        {
            var rendering = renderingFor(table);
            result[key] = rendering is null ? table : table with { ServerRenderedPartitionBound = rendering };
        }

        return result;
    }
}
