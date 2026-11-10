using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>
/// <c>ALTER USER MAPPING ... OPTIONS (SET|ADD option :'name')</c>: a secret sent again to a user mapping that exists (G-132, <c>publish --apply-secrets</c>, G-336's
/// rotation option (c)). A secret is otherwise sent only when its mapping is created.
/// </summary>
public sealed record class ApplySecretsOperation : SqlOperation
{
    public required string Statement { get; init; }

    public override string GenerateSql() => Statement;

    /// <summary>
    /// One operation per user mapping of <paramref name="project"/> that <paramref name="live"/> has, the script does not create, and names one of
    /// <paramref name="variables"/> (every variable when empty).
    /// </summary>
    public static IReadOnlyList<ApplySecretsOperation> For(DatabaseModel project, DatabaseModel live, OperationBatch batch, IReadOnlyCollection<string> variables)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(variables);

        var created = batch.Operations.OfType<CreateOperation>().Select(static create => create.NodeId).ToHashSet(StringComparer.Ordinal);
        IReadOnlySet<string>? names = variables.Count == 0 ? null : new HashSet<string>(variables, StringComparer.Ordinal);
        var operations = new List<ApplySecretsOperation>();
        foreach (var mapping in project.UserMappings.Values.OrderBy(static mapping => mapping.Key, StringComparer.Ordinal))
        {
            var role = mapping.Role == PgProj.Parser.UserMappingNode.CurrentUser && live.ConnectedRole is { } connected ? connected : mapping.Role;
            if (created.Contains(mapping.Key) || !live.UserMappings.TryGetValue(ForeignDataKeys.UserMapping(role, mapping.Server), out var existing))
            {
                continue;
            }

            if (ForeignDataSql.ApplySecrets(existing!, mapping, names) is { } statement)
            {
                operations.Add(new ApplySecretsOperation { NodeId = mapping.Key, NodeKind = ChangeNodeKind.UserMapping, Statement = statement });
            }
        }

        return operations;
    }
}
