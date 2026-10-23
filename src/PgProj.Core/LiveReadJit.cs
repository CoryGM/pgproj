using Npgsql;

namespace PgProj.Core;

/// <summary>
/// The live database is read with <c>jit</c> off (G-328). PostgreSQL 14 to 16 estimate some catalog queries (the aggregates', which joins every function
/// eight times) at a million rows, past <c>jit_above_cost</c>, and spend most of a second compiling one that returns nothing; a catalog read never gains
/// from JIT. Only the read is changed: the session's own setting is put back afterwards, so deployment SQL and the project's scripts run as the server
/// and connection set them.
/// </summary>
internal static class LiveReadJit
{
    /// <summary>Turns <c>jit</c> off for the session and returns the value it had, for <see cref="RestoreAsync"/>.</summary>
    public static async Task<string?> PinAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand("select pg_catalog.current_setting('jit')", connection);
        var previous = (string?)await read.ExecuteScalarAsync(cancellationToken);
        await SetAsync(connection, "off", cancellationToken);
        return previous;
    }

    public static Task RestoreAsync(NpgsqlConnection connection, string? previous, CancellationToken cancellationToken)
        => previous is null ? Task.CompletedTask : SetAsync(connection, previous, cancellationToken);

    private static async Task SetAsync(NpgsqlConnection connection, string value, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_catalog.set_config('jit', @value, false)", connection);
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
