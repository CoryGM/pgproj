using Npgsql;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>
/// Gives policies a <see cref="PolicyDefinition.ServerRenderedUsing"/> and <see cref="PolicyDefinition.ServerRenderedWithCheck"/> (G-245). PostgreSQL stores a policy's
/// expressions as trees and prints them back qualified and parenthesized its own way (a subquery most of all: <c>( SELECT (t.c &lt;= m.c1) FROM m)</c>), so a project policy that
/// also exists in the database is compared by replacing the live policy with it inside a transaction that is always rolled back, and reading back <c>pg_get_expr</c>. Everything
/// happens under a savepoint per policy, so one the server rejects (it uses something this publish has yet to create, or the connecting role does not own the table) is left
/// without a rendering and compared by its text, as before.
/// </summary>
public sealed class ServerPolicyRenderer
{
    private const char Separator = '\u001f';

    /// <summary>
    /// Sets the renderings on each policy of <paramref name="projectModel"/> that also exists in <paramref name="liveModel"/> (with a rendering of its own, which is what a
    /// live policy with an expression has).
    /// </summary>
    public async Task RenderAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(projectModel);
        ArgumentNullException.ThrowIfNull(liveModel);

        var candidates = projectModel.Policies.Values
            .Where(policy => (!string.IsNullOrWhiteSpace(policy.RawUsingExpression) || !string.IsNullOrWhiteSpace(policy.RawWithCheckExpression))
                && !string.IsNullOrWhiteSpace(policy.TableName)
                && liveModel.Policies.TryGetValue(policy.Key, out var livePolicy)
                && (livePolicy.ServerRenderedUsing is not null || livePolicy.ServerRenderedWithCheck is not null))
            .OrderBy(static policy => policy.Key, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            return;
        }

        var renderings = new Dictionary<string, (string? Using, string? WithCheck)>(PgProj.Parser.SqlIdentifiers.NameComparer);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                for (var index = 0; index < candidates.Length; index++)
                {
                    var rendering = await TryRenderAsync(connection, transaction, candidates[index], index, cancellationToken);
                    if (rendering is not null)
                    {
                        renderings[candidates[index].Key] = rendering.Value;
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
            projectModel.Policies = WithRenderings(projectModel.Policies, policy => renderings.TryGetValue(policy.Key, out var rendering) ? rendering : null);
        }
    }

    /// <summary>Gives each live policy the renderings it was read with (G-245).</summary>
    internal static void AttachLiveRenderings(DatabaseModel liveModel, IReadOnlyList<LivePolicySnapshot> snapshots)
    {
        if (snapshots.Count == 0 || liveModel.Policies.Count == 0)
        {
            return;
        }

        liveModel.Policies = WithRenderings(liveModel.Policies, policy =>
        {
            var snapshot = snapshots.FirstOrDefault(candidate =>
                string.Equals(candidate.PolicyName, policy.Name, StringComparison.Ordinal)
                && string.Equals(candidate.TableName, policy.TableName, StringComparison.Ordinal)
                && string.Equals(candidate.SchemaName, policy.SchemaName ?? "public", StringComparison.Ordinal));
            return snapshot is null ? null : (snapshot.UsingExpression?.Trim(), snapshot.WithCheckExpression?.Trim());
        });
    }

    private static async Task<(string? Using, string? WithCheck)?> TryRenderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PolicyDefinition policy,
        int position,
        CancellationToken cancellationToken)
    {
        var table = $"{SqlIdentifiers.Quote(policy.SchemaName ?? "public")}.{SqlIdentifiers.Quote(policy.TableName!)}";
        var name = SqlIdentifiers.Quote(policy.Name);
        var command = policy.Command.ToUpperInvariant() is "SELECT" or "INSERT" or "UPDATE" or "DELETE" ? policy.Command.ToUpperInvariant() : "ALL";
        var statement = $"CREATE POLICY {name} ON {table} AS {(policy.IsPermissive ? "PERMISSIVE" : "RESTRICTIVE")} FOR {command} TO PUBLIC"
            + (string.IsNullOrWhiteSpace(policy.RawUsingExpression) ? string.Empty : $" USING ({policy.RawUsingExpression})")
            + (string.IsNullOrWhiteSpace(policy.RawWithCheckExpression) ? string.Empty : $" WITH CHECK ({policy.RawWithCheckExpression})")
            + ";";
        var literalTable = table.Replace("'", "''", StringComparison.Ordinal);
        var literalName = policy.Name.Replace("'", "''", StringComparison.Ordinal);
        var rendering = await RolledBackRendering.TryRenderAsync(
            connection,
            transaction,
            $"pgproj_policy_{position}",
            [$"DROP POLICY {name} ON {table};", statement],
            $"SELECT coalesce(pg_catalog.pg_get_expr(pol.polqual, pol.polrelid), '') || chr(31) || coalesce(pg_catalog.pg_get_expr(pol.polwithcheck, pol.polrelid), '') FROM pg_catalog.pg_policy pol WHERE pol.polrelid = '{literalTable}'::regclass AND pol.polname = '{literalName}';",
            cancellationToken);
        if (rendering is null)
        {
            return null;
        }

        var parts = rendering.Split(Separator);
        return (parts[0].Length == 0 ? null : parts[0].Trim(), parts.Length > 1 && parts[1].Length > 0 ? parts[1].Trim() : null);
    }

    private static Dictionary<string, PolicyDefinition> WithRenderings(
        IReadOnlyDictionary<string, PolicyDefinition> policies,
        Func<PolicyDefinition, (string? Using, string? WithCheck)?> renderingFor)
    {
        var result = new Dictionary<string, PolicyDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var (key, policy) in policies)
        {
            var rendering = renderingFor(policy);
            result[key] = rendering is null ? policy : policy with { ServerRenderedUsing = rendering.Value.Using, ServerRenderedWithCheck = rendering.Value.WithCheck };
        }

        return result;
    }
}
