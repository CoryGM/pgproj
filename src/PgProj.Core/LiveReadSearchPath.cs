using Npgsql;

namespace PgProj.Core;

/// <summary>
/// The live database is read, and project SQL rendered, with <c>search_path</c> set to <c>public</c> (G-93). PostgreSQL leaves a schema off
/// every name it prints that the session's path finds (<c>pg_get_viewdef</c>, <c>pg_get_indexdef</c>, <c>format_type</c>, <c>pg_get_expr</c>), and
/// the live model reads an unqualified name as the one in <c>public</c> (G-114). Pinning the path makes that true whatever the connecting
/// role's own path is, and whatever <c>searchPath</c> the project sets for the session in which its objects are created and deployed.
/// </summary>
internal static class LiveReadSearchPath
{
    public const string Value = "public";

    /// <summary>Sets the session's path to <see cref="Value"/> and returns the path it had, for <see cref="RestoreAsync"/>.</summary>
    public static async Task<string?> PinAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand("select pg_catalog.current_setting('search_path')", connection, transaction);
        var previous = (string?)await read.ExecuteScalarAsync(cancellationToken);
        await SetAsync(connection, transaction, Value, cancellationToken);
        return previous;
    }

    public static Task RestoreAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string? previous, CancellationToken cancellationToken)
        => previous is null ? Task.CompletedTask : SetAsync(connection, transaction, previous, cancellationToken);

    /// <summary>Inside a transaction the setting is local to it (and to the savepoint it is made under), as <c>SET LOCAL</c> is.</summary>
    private static async Task SetAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string path, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_catalog.set_config('search_path', @path, @local)", connection, transaction);
        command.Parameters.AddWithValue("path", path);
        command.Parameters.AddWithValue("local", transaction is not null);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
