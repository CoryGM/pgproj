using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;

namespace PgProj.Engine;

/// <summary>
/// A statement of the script failed (G-309c). Its message is what <c>pgproj publish</c> prints; <see cref="Failure"/> is the same in parts. The server's own
/// exception is the inner one.
/// </summary>
internal sealed class ScriptExecutionException : InvalidOperationException
{
    public ScriptExecutionException(PublishFailure failure, Exception inner)
        : base(failure.Message, inner)
    {
        Failure = failure;
    }

    public PublishFailure Failure { get; }
}

/// <summary>
/// Runs what a publish planned (G-309c; this was in <c>CliContext</c>): the transactional script in one transaction, then the statements <c>--concurrently</c> leaves for after
/// the commit.
/// </summary>
internal static class ScriptExecution
{
    /// <summary>
    /// Runs <paramref name="script"/> in one transaction. With <paramref name="lockTimeout"/> (G-270), the transaction starts with <c>SET LOCAL lock_timeout</c>, so a
    /// statement that cannot get its lock in time fails, the whole script is rolled back, and the error names the statement, instead of waiting behind a long transaction
    /// and blocking everything that queues behind it.
    /// </summary>
    public static async Task ExecuteScriptAsync(NpgsqlConnection connection, string script, string? lockTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(script);

        var statements = SqlStatementSplitter.Split(script);
        if (statements.Count == 0)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (lockTimeout is not null)
            {
                await using var setTimeout = new NpgsqlBatch(connection, transaction) { BatchCommands = { new NpgsqlBatchCommand($"SET LOCAL lock_timeout = '{lockTimeout}'") } };
                await setTimeout.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var statement in statements)
            {
                // A batch command is sent as written. A plain command is split at every semicolon by Npgsql, which cuts a
                // BEGIN ATOMIC routine body (G-95) apart.
                await using var batch = new NpgsqlBatch(connection, transaction) { BatchCommands = { new NpgsqlBatchCommand(statement) } };
                try
                {
                    await batch.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable && lockTimeout is not null)
                {
                    throw new ScriptExecutionException(
                        new PublishFailure(
                            $"a lock was not granted within --lock-timeout {lockTimeout}, so nothing was applied (the script was rolled back). The statement waiting for it: {UnmodeledStatementSnippet(statement)}",
                            exception.SqlState,
                            null,
                            null,
                            UnmodeledStatementSnippet(statement),
                            AfterCommit: false,
                            []),
                        exception);
                }
                catch (PostgresException exception)
                {
                    throw new ScriptExecutionException(
                        new PublishFailure(
                            DescribeFailedStatement(exception, statement),
                            exception.SqlState,
                            ShownDetail(exception),
                            string.IsNullOrWhiteSpace(exception.Hint) ? null : exception.Hint,
                            UnmodeledStatementSnippet(statement),
                            AfterCommit: false,
                            []),
                        exception);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // A rollback that fails too (the server ran out of shared memory, the connection broke) must not hide the error that caused it (G-279):
            // the server rolls the transaction back itself when the session ends.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception)
            {
            }

            throw;
        }
    }

    /// <summary>
    /// Runs the statements <c>publish --concurrently</c> leaves for after the commit (G-271), one at a time, each in a transaction of its own (<c>CREATE INDEX CONCURRENTLY</c>
    /// cannot run in a transaction block), with <paramref name="lockTimeout"/> set for the session. When one fails, its <see cref="AfterCommitStep.CleanupSql"/> drops what it
    /// was building, and the error names the step, PostgreSQL's error, and the steps not done; what was committed before stays.
    /// </summary>
    public static async Task ExecuteAfterCommitAsync(NpgsqlConnection connection, IReadOnlyList<AfterCommitStep> steps, string? lockTimeout, Action<string> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(progress);

        if (steps.Count == 0)
        {
            return;
        }

        if (lockTimeout is not null)
        {
            await RunAsync(connection, $"SET lock_timeout = '{lockTimeout}'", cancellationToken);
        }

        try
        {
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                progress($"[{index + 1}/{steps.Count}] {step.Description}");
                try
                {
                    await RunAsync(connection, step.Sql, cancellationToken);
                }
                catch (PostgresException exception)
                {
                    var cleanup = string.Empty;
                    if (step.CleanupSql is not null)
                    {
                        try
                        {
                            await RunAsync(connection, step.CleanupSql, CancellationToken.None);
                            cleanup = $"{Environment.NewLine}  Dropped what it was building: {UnmodeledStatementSnippet(step.CleanupSql)}";
                        }
                        catch (Exception cleanupException) when (cleanupException is PostgresException or NpgsqlException)
                        {
                            cleanup = $"{Environment.NewLine}  Could not drop what it was building ({cleanupException.Message}); drop it yourself: {UnmodeledStatementSnippet(step.CleanupSql)}";
                        }
                    }

                    var error = exception.SqlState == PostgresErrorCodes.LockNotAvailable && lockTimeout is not null
                        ? $"a lock was not granted within --lock-timeout {lockTimeout}"
                        : $"{exception.SqlState}: {exception.MessageText}";
                    var detail = ShownDetail(exception) is { } shown ? $"{Environment.NewLine}  Detail: {shown}" : string.Empty;
                    var notDone = steps.Skip(index).Select(static remaining => remaining.Description).ToArray();
                    throw new ScriptExecutionException(
                        new PublishFailure(
                            $"step {index + 1} of {steps.Count} after the commit failed ({step.Description}): {error}{detail}"
                            + $"{Environment.NewLine}  The statement that failed: {UnmodeledStatementSnippet(step.Sql)}{cleanup}"
                            + $"{Environment.NewLine}  The transaction and the steps before this one were committed. Not done:{string.Concat(notDone.Select(static description => $"{Environment.NewLine}    {description}"))}"
                            + $"{Environment.NewLine}  Run pgproj publish --execute --concurrently again to do the rest.",
                            exception.SqlState,
                            ShownDetail(exception),
                            string.IsNullOrWhiteSpace(exception.Hint) ? null : exception.Hint,
                            UnmodeledStatementSnippet(step.Sql),
                            AfterCommit: true,
                            notDone),
                        exception);
                }
            }
        }
        finally
        {
            if (lockTimeout is not null)
            {
                await RunAsync(connection, "RESET lock_timeout", CancellationToken.None);
            }
        }
    }

    /// <summary>One statement outside any transaction block: a batch of one command, which Npgsql does not wrap in one.</summary>
    private static async Task RunAsync(NpgsqlConnection connection, string statement, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(connection) { BatchCommands = { new NpgsqlBatchCommand(statement) } };
        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The server's detail with any password masked, or null when it gave none or the connection string redacts it.</summary>
    private static string? ShownDetail(PostgresException exception)
        => !string.IsNullOrWhiteSpace(exception.Detail) && !exception.Detail.StartsWith("Detail redacted", StringComparison.Ordinal)
            ? SecretRedactor.Redact(exception.Detail)
            : null;

    /// <summary>
    /// The error of a statement that failed in <c>publish --execute</c> (G-279): the server's <c>SQLSTATE: message</c> first, as before, then its detail
    /// (when the connection string does not leave it out), its hint, and the statement, as one short line with any password masked.
    /// </summary>
    public static string DescribeFailedStatement(PostgresException exception, string statement)
    {
        var message = new System.Text.StringBuilder($"{exception.SqlState}: {exception.MessageText}");
        if (ShownDetail(exception) is { } detail)
        {
            message.Append($"{Environment.NewLine}  Detail: {detail}");
        }

        if (!string.IsNullOrWhiteSpace(exception.Hint))
        {
            message.Append($"{Environment.NewLine}  Hint: {exception.Hint}");
        }

        message.Append($"{Environment.NewLine}  The statement that failed: {UnmodeledStatementSnippet(statement)}");
        message.Append($"{Environment.NewLine}  Nothing was applied: the script was rolled back.");
        return message.ToString();
    }

    /// <summary>A statement as one short line, with any password masked (G-142) and its whole-line <c>--</c> comments left out (G-279), for an error message.</summary>
    private static string UnmodeledStatementSnippet(string statement)
    {
        var code = string.Join('\n', statement.Split('\n').Where(static line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        var line = System.Text.RegularExpressions.Regex.Replace(SecretRedactor.Redact(code), @"\s+", " ").Trim();
        return line.Length > 120 ? string.Concat(line.AsSpan(0, 120), "...") : line;
    }
}
