using Npgsql;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>
/// Gives rules a <see cref="RuleDefinition.ServerRenderedDefinition"/> (G-128). PostgreSQL rewrites a rule when it stores it (it qualifies names, adds parentheses,
/// spells out <c>new</c> and <c>old</c>, and turns a command into a query tree), so a project rule is compared by replacing the live rule with it inside a
/// transaction that is always rolled back, and reading back <c>pg_get_ruledef</c>. Everything happens under a savepoint per rule, so one the server rejects (it uses
/// something this publish has yet to create) is left without a rendering and compared by its text.
/// </summary>
public sealed class ServerRuleRenderer
{
    /// <summary>
    /// Sets <see cref="RuleDefinition.ServerRenderedDefinition"/> on each rule of <paramref name="projectModel"/> that also exists in <paramref name="liveModel"/>
    /// (with a rendering of its own, which is what a live rule has).
    /// </summary>
    public async Task RenderAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(projectModel);
        ArgumentNullException.ThrowIfNull(liveModel);

        var candidates = projectModel.Rules.Values
            .Where(rule => !string.IsNullOrWhiteSpace(rule.RawDefinition)
                && !string.IsNullOrWhiteSpace(rule.TableName)
                && liveModel.Rules.TryGetValue(rule.Key, out var liveRule)
                && liveRule.ServerRenderedDefinition is not null)
            .OrderBy(static rule => rule.Key, StringComparer.Ordinal)
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
            projectModel.Rules = WithRenderings(projectModel.Rules, rule => renderings.GetValueOrDefault(rule.Key));
        }
    }

    /// <summary>Gives each live rule the rendering it was read with (G-128).</summary>
    internal static void AttachLiveRenderings(DatabaseModel liveModel, IReadOnlyList<LiveRuleSnapshot> snapshots)
    {
        if (snapshots.Count == 0 || liveModel.Rules.Count == 0)
        {
            return;
        }

        liveModel.Rules = WithRenderings(liveModel.Rules, rule =>
            snapshots.FirstOrDefault(candidate =>
                    string.Equals(candidate.RuleName, rule.Name, StringComparison.Ordinal)
                    && string.Equals(candidate.TableName, rule.TableName, StringComparison.Ordinal)
                    && string.Equals(candidate.SchemaName, rule.SchemaName ?? "public", StringComparison.Ordinal))
                ?.Definition
                .Trim());
    }

    private static async Task<string?> TryRenderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuleDefinition rule,
        int position,
        CancellationToken cancellationToken)
    {
        var table = $"{SqlIdentifiers.Quote(rule.SchemaName ?? "public")}.{SqlIdentifiers.Quote(rule.TableName!)}";
        var name = SqlIdentifiers.Quote(rule.Name);
        var rendering = await RolledBackRendering.TryRenderAsync(
            connection,
            transaction,
            $"pgproj_rule_{position}",
            [
                $"DROP RULE {name} ON {table};",
                rule.RawDefinition!.TrimEnd().TrimEnd(';') + ";",
            ],
            $"SELECT pg_catalog.pg_get_ruledef(r.oid, false) FROM pg_catalog.pg_rewrite r WHERE r.ev_class = '{table.Replace("'", "''", StringComparison.Ordinal)}'::regclass AND r.rulename = '{rule.Name.Replace("'", "''", StringComparison.Ordinal)}';",
            cancellationToken);
        return rendering?.Trim();
    }

    private static Dictionary<string, RuleDefinition> WithRenderings(
        IReadOnlyDictionary<string, RuleDefinition> rules,
        Func<RuleDefinition, string?> renderingFor)
    {
        var result = new Dictionary<string, RuleDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var (key, rule) in rules)
        {
            var rendering = renderingFor(rule);
            result[key] = rendering is null ? rule : rule with { ServerRenderedDefinition = rendering };
        }

        return result;
    }
}
