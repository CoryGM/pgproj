using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>
/// A <see cref="SqlOperation"/> that <see cref="OperationBatch.GetDestructiveOperations"/> classified as able to
/// discard data, paired with a human-readable reason per affected column or object (G-35).
/// </summary>
public sealed record class DestructiveOperation
{
    public required SqlOperation Operation { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

public sealed record class OperationBatch
{
    public required IReadOnlyList<SqlOperation> Operations { get; init; }

    public IReadOnlyList<string> PreDeploymentScripts { get; init; } = [];

    public IReadOnlyList<string> PostDeploymentScripts { get; init; } = [];

    public OperationBatch MergePreDeploymentScript(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        return this with { PreDeploymentScripts = [.. PreDeploymentScripts, script] };
    }

    public OperationBatch MergePostDeploymentScript(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        return this with { PostDeploymentScripts = [.. PostDeploymentScripts, script] };
    }

    /// <summary>
    /// Returns the operations in this batch that contain a <c>-- TODO:</c> placeholder, meaning pgproj could not
    /// produce real DDL for them (G-28). A non-empty result means the batch cannot be safely executed as-is: it
    /// is auditable in a generated script, but running it would skip the affected schema changes or send
    /// PostgreSQL an incomplete statement.
    /// <para>
    /// A placeholder is recognized at the very start of an operation's SQL for every kind, and on any line for
    /// kinds whose SQL is built only from pgproj's own templates, where a placeholder can sit inside a statement
    /// (for example <c>ADD CONSTRAINT ... -- TODO</c>) (G-44). Functions, procedures, triggers, and views embed the
    /// user's own text, which may legitimately contain a <c>-- TODO:</c> comment, so only their start is checked.
    /// Informational notes use <c>-- NOTE:</c> and are never placeholders.
    /// </para>
    /// </summary>
    public IReadOnlyList<SqlOperation> GetUnsupportedOperations()
        => Operations.Where(static operation => ContainsPlaceholder(operation)).ToArray();

    private const string PlaceholderPrefix = "-- TODO:";

    private static bool ContainsPlaceholder(SqlOperation operation)
    {
        var sql = operation.GenerateSql();
        if (sql.TrimStart().StartsWith(PlaceholderPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        return IsTemplateOnly(operation.NodeKind)
            && sql.ReplaceLineEndings("\n")
                .Split('\n')
                .Any(static line => line.TrimStart().StartsWith(PlaceholderPrefix, StringComparison.Ordinal));
    }

    private static bool IsTemplateOnly(ChangeNodeKind nodeKind)
        => nodeKind is ChangeNodeKind.Table
            or ChangeNodeKind.Index
            or ChangeNodeKind.Constraint
            or ChangeNodeKind.Type
            or ChangeNodeKind.Sequence
            or ChangeNodeKind.Grant
            or ChangeNodeKind.Owner;

    /// <summary>
    /// Returns the operations in this batch that can discard data or access that existed before they ran (G-35,
    /// G-80): a dropped table, a dropped materialized view (its computed data is lost until it is refreshed), a
    /// dropped extension (every object it owns is dropped with it), a table alter that removes a column or changes
    /// a column's type, and a genuinely revoked grant or role membership (an access-loss event, not a re-grant —
    /// see <see cref="ClassifyDestructive"/>). A non-empty result means <c>publish --execute</c> should refuse to
    /// run the batch unless the caller explicitly opts in (<c>--allow-destructive</c>); the script remains
    /// available for inspection via <c>--output</c>/<c>--dry-run</c> either way. This is a distinct concern from
    /// <see cref="GetUnsupportedOperations"/>: that method finds operations pgproj could not turn into real SQL at
    /// all, while this one finds operations that produced real, executable SQL which can still discard data or
    /// access. Indexes, constraints, plain views, enum/composite/range types, functions, procedures, triggers, and
    /// sequences are rebuilt from source on a drop-and-recreate and so are not reported here, even though they are
    /// also dropped and recreated. A changed partition strategy is deliberately excluded too: it stays hard-blocked
    /// by G-28's unconditional <c>-- TODO</c> placeholder, with no <c>--allow-destructive</c> escape hatch (G-80,
    /// scoped with the user 2026-09-23: partitioning was intentionally left out of this gap).
    /// </summary>
    public IReadOnlyList<DestructiveOperation> GetDestructiveOperations()
    {
        // A modified grant is redeployed as REVOKE-then-GRANT for the same NodeId (see ScriptGenerator.
        // AddModifiedGrantOperations) — that REVOKE is not an access-loss event, since the GRANT right after it
        // restores the access (possibly narrower, but DiffEngine already reports the modification itself; this
        // check only guards a grant/role membership that disappears with nothing replacing it).
        var recreatedNodeIds = Operations
            .OfType<CreateOperation>()
            .Where(static create => create.NodeKind is ChangeNodeKind.Grant or ChangeNodeKind.EventTrigger or ChangeNodeKind.Cast or ChangeNodeKind.Operator or ChangeNodeKind.Rule or ChangeNodeKind.Aggregate or ChangeNodeKind.Publication)
            .Select(static create => create.NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Operations
            .Select(operation => ClassifyDestructive(operation, recreatedNodeIds))
            .Where(static classified => classified is not null)
            .Select(static classified => classified!)
            .ToArray();
    }

    private static DestructiveOperation? ClassifyDestructive(SqlOperation operation, IReadOnlySet<string> recreatedNodeIds)
        => operation switch
        {
            DropOperation { Node: TableDefinition } drop
                => new DestructiveOperation { Operation = drop, Reasons = ["table dropped"] },
            DropOperation { Node: ViewDefinition { IsMaterialized: true } } drop
                => new DestructiveOperation { Operation = drop, Reasons = ["materialized view dropped; its computed data is lost until it is refreshed"] },
            DropOperation { Node: TypeDefinition type } drop when SqlOperation.IsExtension(type)
                => new DestructiveOperation { Operation = drop, Reasons = ["extension dropped; every object it owns is dropped with it"] },
            // A modified event trigger is redeployed as drop-then-create under one NodeId, which is no loss (G-127); one that only disappears stops
            // running on every DDL command it guarded, which is not something to do by accident.
            // A modified cast is dropped and created again under one NodeId, which is no loss (G-131); one that only disappears stops a conversion
            // that queries, views being planned, and functions may rely on.
            DropOperation { Node: CastDefinition cast } drop when !recreatedNodeIds.Contains(drop.NodeId)
                => new DestructiveOperation { Operation = drop, Reasons = [$"cast dropped; the conversion {cast.Name} no longer exists"] },
            // A modified operator is dropped and created again under one NodeId (G-129); one that only disappears breaks every expression, view, and index that uses it.
            // A modified rule is dropped and created again under one NodeId (G-128); one that only disappears stops rewriting its command (an INSTEAD rule on a view
            // is what lets the view be written to).
            // A modified aggregate is dropped and created again under one NodeId (G-130); one that only disappears breaks every query and view that calls it.
            // A publication that only disappears stops sending its tables' changes to every subscriber; one whose FOR ALL TABLES changes is dropped and created again (G-134).
            DropOperation { Node: PublicationDefinition publication } drop when !recreatedNodeIds.Contains(drop.NodeId)
                => new DestructiveOperation { Operation = drop, Reasons = [$"publication dropped; subscribers stop receiving what {publication.Name} sent"] },
            DropOperation { Node: AggregateDefinition aggregate } drop when !recreatedNodeIds.Contains(drop.NodeId)
                => new DestructiveOperation { Operation = drop, Reasons = [$"aggregate dropped; every query that calls {aggregate.Name} stops working"] },
            DropOperation { Node: RuleDefinition rule } drop when !recreatedNodeIds.Contains(drop.NodeId)
                => new DestructiveOperation { Operation = drop, Reasons = [$"rule dropped; {rule.Event.ToUpperInvariant()} on {rule.TableName} is no longer rewritten by {rule.Name}"] },
            DropOperation { Node: OperatorDefinition definition } drop when !recreatedNodeIds.Contains(drop.NodeId)
                => new DestructiveOperation { Operation = drop, Reasons = [$"operator dropped; every expression that uses {definition.Name} stops working"] },
            DropOperation { Node: EventTriggerDefinition eventTrigger } drop when !recreatedNodeIds.Contains(drop.NodeId)
                => new DestructiveOperation { Operation = drop, Reasons = [$"event trigger dropped; it no longer runs on {eventTrigger.EventName ?? "its event"}"] },
            DropOperation { Node: GrantDefinition grant } drop when !recreatedNodeIds.Contains(drop.NodeId)
                => new DestructiveOperation { Operation = drop, Reasons = [DescribeGrantRevocation(grant)] },
            // A changed owner takes the old owner's privileges away and puts them on the new one, which is an access-loss event for whoever relied on them (G-146);
            // an owner set on a new object (a create) is not.
            AlterOperation { Change.OldNode: OwnerDefinition oldOwner, Change.NewNode: OwnerDefinition newOwner } alter
                => new DestructiveOperation
                {
                    Operation = alter,
                    Reasons = [$"owner of {newOwner.ObjectKind} {newOwner.ObjectKey} changes from {oldOwner.OwnerRole} to {newOwner.OwnerRole}; {oldOwner.OwnerRole} loses the privileges it had as the owner"],
                },
            AlterOperation { Change.OldNode: TableDefinition oldTable, Change.NewNode: TableDefinition newTable } alter
                => AlterOperation.GetDestructiveColumnReasons(oldTable, newTable) is { Count: > 0 } reasons
                    ? new DestructiveOperation { Operation = alter, Reasons = reasons }
                    : null,
            AlterOperation { Change.OldNode: TypeDefinition oldType, Change.NewNode: TypeDefinition newType } alter
                => AlterOperation.GetDestructiveCompositeFieldReasons(oldType, newType) is { Count: > 0 } fieldReasons
                    ? new DestructiveOperation { Operation = alter, Reasons = fieldReasons }
                    : null,
            _ => null,
        };

    /// <summary>
    /// Describes a genuinely revoked grant using the exact <c>REVOKE</c> statement <see cref="GrantSqlBuilder"/>
    /// would generate for it (the same text <see cref="DropOperation.GenerateSql"/> already produces for a
    /// <see cref="GrantDefinition"/>), so the reason always matches the script. Role membership (G-61's
    /// <c>role:</c>-prefixed <see cref="GrantDefinition.ObjectKey"/>) gets its own label since "role membership
    /// revoked" is clearer than "grant revoked" for <c>REVOKE role FROM grantee</c>.
    /// </summary>
    private static string DescribeGrantRevocation(GrantDefinition grant)
    {
        var isRoleMembership = grant.ObjectKey?.StartsWith("role:", StringComparison.OrdinalIgnoreCase) == true;
        var statement = GrantSqlBuilder.Revoke(grant).TrimEnd(';');
        return isRoleMembership ? $"role membership revoked: {statement}" : $"grant revoked: {statement}";
    }
}
