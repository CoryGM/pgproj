using Npgsql;

namespace PgProj.Core;

/// <summary>
/// Asks the server to render a piece of project SQL the way it renders the live object (G-96, G-101): the caller's
/// statements create temporary objects under a savepoint inside a transaction the caller always rolls back, and a query
/// reads back PostgreSQL's rendering. A statement the server rejects rolls back to the savepoint and yields no rendering,
/// so one failure doesn't affect the others.
/// </summary>
internal static class RolledBackRendering
{
    public static async Task<string?> TryRenderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string savepointName,
        IReadOnlyList<string> setupStatements,
        string renderingQuery,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, $"SAVEPOINT {savepointName};", cancellationToken);
        try
        {
            foreach (var statement in setupStatements)
            {
                await ExecuteAsync(connection, transaction, statement, cancellationToken);
            }

            // Created under the session's path (the project's searchPath, G-93), rendered under the path the live model is read with,
            // so both sides are printed alike. The setting is local to this savepoint.
            var previousSearchPath = await LiveReadSearchPath.PinAsync(connection, transaction, cancellationToken);
            await using var command = new NpgsqlCommand(renderingQuery, connection, transaction);
            var rendering = await command.ExecuteScalarAsync(cancellationToken) as string;
            await LiveReadSearchPath.RestoreAsync(connection, transaction, previousSearchPath, cancellationToken);
            await ExecuteAsync(connection, transaction, $"RELEASE SAVEPOINT {savepointName};", cancellationToken);
            return rendering;
        }
        catch (PostgresException)
        {
            await ExecuteAsync(connection, transaction, $"ROLLBACK TO SAVEPOINT {savepointName};", CancellationToken.None);
            return null;
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        // A batch command is sent as written; a plain command would be split at the semicolons of a BEGIN ATOMIC body (G-95).
        await using var batch = new NpgsqlBatch(connection, transaction) { BatchCommands = { new NpgsqlBatchCommand(sql) } };
        await batch.ExecuteNonQueryAsync(cancellationToken);
    }
}
