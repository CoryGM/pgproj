using Npgsql;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>
/// Gives a domain's <c>CHECK</c> constraints a <see cref="DomainCheckConstraintDefinition.ServerRenderedCheck"/> on both sides (G-288, approved as recommended). PostgreSQL
/// stores a domain check with the casts it adds (<c>length(VALUE)</c> on a <c>varchar</c> domain is <c>length((VALUE)::text)</c>), which the project's text cannot know, so a
/// project check is added to the live domain as a scratch <c>NOT VALID</c> constraint and read back with <c>pg_get_expr</c>, and the live checks are read the same way. All of it
/// happens in a transaction that is always rolled back. A check the server refuses (the role does not own the domain, or the expression names what the database lacks) gets
/// no rendering, and its domain is compared by text as before.
/// </summary>
public sealed class ServerDomainCheckRenderer
{
    private readonly ServerCheckConstraintRenderer _checkRenderer;

    public ServerDomainCheckRenderer(PgQueryParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _checkRenderer = new ServerCheckConstraintRenderer(parser);
    }

    /// <summary>Sets the renderings on each domain that has checks in both models.</summary>
    public async Task RenderAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(projectModel);
        ArgumentNullException.ThrowIfNull(liveModel);

        var domains = projectModel.Types.Values
            .Where(type => type.DomainCheckConstraints.Count > 0
                && liveModel.Types.TryGetValue(type.Key, out var live)
                && live.DomainCheckConstraints.Count > 0)
            .OrderBy(static type => type.Key, StringComparer.Ordinal)
            .ToArray();
        if (domains.Length == 0)
        {
            return;
        }

        var projectRenderings = new Dictionary<string, IReadOnlyList<string?>>(SqlIdentifiers.NameComparer);
        var liveRenderings = new Dictionary<string, IReadOnlyList<string?>>(SqlIdentifiers.NameComparer);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                var position = 0;
                foreach (var domain in domains)
                {
                    var live = liveModel.Types[domain.Key];
                    var type = DomainLiteral(domain);

                    // The live checks first, before a scratch check is added to the domain.
                    var liveList = new List<string?>();
                    foreach (var check in live.DomainCheckConstraints)
                    {
                        liveList.Add(check.Name is null ? null : await RenderAsync(connection, transaction, $"pgproj_domain_live_{position++}", [], type, check.Name, cancellationToken));
                    }

                    var projectList = new List<string?>();
                    foreach (var check in domain.DomainCheckConstraints)
                    {
                        var name = $"pgproj_domain_check_{position++}";
                        projectList.Add(_checkRenderer.IsCheckExpression(check.RawExpression)
                            ? await RenderAsync(
                                connection,
                                transaction,
                                name,
                                [$"ALTER DOMAIN {SqlIdentifiers.Quote(domain.SchemaName ?? "public")}.{SqlIdentifiers.Quote(domain.Name)} ADD CONSTRAINT {name} CHECK ({check.RawExpression}) NOT VALID;"],
                                type,
                                name,
                                cancellationToken)
                            : null);
                    }

                    liveRenderings[domain.Key] = liveList;
                    projectRenderings[domain.Key] = projectList;
                }
            }
            finally
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }

        projectModel.Types = WithRenderings(projectModel.Types, projectRenderings);
        liveModel.Types = WithRenderings(liveModel.Types, liveRenderings);
    }

    private static async Task<string?> RenderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string savepoint,
        IReadOnlyList<string> setup,
        string typeLiteral,
        string constraintName,
        CancellationToken cancellationToken)
    {
        var rendering = await RolledBackRendering.TryRenderAsync(
            connection,
            transaction,
            savepoint,
            setup,
            $"SELECT pg_catalog.pg_get_expr(con.conbin, 0) FROM pg_catalog.pg_constraint con WHERE con.contypid = {typeLiteral}::regtype AND con.conname = {Literal(constraintName)};",
            cancellationToken);
        return rendering?.Trim();
    }

    /// <summary>The domain as a <c>regtype</c> literal, quoted as PostgreSQL reads a name.</summary>
    private static string DomainLiteral(TypeDefinition domain)
        => Literal($"{SqlIdentifiers.Quote(domain.SchemaName ?? "public")}.{SqlIdentifiers.Quote(domain.Name)}");

    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static Dictionary<string, TypeDefinition> WithRenderings(IReadOnlyDictionary<string, TypeDefinition> types, IReadOnlyDictionary<string, IReadOnlyList<string?>> renderings)
    {
        var result = new Dictionary<string, TypeDefinition>(SqlIdentifiers.NameComparer);
        foreach (var (key, type) in types)
        {
            result[key] = renderings.TryGetValue(key, out var list) && list.Count == type.DomainCheckConstraints.Count
                ? type with { DomainCheckConstraints = type.DomainCheckConstraints.Select((check, index) => check with { ServerRenderedCheck = list[index] }).ToArray() }
                : type;
        }

        return result;
    }
}
