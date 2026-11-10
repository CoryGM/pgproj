using System.Text.Json;
using Npgsql;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>
/// Gives partial indexes a <see cref="IndexDefinition.ServerRenderedPredicate"/> (G-100). PostgreSQL stores an index predicate with
/// each literal typed by the column it is compared with (<c>'x'::s.st</c> for an enum, a date, a <c>timestamptz</c> with its offset),
/// so a project index's predicate is compared by putting it on a temporary index over a copy of the live table (<c>LIKE</c>) and
/// reading back PostgreSQL's rendering. Everything happens in a transaction that is always rolled back.
/// </summary>
public sealed class ServerIndexPredicateRenderer
{
    private readonly PgQueryParser _parser;

    public ServerIndexPredicateRenderer(PgQueryParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parser = parser;
    }

    /// <summary>
    /// Sets <see cref="IndexDefinition.ServerRenderedPredicate"/> on each partial index of <paramref name="projectModel"/> whose live
    /// index is partial too. A predicate the server rejects (it uses a column the live table doesn't have yet) is left without one and
    /// compared as before. Then sets <see cref="IndexDefinition.ServerRenderedKeys"/> on each index with an expression key whose live index
    /// has its renderings (G-344), the same way.
    /// </summary>
    public async Task RenderAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(projectModel);
        ArgumentNullException.ThrowIfNull(liveModel);

        await RenderPredicatesAsync(connection, projectModel, liveModel, cancellationToken);
        await RenderKeysAsync(connection, projectModel, liveModel, cancellationToken);
    }

    /// <summary>
    /// Each expression key of a project index put alone on a temporary index over a copy of the live table (G-344), and read back as PostgreSQL renders it,
    /// <c>pg_get_indexdef(index, 1, true)</c>, as the live index's keys are read. A column key needs none. An index whose expression the server rejects (a
    /// column the live table does not have yet), or whose key count differs from the live index's, keeps none and is compared as before.
    /// </summary>
    private async Task RenderKeysAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        var candidates = projectModel.Indexes.Values
            .Where(index => !string.IsNullOrWhiteSpace(index.TableName)
                && index.KeyElements.Any(static key => key.RawExpression is not null)
                && index.KeyElements.All(key => key.RawExpression is null || IsIndexExpression(key.RawExpression))
                && liveModel.Indexes.TryGetValue(index.Key, out var liveIndex)
                && liveIndex.ServerRenderedKeys is { } liveKeys
                && liveKeys.Count == index.KeyElements.Count)
            .OrderBy(static index => index.Key, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        var renderings = new Dictionary<string, IReadOnlyList<string?>>(PgProj.Parser.SqlIdentifiers.NameComparer);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                for (var position = 0; position < candidates.Length; position++)
                {
                    var index = candidates[position];
                    var keys = new string?[index.KeyElements.Count];
                    var rendered = true;
                    for (var key = 0; key < keys.Length && rendered; key++)
                    {
                        if (index.KeyElements[key].RawExpression is { } expression)
                        {
                            keys[key] = await TryRenderKeyAsync(connection, transaction, index, expression, position, key, cancellationToken);
                            rendered = keys[key] is not null;
                        }
                    }

                    if (rendered)
                    {
                        renderings[index.Key] = keys;
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
            var indexes = new Dictionary<string, IndexDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
            foreach (var (key, index) in projectModel.Indexes)
            {
                indexes[key] = renderings.TryGetValue(key, out var keys) ? index with { ServerRenderedKeys = keys } : index;
            }

            projectModel.Indexes = indexes;
        }
    }

    private static async Task<string?> TryRenderKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IndexDefinition index,
        string expression,
        int position,
        int key,
        CancellationToken cancellationToken)
    {
        var liveTable = $"{SqlIdentifiers.Quote(index.SchemaName ?? "public")}.{SqlIdentifiers.Quote(index.TableName!)}";
        var tableCopy = $"pgproj_key_{position}_{key}";
        var indexCopy = $"pgproj_key_{position}_{key}_idx";
        var rendering = await RolledBackRendering.TryRenderAsync(
            connection,
            transaction,
            tableCopy,
            [
                $"CREATE TEMPORARY TABLE {tableCopy} (LIKE {liveTable});",
                $"CREATE INDEX {indexCopy} ON {tableCopy} (({expression}));",
            ],
            $"SELECT pg_catalog.pg_get_indexdef('pg_temp.{indexCopy}'::regclass, 1, true);",
            cancellationToken);
        return rendering?.Trim();
    }

    /// <summary>
    /// The expression is project SQL placed in the key list of a temporary <c>CREATE INDEX</c>, so it must be one key's expression and nothing else; PgQuery
    /// confirms that before anything is sent to the server (G-344).
    /// </summary>
    internal bool IsIndexExpression(string expression)
    {
        var tree = _parser.Parse($"CREATE INDEX pgproj_key_check ON pgproj_key_table (({expression}))");
        if (tree.HasErrors || string.IsNullOrWhiteSpace(tree.RawJsonAst))
        {
            return false;
        }

        using var document = PgQueryJson.Parse(tree.RawJsonAst);
        return document.RootElement.TryGetProperty("stmts", out var statements)
            && statements.ValueKind == JsonValueKind.Array
            && statements.GetArrayLength() == 1
            && statements[0].TryGetProperty("stmt", out var statement)
            && statement.TryGetProperty("IndexStmt", out var indexStatement)
            && indexStatement.TryGetProperty("indexParams", out var keys)
            && keys.ValueKind == JsonValueKind.Array
            && keys.GetArrayLength() == 1
            && keys[0].TryGetProperty("IndexElem", out var element)
            && element.TryGetProperty("expr", out _)
            && !indexStatement.TryGetProperty("whereClause", out _);
    }

    private async Task RenderPredicatesAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        var candidates = projectModel.Indexes.Values
            .Where(index => !string.IsNullOrWhiteSpace(index.RawPredicateExpression)
                && !string.IsNullOrWhiteSpace(index.TableName)
                && liveModel.Indexes.TryGetValue(index.Key, out var liveIndex)
                && liveIndex.ServerRenderedPredicate is not null
                && IsIndexPredicate(index.RawPredicateExpression!))
            .OrderBy(static index => index.Key, StringComparer.Ordinal)
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
            projectModel.Indexes = WithRenderings(projectModel.Indexes, index => renderings.GetValueOrDefault(index.Key));
        }
    }

    /// <summary>Gives each live partial index the predicate it was read with (G-100).</summary>
    internal static void AttachLiveRenderings(DatabaseModel liveModel, IReadOnlyList<LiveIndexSnapshot> snapshots)
    {
        // Each key as it was read (G-344), for an index with an expression key to be compared by.
        if (snapshots.Any(static snapshot => snapshot.KeyRenderings.Count > 0))
        {
            var bySchemaAndName = new Dictionary<(string Schema, string Name), LiveIndexSnapshot>();
            foreach (var snapshot in snapshots)
            {
                bySchemaAndName.TryAdd((snapshot.SchemaName, snapshot.IndexName), snapshot);
            }

            var indexes = new Dictionary<string, IndexDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
            foreach (var (key, index) in liveModel.Indexes)
            {
                indexes[key] = index.KeyElements.Any(static element => element.RawExpression is not null)
                    && bySchemaAndName.TryGetValue((index.SchemaName ?? "public", index.Name), out var snapshot)
                    && snapshot.KeyRenderings.Count > 0
                    ? index with { ServerRenderedKeys = [.. snapshot.KeyRenderings.Select(static rendering => (string?)rendering.Trim())] }
                    : index;
            }

            liveModel.Indexes = indexes;
        }

        if (!snapshots.Any(static snapshot => snapshot.Predicate is not null))
        {
            return;
        }

        liveModel.Indexes = WithRenderings(liveModel.Indexes, index =>
            snapshots.FirstOrDefault(candidate =>
                    candidate.Predicate is not null
                    && string.Equals(candidate.IndexName, index.Name, StringComparison.Ordinal)
                    && string.Equals(candidate.SchemaName, index.SchemaName ?? "public", StringComparison.Ordinal))
                ?.Predicate
                ?.Trim());
    }

    /// <summary>
    /// The predicate is project SQL placed after <c>WHERE</c> in a temporary <c>CREATE INDEX</c>, so it must be one index's predicate
    /// and nothing else; PgQuery confirms that before anything is sent to the server.
    /// </summary>
    internal bool IsIndexPredicate(string predicate)
    {
        var tree = _parser.Parse($"CREATE INDEX pgproj_predicate_check ON pgproj_predicate_table ((1)) WHERE {predicate}");
        if (tree.HasErrors || string.IsNullOrWhiteSpace(tree.RawJsonAst))
        {
            return false;
        }

        using var document = PgQueryJson.Parse(tree.RawJsonAst);
        return document.RootElement.TryGetProperty("stmts", out var statements)
            && statements.ValueKind == JsonValueKind.Array
            && statements.GetArrayLength() == 1
            && statements[0].TryGetProperty("stmt", out var statement)
            && statement.TryGetProperty("IndexStmt", out var indexStatement)
            && indexStatement.TryGetProperty("whereClause", out _);
    }

    private static async Task<string?> TryRenderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IndexDefinition index,
        int position,
        CancellationToken cancellationToken)
    {
        var liveTable = $"{SqlIdentifiers.Quote(index.SchemaName ?? "public")}.{SqlIdentifiers.Quote(index.TableName!)}";
        var tableCopy = $"pgproj_index_{position}";
        var indexCopy = $"pgproj_index_{position}_idx";
        var rendering = await RolledBackRendering.TryRenderAsync(
            connection,
            transaction,
            tableCopy,
            [
                $"CREATE TEMPORARY TABLE {tableCopy} (LIKE {liveTable});",
                $"CREATE INDEX {indexCopy} ON {tableCopy} ((1)) WHERE {index.RawPredicateExpression};",
            ],
            $"SELECT pg_catalog.pg_get_expr(ind.indpred, ind.indrelid) FROM pg_catalog.pg_index ind WHERE ind.indexrelid = 'pg_temp.{indexCopy}'::regclass;",
            cancellationToken);
        return rendering?.Trim();
    }

    private static Dictionary<string, IndexDefinition> WithRenderings(
        IReadOnlyDictionary<string, IndexDefinition> indexes,
        Func<IndexDefinition, string?> renderingFor)
    {
        var result = new Dictionary<string, IndexDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var (key, index) in indexes)
        {
            var rendering = renderingFor(index);
            result[key] = rendering is null ? index : index with { ServerRenderedPredicate = rendering };
        }

        return result;
    }
}
