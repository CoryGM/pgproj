using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy;

public sealed partial class ScriptGenerator
{
    private readonly ScriptWriter _scriptWriter = new();

    public OperationBatch Generate(ChangeSet changes, DependencyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(graph);

        var operations = new List<SqlOperation>();
        AddSchemaCreateOperations(operations, changes);

        var orderedNodeIds = graph.TopologicalSort().Select(node => node.NodeId).ToArray();
        var orderedNodeIdSet = orderedNodeIds.ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);

        // Column changes are informational: the owning table's CREATE/ALTER/DROP operation already emits them
        // (G-44), so they must never become operations of their own.
        // A declared schema with no comment to set is only created, which AddSchemaCreateOperations does (G-284).
        var addedChanges = changes.Added.Where(static change => !IsColumnChange(change.Node) && change.Node is not SchemaDefinition { Comment: null }).ToArray();
        // An extension is altered in place, and only in what the project states (G-213); a change to anything else produces no operation (G-46).
        var modifiedChanges = changes.Modified
            .Where(static change => !IsColumnChange(change.NewNode)
                && !(change.NewNode is TypeDefinition newType && SqlOperation.IsExtension(newType)
                    && (change.OldNode is not TypeDefinition oldType || AlterOperation.ExtensionAlterStatements(oldType, newType).Count == 0)))
            .ToArray();
        var removedChanges = changes.Removed.Where(static change => !IsColumnChange(change.Node)).ToArray();

        // A collation that something uses cannot be dropped to be created again (PostgreSQL refuses), so a change to one in use is not carried out (G-148).
        var changedCollationsInUse = FindChangedObjectsInUse(modifiedChanges, graph);
        foreach (var (key, users) in changedCollationsInUse)
        {
            operations.Add(BlockedChangedObject(modifiedChanges.First(change => string.Equals(change.NodeId, key, StringComparison.Ordinal)), users));
        }

        modifiedChanges = modifiedChanges.Where(change => !changedCollationsInUse.ContainsKey(change.NodeId)).ToArray();

        var addedByNodeId = addedChanges.ToDictionary(change => change.NodeId, PgProj.Parser.SqlIdentifiers.NameComparer);
        var modifiedByNodeId = modifiedChanges.ToDictionary(change => change.NodeId, PgProj.Parser.SqlIdentifiers.NameComparer);
        var removedByNodeId = removedChanges.ToDictionary(change => change.NodeId, PgProj.Parser.SqlIdentifiers.NameComparer);

        // A removed object that a surviving object is still using in the live database cannot be dropped until that object
        // has been altered to stop using it, so its drop waits for the end (G-52).
        var deferredRemovals = FindDeferredRemovals(graph, removedByNodeId.Keys, orderedNodeIdSet);

        var replaceInPlace = FindRoutinesReplacedInPlace(modifiedChanges, graph, orderedNodeIdSet);

        // The objects in the way of this publish are dropped before it and created again after it, from the project: those that use a
        // column a table alter drops or retypes (G-103), and those that use a routine it has to drop (G-105). One the project doesn't have
        // can't be recreated, so the script refuses it instead.
        var droppedRoutines = modifiedChanges
            .Where(change => change.NewNode is FunctionDefinition or ProcedureDefinition or OperatorDefinition or AggregateDefinition && IsDropAndRecreate(change, replaceInPlace))
            .Select(static change => change.NodeId)
            .ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
        var rebuilds = FindDependentRebuilds(modifiedChanges, droppedRoutines, graph);
        var forcedRebuilds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var detaches = new List<SqlOperation>();
        var reattaches = new List<SqlOperation>();
        foreach (var (key, reason) in rebuilds.Reasons)
        {
            if (removedByNodeId.ContainsKey(key))
            {
                continue;
            }

            var kind = rebuilds.Kinds[key];
            if (kind is "default" or "domain check")
            {
                if (!TryDetachAndReattach(key, kind, reason, graph, detaches, reattaches))
                {
                    operations.Add(BlockedDependent(key, kind, reason));
                }

                continue;
            }

            if (kind is not ("generated column" or "other")
                && graph.IsTargetNode(key)
                && graph.Nodes.TryGetValue(key, out var targetNode)
                && targetNode.SemanticNode is ViewDefinition or PolicyDefinition or TriggerDefinition or FunctionDefinition or ProcedureDefinition
                    or IndexDefinition or ConstraintDefinition or OperatorDefinition or RuleDefinition or AggregateDefinition)
            {
                forcedRebuilds.Add(key);
                if (!modifiedByNodeId.ContainsKey(key))
                {
                    var target = targetNode.SemanticNode;
                    modifiedByNodeId[key] = new ModifiedChange
                    {
                        NodeId = key,
                        NodeKind = ChangeRecord.MapKind(target),
                        OldNode = target is ViewDefinition targetView ? targetView with { IsMaterialized = kind == "materialized view" } : target,
                        NewNode = target,
                        Details = [],
                    };
                }
            }
            else
            {
                operations.Add(BlockedDependent(key, kind, reason));
            }
        }

        // A materialized view is dropped to be changed, and its indexes go with it, so each one the project keeps is dropped explicitly first and created
        // again after the view (G-171); left alone, the next publish would have to add them.
        var indexReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var viewChange in modifiedChanges.Where(change => change.NewNode is ViewDefinition { IsMaterialized: true } && !IsCommentOnlyChange<ViewDefinition>(change, nameof(ViewDefinition.Comment))))
        {
            foreach (var dependent in graph.GetDependents(viewChange.NodeId))
            {
                if (dependent.SemanticNode is not IndexDefinition index
                    || !graph.IsTargetNode(dependent.NodeId)
                    || addedByNodeId.ContainsKey(dependent.NodeId)
                    || removedByNodeId.ContainsKey(dependent.NodeId)
                    || !forcedRebuilds.Add(dependent.NodeId))
                {
                    continue;
                }

                indexReasons[dependent.NodeId] = $"its materialized view {viewChange.NodeId} is dropped and created again";
                if (!modifiedByNodeId.ContainsKey(dependent.NodeId))
                {
                    modifiedByNodeId[dependent.NodeId] = new ModifiedChange
                    {
                        NodeId = dependent.NodeId,
                        NodeKind = ChangeRecord.MapKind(index),
                        OldNode = index,
                        NewNode = index,
                        Details = [],
                    };
                }
            }
        }

        // PostgreSQL recorded that each object in the way uses the routine being dropped; the project may not say so (a policy's expression,
        // for one), so the order drops it first and creates it after the routine anyway.
        if (droppedRoutines.Count > 0 && AddLiveRoutineEdges(graph, forcedRebuilds, droppedRoutines))
        {
            orderedNodeIds = graph.TopologicalSort().Select(node => node.NodeId).ToArray();
        }

        bool Recreates(ModifiedChange change) => forcedRebuilds.Contains(change.NodeId) || IsDropAndRecreate(change, replaceInPlace);
        string? DropReason(string nodeId)
            => forcedRebuilds.Contains(nodeId) ? $"{nodeId} is dropped here and recreated below: {RebuildReason(nodeId)}." : null;
        string? CreateReason(string nodeId)
            => forcedRebuilds.Contains(nodeId) ? $"{nodeId} is recreated; it was dropped above because {RebuildReason(nodeId)}." : null;
        string RebuildReason(string nodeId) => rebuilds.Reasons.TryGetValue(nodeId, out var reason) ? reason : indexReasons[nodeId];

        // Defaults and domain CHECKs are detached before anything is dropped and put back once everything has been created (G-105).
        operations.AddRange(detaches);

        // Phase 1: drops, dependent-first (reverse topological order). This covers removed
        // objects and the drop-half of modified drop-and-recreate objects, so a dependency's
        // DROP is never emitted before its dependent's DROP (G-27).
        foreach (var nodeId in orderedNodeIds.Reverse())
        {
            if (!deferredRemovals.Contains(nodeId) && removedByNodeId.TryGetValue(nodeId, out var removed))
            {
                operations.Add(new DropOperation
                {
                    NodeId = removed.NodeId,
                    NodeKind = removed.NodeKind,
                    Node = removed.Node,
                });
            }

            if (modifiedByNodeId.TryGetValue(nodeId, out var modified) && Recreates(modified))
            {
                operations.Add(new DropOperation
                {
                    NodeId = modified.NodeId,
                    NodeKind = modified.NodeKind,
                    Node = modified.OldNode,
                    Reason = DropReason(modified.NodeId),
                });
            }
        }

        // Phase 2: creates and alters, dependency-first (forward topological order). This
        // covers added objects, the create-half of modified drop-and-recreate objects, and
        // all other modification kinds (view/grant/alter).
        foreach (var nodeId in orderedNodeIds)
        {
            if (addedByNodeId.TryGetValue(nodeId, out var added) && IsMadeByTablesLike(added.Node, addedByNodeId))
            {
                // The table's LIKE clause makes this copy when the table is created (G-221); a comment the copy has is set after it, as it is after a constraint.
                operations.AddRange(CommentOfLikeCopy(added.Node, added.NodeId, added.NodeKind, graph));
            }
            else if (added is not null)
            {
                operations.Add(new CreateOperation
                {
                    NodeId = added.NodeId,
                    NodeKind = added.NodeKind,
                    Node = added.Node,
                });
                operations.AddRange(CommentAfterCreate(added.Node, added.NodeId, added.NodeKind, graph, replaced: false));
            }

            if (modifiedByNodeId.TryGetValue(nodeId, out var modified))
            {
                if (Recreates(modified))
                {
                    var create = new CreateOperation
                    {
                        NodeId = modified.NodeId,
                        NodeKind = modified.NodeKind,
                        Node = modified.NewNode,
                        Reason = CreateReason(modified.NodeId),
                    };

                    // A CHECK put back around a routine's drop is added NOT VALID and then validated, so the table is scanned without holding
                    // the lock a plain ADD CONSTRAINT would (G-105, decision 3).
                    // A NOT ENFORCED CHECK (PostgreSQL 18) has nothing to validate, so it is added as it is (G-121).
                    operations.AddRange(forcedRebuilds.Contains(modified.NodeId) && modified.NewNode is ConstraintDefinition { ConstraintType: { } type, IsEnforced: true } check
                        && string.Equals(type.Trim(), "CHECK", StringComparison.OrdinalIgnoreCase)
                            ? ReaddCheck(create, check)
                            : [create]);
                    operations.AddRange(CommentAfterCreate(modified.NewNode, modified.NodeId, modified.NodeKind, graph, replaced: true));
                }
                else
                {
                    AddModifiedOperations(operations, modified, replaceInPlace);
                }
            }
        }

        operations.AddRange(reattaches);

        // Phase 3: the drops that had to wait for an alter (G-52), still dependent-first among themselves.
        foreach (var nodeId in orderedNodeIds.Reverse())
        {
            if (deferredRemovals.Contains(nodeId) && removedByNodeId.TryGetValue(nodeId, out var deferred))
            {
                operations.Add(new DropOperation
                {
                    NodeId = deferred.NodeId,
                    NodeKind = deferred.NodeKind,
                    Node = deferred.Node,
                });
            }
        }

        AddUnorderedOperations(operations, addedChanges, orderedNodeIdSet, static addedChange => new CreateOperation
        {
            NodeId = addedChange.NodeId,
            NodeKind = addedChange.NodeKind,
            Node = addedChange.Node,
        });

        foreach (var modified in modifiedChanges)
        {
            if (orderedNodeIdSet.Contains(modified.NodeId))
            {
                continue;
            }

            AddModifiedOperations(operations, modified, replaceInPlace);
        }

        AddUnorderedOperations(operations, removedChanges, orderedNodeIdSet, static removedChange => new DropOperation
        {
            NodeId = removedChange.NodeId,
            NodeKind = removedChange.NodeKind,
            Node = removedChange.Node,
        });

        // A dropped and recreated object loses its privileges; the grants the project keeps on it are issued again after it is created
        // (G-116). A grant being added, removed, or changed in this publish already has its own operation.
        var changedGrantIds = addedByNodeId.Keys.Concat(removedByNodeId.Keys).Concat(modifiedByNodeId.Keys).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
        operations = InsertRegrants(operations, graph, changedGrantIds);

        return new OperationBatch
        {
            Operations = AppendInternalTriggerStates(DeferSequenceOwnership(BreakViewCycles(operations, graph)), changes, graph),
        };
    }

    /// <summary>
    /// <c>ALTER TABLE t { ENABLE | DISABLE } TRIGGER ALL</c> for each table whose internal (foreign-key) trigger state the project states and the database does not have (G-261), at the end of the
    /// script: the internal triggers exist only after every foreign key that involves the table is created, and <c>ALL</c> also reaches the declared triggers, whose own states are written again.
    /// </summary>
    private static List<SqlOperation> AppendInternalTriggerStates(List<SqlOperation> operations, ChangeSet changes, DependencyGraph graph)
    {
        var tables = changes.Added
            .Where(static added => added.Node is TableDefinition { InternalTriggerState: "DISABLE" })
            .Select(static added => (added.NodeId, added.NodeKind, Table: (TableDefinition)added.Node))
            .Concat(changes.Modified
                .Where(static modified => modified.NewNode is TableDefinition && modified.Details.Any(static detail => detail.PropertyName == nameof(TableDefinition.InternalTriggerState)))
                .Select(static modified => (modified.NodeId, modified.NodeKind, Table: (TableDefinition)modified.NewNode)))
            .ToArray();
        if (tables.Length == 0)
        {
            return operations;
        }

        var triggers = graph.Nodes.Values.Select(static node => node.SemanticNode).OfType<TriggerDefinition>().ToArray();
        foreach (var (nodeId, nodeKind, table) in tables)
        {
            var state = table.InternalTriggerState ?? "ENABLE";
            operations.Add(new InternalTriggerStateOperation
            {
                NodeId = nodeId,
                NodeKind = nodeKind,
                Table = table,
                Triggers = [.. triggers
                    .Where(trigger => string.Equals(trigger.TableName, table.Name, StringComparison.Ordinal) && string.Equals(trigger.SchemaName, table.SchemaName, StringComparison.Ordinal)
                        && !string.Equals(trigger.EnabledState ?? "ENABLE", state, StringComparison.Ordinal))
                    .OrderBy(static trigger => trigger.Name, StringComparer.Ordinal)],
                Reason = "the table's internal (foreign-key) triggers are enabled or disabled after every foreign key exists",
            });
        }

        return operations;
    }

    /// <summary>
    /// Issues again, after a dropped object is created anew, each grant the project keeps on it (G-116). PostgreSQL drops an object's
    /// privileges with it, and the grant is unchanged between the project and the database, so the diff has no operation for it: before
    /// this, a materialized view (or a trigger function, or a type) that was dropped and recreated came back with its comment but
    /// without its grants, and the next <c>diff</c> reported them missing. A grant over a whole schema (<c>ALL TABLES IN SCHEMA</c>)
    /// that covers the object is issued again too; it is idempotent for the objects it already covered. Only the project's grants are
    /// issued: one only the database has (a <c>GRANT ... TO PUBLIC</c> the project revokes) must not come back. Role memberships, schema
    /// grants, and default privileges name no such object.
    /// </summary>
    /// <summary>The live privileges on <paramref name="node"/>, or over the whole schema for its kind, of a project that leaves grants out.</summary>
    private static IEnumerable<UnmodeledPrivilege> UnmodeledPrivilegesOn(ISemanticNode node, IReadOnlyList<UnmodeledPrivilege> privileges)
    {
        var kind = node switch
        {
            TableDefinition or ViewDefinition => "table",
            SequenceDefinition => "sequence",
            FunctionDefinition => "function",
            ProcedureDefinition => "procedure",
            _ => null,
        };

        return kind is null
            ? []
            : privileges.Where(privilege => privilege.ObjectKind == kind
                && string.Equals(privilege.SchemaName, node.SchemaName, StringComparison.Ordinal)
                && (privilege.Name is null || string.Equals(privilege.Name, node.Name, StringComparison.Ordinal)));
    }

    private static List<SqlOperation> InsertRegrants(List<SqlOperation> operations, DependencyGraph graph, ISet<string> changedGrantIds)
    {
        var dropped = operations
            .OfType<DropOperation>()
            .Where(static operation => operation.NodeKind is not (ChangeNodeKind.Grant or ChangeNodeKind.Owner))
            .Select(static operation => operation.NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (dropped.Count == 0)
        {
            return operations;
        }

        var regranted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SqlOperation>(operations.Count);
        foreach (var operation in operations)
        {
            result.Add(operation);
            if (operation is not CreateOperation create || !dropped.Contains(create.NodeId))
            {
                continue;
            }

            foreach (var grant in graph.GetDependents(create.NodeId).Select(static node => node.SemanticNode).OfType<GrantDefinition>())
            {
                if (changedGrantIds.Contains(grant.Key) || !graph.IsTargetNode(grant.Key) || !regranted.Add(grant.Key))
                {
                    continue;
                }

                result.Add(new CreateOperation
                {
                    NodeId = grant.Key,
                    NodeKind = ChangeNodeKind.Grant,
                    Node = grant,
                });
            }

            // When the project states an owner for the object, that one is set below instead (G-146).
            var projectStatesOwner = graph.GetDependents(create.NodeId).Select(static node => node.SemanticNode).OfType<OwnerDefinition>().Any(stated => graph.IsTargetNode(stated.Key));

            // A recreated view is owned by the role that deploys; it keeps the owner it had when that was another role (G-103, decision 7).
            if (!projectStatesOwner
                && create.Node is ViewDefinition view
                && graph.ReplacedOwners.TryGetValue(create.NodeId, out var owner)
                && !string.Equals(owner, graph.ReplacedConnectedRole, StringComparison.Ordinal))
            {
                result.Add(new PreservedStatementOperation
                {
                    NodeId = create.NodeId,
                    NodeKind = ChangeNodeKind.View,
                    Statement = $"ALTER {(view.IsMaterialized ? "MATERIALIZED VIEW" : "VIEW")} {QuoteQualified(view.SchemaName, view.Name)} OWNER TO {Quote(owner)}",
                    Reason = $"{create.NodeId} keeps the owner it had ({owner}).",
                });
            }

            if (!projectStatesOwner
                && create.Node is FunctionDefinition or ProcedureDefinition
                && graph.ReplacedOwners.TryGetValue(create.NodeId, out var routineOwner)
                && !string.Equals(routineOwner, graph.ReplacedConnectedRole, StringComparison.Ordinal))
            {
                var (keyword, signature) = create.Node switch
                {
                    FunctionDefinition function => ("FUNCTION", function.Signature?.RenderArgumentTypeSignatureSql() ?? string.Empty),
                    ProcedureDefinition procedure => ("PROCEDURE", procedure.ArgumentIdentitySql),
                    _ => (string.Empty, string.Empty),
                };
                result.Add(new PreservedStatementOperation
                {
                    NodeId = create.NodeId,
                    NodeKind = create.NodeKind,
                    Statement = $"ALTER {keyword} {QuoteQualified(create.Node.SchemaName, create.Node.Name)}({signature}) OWNER TO {Quote(routineOwner)}",
                    Reason = $"{create.NodeId} keeps the owner it had ({routineOwner}).",
                });
            }

            // A recreated object is owned by the role that deploys; the owner the project states for it is set again (G-146), in place of the owner the
            // object had above.
            foreach (var statedOwner in graph.GetDependents(create.NodeId).Select(static node => node.SemanticNode).OfType<OwnerDefinition>())
            {
                if (changedGrantIds.Contains(statedOwner.Key) || !graph.IsTargetNode(statedOwner.Key) || !regranted.Add(statedOwner.Key))
                {
                    continue;
                }

                result.Add(new CreateOperation
                {
                    NodeId = statedOwner.Key,
                    NodeKind = ChangeNodeKind.Owner,
                    Node = statedOwner,
                    Reason = $"{create.NodeId} was dropped and recreated above; its owner is set again.",
                });
            }

            // A project that leaves grants out (G-87) keeps whatever the database had on the object.
            foreach (var privilege in UnmodeledPrivilegesOn(create.Node, graph.ReplacedUnmodeledPrivileges))
            {
                if (regranted.Add(privilege.Statement))
                {
                    result.Add(new PreservedStatementOperation
                    {
                        NodeId = create.NodeId,
                        NodeKind = ChangeNodeKind.Grant,
                        Statement = privilege.Statement,
                        Reason = $"{create.NodeId} was dropped and recreated above; this privilege it had is restored (grants are not modeled).",
                    });
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The removed objects whose drop must wait until after the creates and alters (G-52): a removed object that some surviving
    /// object used to depend on and no longer does (<see cref="DependencyGraph.GetRetiredDependents"/>). The survivor is altered
    /// (or dropped and recreated) to stop using it, and PostgreSQL refuses the drop until that has happened
    /// (<c>2BP01</c>). The removed objects it depends on wait too, so the dependent-first order among the deferred drops is
    /// kept; a removed object nothing surviving used is dropped early exactly as before (G-27).
    /// </summary>
    private static HashSet<string> FindDeferredRemovals(DependencyGraph graph, IEnumerable<string> removedIds, IReadOnlySet<string> orderedNodeIds)
    {
        var removed = removedIds.Where(orderedNodeIds.Contains).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
        var deferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();

        foreach (var removedId in removed)
        {
            if (graph.GetRetiredDependents(removedId).Any(dependentId => !removed.Contains(dependentId)) && deferred.Add(removedId))
            {
                pending.Enqueue(removedId);
            }
        }

        while (pending.Count > 0)
        {
            foreach (var dependency in graph.GetDependencies(pending.Dequeue()))
            {
                if (removed.Contains(dependency.NodeId) && deferred.Add(dependency.NodeId))
                {
                    pending.Enqueue(dependency.NodeId);
                }
            }
        }

        return deferred;
    }

    /// <summary>
    /// Two views that refer to each other (G-254) are created by making one of them from its first definition, which does not refer to the other, then the other, then replacing the first
    /// by its real body with <c>CREATE OR REPLACE VIEW</c>: the way <c>pg_dump</c> breaks the cycle with a placeholder.
    /// </summary>
    private static List<SqlOperation> BreakViewCycles(List<SqlOperation> operations, DependencyGraph graph)
    {
        if (graph.SplitViews.Count == 0)
        {
            return operations;
        }

        var result = new List<SqlOperation>(operations);
        foreach (var (viewId, partners) in graph.SplitViews)
        {
            var index = result.FindIndex(operation => operation is CreateOperation { Node: ViewDefinition view } && string.Equals(view.Key, viewId, StringComparison.Ordinal));
            if (index < 0 || ((CreateOperation)result[index]).Node is not ViewDefinition { InitialDefinition: { } initial } real)
            {
                continue;
            }

            var create = (CreateOperation)result[index];
            result[index] = create with { Node = initial with { Comment = null, ColumnComments = ColumnCommentMap.Empty } };

            // The real body goes in after the last of the views it was waiting for.
            var last = index;
            for (var i = index + 1; i < result.Count; i++)
            {
                if (result[i] is CreateOperation { Node: ViewDefinition partner } && partners.Contains(partner.Key, PgProj.Parser.SqlIdentifiers.NameComparer))
                {
                    last = i;
                }
            }

            result.Insert(last + 1, create with { Node = real with { InitialDefinition = null } });
        }

        return result;
    }

    /// <summary>
    /// A sequence owned by a column (a <c>serial</c> column's, or an explicit <c>OWNED BY</c>) is created without
    /// ownership and given to its column by a trailing <c>ALTER SEQUENCE ... OWNED BY</c>. Created inline, the clause
    /// fails (<c>42P01</c>) whenever the table does not exist yet, which is always the case for a serial column: its
    /// default needs the sequence to exist before the table can be created (G-58).
    /// </summary>
    private static List<SqlOperation> DeferSequenceOwnership(List<SqlOperation> operations)
    {
        var result = new List<SqlOperation>(operations.Count);
        var ownership = new List<SqlOperation>();
        foreach (var operation in operations)
        {
            if (operation is CreateOperation { Node: SequenceDefinition sequence } create
                && !sequence.IsIdentityOwned
                && !string.IsNullOrWhiteSpace(sequence.OwnedByTable)
                && !string.IsNullOrWhiteSpace(sequence.OwnedByColumn))
            {
                result.Add(create with { Node = sequence with { OwnedByTable = null, OwnedByColumn = null } });
                ownership.Add(new SequenceOwnershipOperation
                {
                    NodeId = operation.NodeId,
                    NodeKind = operation.NodeKind,
                    Sequence = sequence,
                });
                continue;
            }

            result.Add(operation);
        }

        // PostgreSQL refuses OWNED BY when the sequence and the table have different owners (55000), and an owner statement changes the table's owner (and, with it, the owner of the
        // sequences it already owns). So the sequences are handed to their columns before the first owner statement, not after the last (G-242).
        var firstOwner = result.FindIndex(static operation => operation.NodeKind == ChangeNodeKind.Owner);
        if (firstOwner < 0)
        {
            result.AddRange(ownership);
        }
        else
        {
            result.InsertRange(firstOwner, ownership);
        }

        return result;
    }

    public OperationBatch Generate(
        ChangeSet changes,
        DependencyGraph graph,
        string? preDeploymentScript,
        string? postDeploymentScript)
    {
        var batch = Generate(changes, graph);
        if (!string.IsNullOrWhiteSpace(preDeploymentScript))
        {
            batch = batch.MergePreDeploymentScript(preDeploymentScript);
        }

        if (!string.IsNullOrWhiteSpace(postDeploymentScript))
        {
            batch = batch.MergePostDeploymentScript(postDeploymentScript);
        }

        return batch;
    }

    public string GenerateSqlScript(OperationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return _scriptWriter.Write(batch);
    }

    /// <summary>
    /// Scripted objects are redeployed by dropping the old object and replaying the user's
    /// original script, which is the only safe option when a routine signature changes.
    /// Views use their own rule (see <see cref="AddModifiedViewOperations"/>). Indexes,
    /// constraints, and types are effectively immutable in PostgreSQL (no in-place ALTER covers
    /// arbitrary structural changes), so they are also redeployed via drop-then-recreate (see
    /// <see cref="IsDropAndRecreateObject"/>). Grants have no DDL identity to alter in place, so a
    /// modified grant is redeployed as REVOKE-then-GRANT (see <see cref="AddModifiedGrantOperations"/>).
    /// All other kinds keep the existing alter path.
    /// </summary>
    private static void AddModifiedOperations(ICollection<SqlOperation> operations, ModifiedChange modified, IReadOnlySet<string> replaceInPlace)
    {
        if (replaceInPlace.Contains(modified.NodeId))
        {
            operations.Add(new CreateOperation
            {
                NodeId = modified.NodeId,
                NodeKind = modified.NodeKind,
                Node = modified.NewNode,
                PreviousComment = GetComment(modified.OldNode),
            });

            return;
        }

        if ((IsScriptedObject(modified.NodeKind) && !IsChangeOnlyTo<TriggerDefinition>(modified, nameof(TriggerDefinition.Comment), nameof(TriggerDefinition.EnabledState), nameof(TriggerDefinition.PartitionStates))) || IsDropAndRecreateObject(modified))
        {
            operations.Add(new DropOperation
            {
                NodeId = modified.NodeId,
                NodeKind = modified.NodeKind,
                Node = modified.OldNode,
            });

            operations.Add(new CreateOperation
            {
                NodeId = modified.NodeId,
                NodeKind = modified.NodeKind,
                Node = modified.NewNode,
            });

            return;
        }

        if (modified.NodeKind == ChangeNodeKind.View)
        {
            AddModifiedViewOperations(operations, modified);
            return;
        }

        if (modified.NodeKind == ChangeNodeKind.Grant)
        {
            AddModifiedGrantOperations(operations, modified);
            return;
        }

        // Only the internal triggers' state changed: it is written at the end of the script (G-261), so the table has nothing to alter here.
        if (IsChangeOnlyTo<TableDefinition>(modified, nameof(TableDefinition.InternalTriggerState)))
        {
            return;
        }

        operations.Add(new AlterOperation
        {
            NodeId = modified.NodeId,
            NodeKind = modified.NodeKind,
            Change = modified,
        });
    }

    /// <summary>
    /// A modified grant has no stable DDL identity to alter in place (a changed privilege list,
    /// grantee, or object key is really a different grant/revoke pair), so it is redeployed as
    /// <c>REVOKE</c> of the old grant followed by <c>GRANT</c> of the new one, reusing the same
    /// <see cref="DropOperation"/>/<see cref="CreateOperation"/> SQL paths added for <c>G-15</c>.
    /// </summary>
    private static void AddModifiedGrantOperations(ICollection<SqlOperation> operations, ModifiedChange modified)
    {
        operations.Add(new DropOperation
        {
            NodeId = modified.NodeId,
            NodeKind = modified.NodeKind,
            Node = modified.OldNode,
        });

        operations.Add(new CreateOperation
        {
            NodeId = modified.NodeId,
            NodeKind = modified.NodeKind,
            Node = modified.NewNode,
        });
    }

    /// <summary>
    /// Non-materialized views with an unchanged output column list can be redeployed in place
    /// with <c>CREATE OR REPLACE VIEW</c>, which <see cref="CreateOperation"/> already emits for
    /// non-materialized views. PostgreSQL rejects <c>CREATE OR REPLACE VIEW</c> when the column
    /// list changes, and never supports replacing a materialized view at all, so both cases fall
    /// back to drop-then-recreate.
    /// </summary>
    private static void AddModifiedViewOperations(ICollection<SqlOperation> operations, ModifiedChange modified)
    {
        // A materialized view has no CREATE OR REPLACE; every other change drops and recreates it (losing what it computed
        // and needs a REFRESH to rebuild), so a comment-only change takes the ALTER path instead (G-34, the same reasoning as
        // G-53 for types).
        if (modified.NewNode is ViewDefinition { IsMaterialized: true } && IsChangeOnlyTo<ViewDefinition>(modified, nameof(ViewDefinition.Comment), nameof(ViewDefinition.ColumnComments), nameof(ViewDefinition.StorageOptions), nameof(ViewDefinition.TablespaceName), nameof(ViewDefinition.AccessMethod)))
        {
            operations.Add(new AlterOperation
            {
                NodeId = modified.NodeId,
                NodeKind = modified.NodeKind,
                Change = modified,
            });

            return;
        }

        // Output columns that are the live ones renamed (a table column the view selects was renamed) are renamed first and the view replaced (G-276).
        var columnRenames = modified.OldNode is ViewDefinition { IsMaterialized: false } renamedOld && modified.NewNode is ViewDefinition { IsMaterialized: false } renamedNew
            ? ViewColumnRenames.Plan(renamedOld.ServerOutputColumns, renamedNew.ServerOutputColumns) ?? []
            : [];
        var requiresDropAndCreate = modified.OldNode is not ViewDefinition { IsMaterialized: false } oldView
            || modified.NewNode is not ViewDefinition { IsMaterialized: false } newView
            || (columnRenames.Count == 0 && !oldView.OutputColumnNames.SequenceEqual(newView.OutputColumnNames, StringComparer.Ordinal));

        if (requiresDropAndCreate)
        {
            operations.Add(new DropOperation
            {
                NodeId = modified.NodeId,
                NodeKind = modified.NodeKind,
                Node = modified.OldNode,
            });
        }

        operations.Add(new CreateOperation
        {
            NodeId = modified.NodeId,
            NodeKind = modified.NodeKind,
            Node = modified.NewNode,
            // A drop already clears whatever comment the object carried; only a replace-in-place needs an explicit clear (G-34).
            PreviousComment = requiresDropAndCreate ? null : GetComment(modified.OldNode),
            PreviousColumnComments = requiresDropAndCreate ? null : RenamedColumnComments((modified.OldNode as ViewDefinition)?.ColumnComments, columnRenames),
            PreviousColumnDefaults = requiresDropAndCreate ? null : RenamedColumnComments((modified.OldNode as ViewDefinition)?.ColumnDefaults, columnRenames),
            ViewColumnRenames = requiresDropAndCreate ? [] : columnRenames,
        });
    }

    /// <summary>A view's column comments under the names its columns are renamed to (G-276), since a rename keeps the comment.</summary>
    private static IReadOnlyDictionary<string, string>? RenamedColumnComments(IReadOnlyDictionary<string, string>? comments, IReadOnlyList<(string From, string To)> renames)
    {
        if (comments is null || renames.Count == 0)
        {
            return comments;
        }

        var result = new Dictionary<string, string>(comments, PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var (from, to) in renames)
        {
            if (result.Remove(ViewColumnRenames.Unquote(from), out var comment))
            {
                result[ViewColumnRenames.Unquote(to)] = comment;
            }
        }

        return result;
    }

    /// <summary>The comment carried by a view, function, or procedure definition, or null for any other node kind or when it has none.</summary>
    private static string? GetComment(ISemanticNode node)
        => node switch
        {
            ViewDefinition view => view.Comment,
            FunctionDefinition function => function.Comment,
            ProcedureDefinition procedure => procedure.Comment,
            _ => null,
        };

    /// <summary>
    /// A constraint, index, or statistics object that a table's <c>LIKE</c> copies (G-221), or a constraint written in a partition's <c>PARTITION OF parent (...)</c> list (G-266),
    /// whose table is created by the same script: its <c>CREATE TABLE</c>, which deploys the clause as written, makes it, so it is not created again.
    /// </summary>
    private static bool IsMadeByTablesLike(ISemanticNode node, IReadOnlyDictionary<string, AddedChange> addedByNodeId)
    {
        var (copiedBy, schemaName, tableName) = node switch
        {
            ConstraintDefinition constraint => (constraint.CopiedByLike, constraint.SchemaName, constraint.TableName),
            IndexDefinition index => (index.CopiedByLike, index.SchemaName, index.TableName),
            StatisticsDefinition statistics => (statistics.CopiedByLike, statistics.TableSchemaName, statistics.TableName),
            _ => (null, null, null),
        };
        // A constraint written in a partition's element list is made the same way, by the CREATE TABLE that deploys the list (G-266).
        if (node is ConstraintDefinition { IsInPartitionOfList: true })
        {
            copiedBy = string.Empty;
        }

        return copiedBy is not null && tableName is not null
            && addedByNodeId.TryGetValue(DatabaseModel.CreateKey(schemaName, tableName), out var table)
            && table.Node is TableDefinition;
    }

    private static SqlOperation[] CommentOfLikeCopy(ISemanticNode node, string nodeId, ChangeNodeKind kind, DependencyGraph graph)
        => node switch
        {
            IndexDefinition { Comment: { } comment } index => [new PreservedStatementOperation { NodeId = nodeId, NodeKind = kind, Statement = CommentSqlHelper.GenerateIndexComment(index.SchemaName, index.Name, comment)! }],
            ConstraintDefinition => CommentAfterCreate(node, nodeId, kind, graph, replaced: false),
            _ => [],
        };

    private static bool IsColumnChange(ISemanticNode node)
        => node is ColumnDefinition;

    /// <summary>
    /// The modified functions and procedures that are redeployed with <c>CREATE OR REPLACE</c> alone (G-64). Dropping one first
    /// fails (<c>2BP01</c>) whenever a surviving object still uses it (a column default, a view, a constraint, an index), and it
    /// would discard the routine's grants; replacing keeps both. It requires a compatible signature
    /// (<see cref="HasCompatibleRoutineSignature"/>) and that nothing it depends on is itself being dropped and recreated: a
    /// routine that uses a type that is dropped has to be dropped before it, or PostgreSQL refuses the type's drop.
    /// </summary>
    private static HashSet<string> FindRoutinesReplacedInPlace(IEnumerable<ModifiedChange> modifiedChanges, DependencyGraph graph, IReadOnlySet<string> orderedNodeIds)
    {
        var changes = modifiedChanges.ToArray();
        var dropped = changes
            .Where(change => (IsScriptedObject(change.NodeKind) && !HasCompatibleRoutineSignature(change)) || IsDropAndRecreateObject(change))
            .Select(static change => change.NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return changes
            .Where(change => IsScriptedObject(change.NodeKind)
                && HasCompatibleRoutineSignature(change)
                && (!orderedNodeIds.Contains(change.NodeId) || !graph.GetDependencies(change.NodeId).Any(dependency => dropped.Contains(dependency.NodeId))))
            .Select(static change => change.NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for a modified function or procedure whose parameters and return type are unchanged. PostgreSQL refuses
    /// <c>OR REPLACE</c> when the return type, or a parameter's name, mode, or type, changes, or a default is removed, or a function
    /// becomes a window function or stops being one (G-197), so anything but an identical parameter list, return type, and kind is dropped
    /// and recreated. Triggers have no <c>OR REPLACE</c> form here.
    /// </summary>
    private static bool HasCompatibleRoutineSignature(ModifiedChange modified)
    {
        // Compared as the diff compares them (G-66): the old side is usually read from the live catalog and spells types and
        // defaults differently from the project (character varying and varchar(10), 'x'::text and 'x'), which is not a change.
        static bool SameParameters(PgProj.Parser.RoutineSignature? oldSignature, PgProj.Parser.RoutineSignature? newSignature)
            => oldSignature is not null
                && newSignature is not null
                && RoutineSignatureComparison.Parameters(oldSignature).SequenceEqual(RoutineSignatureComparison.Parameters(newSignature), StringComparer.Ordinal);

        return (modified.OldNode, modified.NewNode) switch
        {
            (FunctionDefinition oldFunction, FunctionDefinition newFunction) =>
                SameParameters(oldFunction.Signature, newFunction.Signature)
                && string.Equals(RoutineSignatureComparison.Result(oldFunction.Signature), RoutineSignatureComparison.Result(newFunction.Signature), StringComparison.Ordinal)
                && RoutineSignatureComparison.IsWindow(oldFunction.Signature) == RoutineSignatureComparison.IsWindow(newFunction.Signature),
            (ProcedureDefinition oldProcedure, ProcedureDefinition newProcedure) => SameParameters(oldProcedure.Signature, newProcedure.Signature),
            _ => false,
        };
    }

    private static bool IsScriptedObject(ChangeNodeKind nodeKind)
        => nodeKind is ChangeNodeKind.Function or ChangeNodeKind.Procedure or ChangeNodeKind.Trigger;

    /// <summary>
    /// True for modification kinds whose drop and create halves are phased separately by
    /// <see cref="Generate(ChangeSet, DependencyGraph)"/> (dependent-first drops, then
    /// dependency-first creates) so that a shared dependency is never dropped before its
    /// dependent's drop has been emitted (G-27). This is the union of
    /// <see cref="IsScriptedObject"/> and <see cref="IsDropAndRecreateObject"/>.
    /// </summary>
    private static bool IsDropAndRecreate(ModifiedChange modified, IReadOnlySet<string> replaceInPlace)
        => (IsScriptedObject(modified.NodeKind) && !replaceInPlace.Contains(modified.NodeId) && !IsChangeOnlyTo<TriggerDefinition>(modified, nameof(TriggerDefinition.Comment), nameof(TriggerDefinition.EnabledState), nameof(TriggerDefinition.PartitionStates))) || IsDropAndRecreateObject(modified);

    /// <summary>
    /// Indexes, constraints, and types have no general-purpose <c>ALTER</c> statement that covers
    /// arbitrary structural changes in PostgreSQL (index definitions are immutable, constraints can
    /// only be dropped and re-added, and type attribute/range changes are narrow special cases), so
    /// a modification of any of these kinds is redeployed as drop-then-recreate. The one exception is
    /// an enum type whose new label list is a strict, ordered superset of the old one (labels only
    /// appended, none removed or reordered) and nothing else structurally changed — that case is
    /// deployable in-place via <c>ALTER TYPE ... ADD VALUE</c> (see <see cref="IsAdditiveEnumChange"/>)
    /// and is routed through <see cref="AlterOperation"/> instead (G-31). A type whose only difference is its
    /// comment is also altered in place, with <c>COMMENT ON TYPE</c> (G-53).
    /// </summary>
    private static bool IsDropAndRecreateObject(ModifiedChange modified)
        => modified.NodeKind switch
        {
            // A change to only the storage parameters (and the comment) is made in place with ALTER INDEX ... SET/RESET (G-195).
            ChangeNodeKind.Index => !IsChangeOnlyTo<IndexDefinition>(modified, nameof(IndexDefinition.Name), nameof(IndexDefinition.Comment), nameof(IndexDefinition.StorageOptions), nameof(IndexDefinition.IsClustered), nameof(IndexDefinition.IsReplicaIdentity)),
            // A constraint is dropped and added again for any change but its comment, which COMMENT ON CONSTRAINT changes in place (G-161).
            // A name a column rename changed (G-289) is renamed in place too.
            ChangeNodeKind.Constraint => !IsChangeOnlyTo<ConstraintDefinition>(modified, nameof(ConstraintDefinition.Name), nameof(ConstraintDefinition.Comment), nameof(ConstraintDefinition.IndexComment), nameof(ConstraintDefinition.IndexStorageOptions), nameof(ConstraintDefinition.IsNotValid), nameof(ConstraintDefinition.IsClustered), nameof(ConstraintDefinition.IsReplicaIdentity)),
            // ALTER POLICY can only change roles/USING/WITH CHECK, never the command or PERMISSIVE/RESTRICTIVE kind, so a
            // modified policy is redeployed as drop-then-recreate unconditionally, matching a constraint (G-76).
            // A policy whose only change is its comment is changed in place with COMMENT ON POLICY (G-164).
            ChangeNodeKind.Policy => !IsCommentOnlyChange<PolicyDefinition>(modified, nameof(PolicyDefinition.Comment)),
            // An event trigger has no ALTER for its event, filter, or function, only for whether it is enabled, so a modified one is redeployed as
            // drop-then-create, matching a policy (G-127).
            ChangeNodeKind.EventTrigger => true,
            // A cast cannot be altered at all (its method, function, and context are fixed when it is created), so a modified one is
            // dropped and created again (G-131).
            ChangeNodeKind.Cast => true,

            // An operator's function, estimators, links, and flags can be changed by ALTER OPERATOR only in part (RESTRICT and JOIN before PostgreSQL 17, and never the
            // function), so a modified one is dropped and created again (G-129).
            ChangeNodeKind.Operator => true,

            // A rule's event and body are fixed when it is created, and dropping one is harmless, so a modified rule is dropped and created again (G-128).
            // Except that its firing mode is changed in place (G-257).
            ChangeNodeKind.Rule => !IsChangeOnlyTo<RuleDefinition>(modified, nameof(RuleDefinition.EnabledState)),

            // An aggregate has no ALTER for its functions, types, or options, so a modified one is dropped and created again (G-130).
            ChangeNodeKind.Aggregate => true,

            // A collation has no ALTER for its provider, locale, rules, or determinism, so a modified one is dropped and created again (G-148).
            ChangeNodeKind.Collation => !IsCommentOnlyChange<CollationDefinition>(modified, nameof(CollationDefinition.Comment)),

            // Extended statistics have no ALTER for their columns, expressions, or kinds, so a modified one is dropped and created again; nothing is lost, since ANALYZE collects them again (G-150).
            ChangeNodeKind.Statistics => !IsCommentOnlyChange<StatisticsDefinition>(modified, nameof(StatisticsDefinition.Comment)),

            // A dictionary's options are altered in place, but its template cannot be changed, and a configuration's mappings are altered in place but its parser cannot be
            // changed, so a change to either of those is a drop and a create (G-149).
            ChangeNodeKind.TextSearchDictionary => modified.Details.Any(static detail => detail.PropertyName == nameof(TextSearchDictionaryDefinition.Template)),
            ChangeNodeKind.TextSearchConfiguration => modified.Details.Any(static detail => detail.PropertyName == nameof(TextSearchConfigurationDefinition.Parser)),

            // A publication is altered in place (ALTER PUBLICATION SET), except that FOR ALL TABLES cannot be altered into or out of, so that change is a drop and a create (G-134).
            ChangeNodeKind.Publication => modified.Details.Any(static detail => detail.PropertyName == nameof(PublicationDefinition.IsAllTables)),
            // An extension is altered in place, never dropped and created again, which would drop every object it installed (G-46, G-213).
            ChangeNodeKind.Type => !(modified.NewNode is TypeDefinition extension && SqlOperation.IsExtension(extension))
                && !IsChangeOnlyTo<TypeDefinition>(modified, nameof(TypeDefinition.Comment), nameof(TypeDefinition.ColumnComments))
                && !IsAdditiveEnumChange(modified) && !IsCommentOnlyTypeChange(modified) && !IsAlterSafeDomainChange(modified)
                && !IsAlterSafeCompositeChange(modified),
            _ => false,
        };

    /// <summary>
    /// True when a modified domain's change can be deployed with in-place <c>ALTER DOMAIN</c> statements
    /// (<see cref="AlterOperation"/>, G-77) instead of dropping and recreating it (which fails while any column
    /// still uses the domain - the common case): its base type is byte-for-byte the same (there is no
    /// <c>ALTER DOMAIN ... TYPE</c>). An unnamed CHECK constraint used to force drop-then-recreate too, because adding or
    /// removing one renumbers the default names of the others; since G-98 those names are left out of the comparison, so
    /// <see cref="AlterOperation"/> changes unnamed CHECKs in place as well (G-109).
    /// </summary>
    private static bool IsAlterSafeDomainChange(ModifiedChange modified)
    {
        if (modified.OldNode is not TypeDefinition oldType || modified.NewNode is not TypeDefinition newType || !SqlOperation.IsDomain(newType))
        {
            return false;
        }

        return string.Equals(ColumnTypeNormalizer.NormalizeBaseTypeForColumnComparison(oldType.DomainBaseType), ColumnTypeNormalizer.NormalizeBaseTypeForColumnComparison(newType.DomainBaseType), StringComparison.OrdinalIgnoreCase)
            && oldType.DomainBaseTypeModifiers.SequenceEqual(newType.DomainBaseTypeModifiers, StringComparer.Ordinal)
            && oldType.DomainBaseTypeArrayDimensions == newType.DomainBaseTypeArrayDimensions
            && string.Equals(oldType.DomainIntervalFields, newType.DomainIntervalFields, StringComparison.OrdinalIgnoreCase)
            // There is no ALTER DOMAIN ... COLLATE (G-196).
            && CollationNames.Same(oldType.DomainCollationName, newType.DomainCollationName);
    }

    /// <summary>
    /// True when a modified composite type differs only in its fields (and perhaps its comment), each of which has an in-place
    /// <c>ALTER TYPE</c> form (<see cref="AlterOperation"/>, G-111): <c>DROP ATTRIBUTE</c>, <c>ALTER ATTRIBUTE ... TYPE</c>, and
    /// <c>ADD ATTRIBUTE</c>. Dropping and recreating the type instead fails while anything uses it (<c>2BP01</c>), the ordinary case.
    /// A change of kind (composite to enum, say) still drops and recreates.
    /// </summary>
    private static bool IsAlterSafeCompositeChange(ModifiedChange modified)
        => modified.OldNode is TypeDefinition oldType && modified.NewNode is TypeDefinition newType
            && SqlOperation.IsCompositeChange(oldType, newType)
            && modified.Details.Any(static detail => detail.PropertyName == nameof(TypeDefinition.CompositeFields))
            && modified.Details.All(static detail => detail.PropertyName is nameof(TypeDefinition.CompositeFields) or nameof(TypeDefinition.Comment) or nameof(TypeDefinition.ColumnComments));

    /// <summary>
    /// True when the diff reports a modified definition of type <typeparamref name="TDefinition"/> whose only difference is its
    /// comment. Used where the object has no in-place ALTER for anything else, so a comment-only change would otherwise be
    /// redeployed by dropping and recreating it: needlessly for an index, and destructively (losing computed data) for a
    /// materialized view (G-34; the same reasoning as G-53 for types).
    /// </summary>
    /// <summary>True when every difference of a modified definition is one of <paramref name="propertyNames"/>, each of which has an in-place form.</summary>
    private static bool IsChangeOnlyTo<TDefinition>(ModifiedChange modified, params string[] propertyNames)
        where TDefinition : class
        => modified.OldNode is TDefinition && modified.NewNode is TDefinition
            && modified.Details.Count > 0
            && modified.Details.All(detail => propertyNames.Contains(detail.PropertyName, StringComparer.Ordinal));

    private static bool IsCommentOnlyChange<TDefinition>(ModifiedChange modified, string commentPropertyName)
        where TDefinition : class
        => modified.OldNode is TDefinition && modified.NewNode is TDefinition
            && modified.Details.Count > 0
            && modified.Details.All(detail => string.Equals(detail.PropertyName, commentPropertyName, StringComparison.Ordinal));

    /// <summary>
    /// True when the diff reports a modified <see cref="TypeDefinition"/> whose only difference is its comment. Dropping and
    /// recreating the type for that fails while any column uses it and is needlessly destructive otherwise, whereas
    /// <c>COMMENT ON TYPE</c> changes exactly what changed (G-53). A change with no reported details is not assumed to be
    /// comment-only.
    /// </summary>
    private static bool IsCommentOnlyTypeChange(ModifiedChange modified)
        => modified.OldNode is TypeDefinition && modified.NewNode is TypeDefinition
            && modified.Details.Count > 0
            && modified.Details.All(static detail => string.Equals(detail.PropertyName, nameof(TypeDefinition.Comment), StringComparison.Ordinal));

    /// <summary>
    /// True when a modified <see cref="TypeDefinition"/> is an enum whose new label list is the old
    /// label list with one or more labels inserted anywhere (none removed, reordered, or renamed; G-183), and no
    /// other structural aspect of the type (composite fields, range options) changed. PostgreSQL
    /// supports adding enum values in-place via <c>ALTER TYPE ... ADD VALUE</c>; any other enum label
    /// change (removal, reorder) has no in-place equivalent and still requires drop-then-recreate.
    /// </summary>
    private static bool IsAdditiveEnumChange(ModifiedChange modified)
    {
        if (modified.OldNode is not TypeDefinition oldType || modified.NewNode is not TypeDefinition newType)
        {
            return false;
        }

        // Labels may be inserted anywhere (ADD VALUE ... BEFORE | AFTER), but none removed, renamed, or moved (G-183).
        // Both sides must be enums: an empty enum that becomes an empty composite has no labels to add either (G-268).
        if (EnumLabelAdditions.Plan(oldType.EnumLabels, newType.EnumLabels) is null || (oldType.EnumLabels.Count == 0 && !oldType.IsEnum) || (newType.EnumLabels.Count == 0 && !newType.IsEnum))
        {
            return false;
        }

        return oldType.CompositeFields.Count == 0 && newType.CompositeFields.Count == 0
            && oldType.RangeOptions is null && newType.RangeOptions is null;
    }

    /// <summary>
    /// Emits <c>CREATE SCHEMA IF NOT EXISTS</c> for every distinct, non-default schema referenced
    /// by an added object, ordered alphabetically before all other generated DDL. The built-in
    /// <c>public</c> schema always exists and is never scripted, matching the live loader's
    /// snapshot behavior.
    /// </summary>
    private static void AddSchemaCreateOperations(ICollection<SqlOperation> operations, ChangeSet changes)
    {
        // An extension moved to a schema needs it as much as an object created in it (G-213).
        var schemaNames = changes.Added
            .Select(added => added.Node.SchemaName)
            .Concat(changes.Modified
                .Where(static modified => modified.NewNode is TypeDefinition { ExtensionSchemaStated: true } extension && SqlOperation.IsExtension(extension))
                .Select(static modified => modified.NewNode.SchemaName))
            // A schema's owner is set on a schema that may hold nothing else, so it needs creating as much as one with an object in it (G-247).
            .Concat(changes.Added.Select(static added => added.Node)
                .OfType<OwnerDefinition>()
                .Where(static owner => string.Equals(owner.ObjectKind, "schema", StringComparison.OrdinalIgnoreCase))
                .Select(static owner => owner.ObjectName))
            // A publication FOR TABLES IN SCHEMA needs the schema it names, which may hold nothing else (G-231).
            .Concat(changes.Added.Select(static added => added.Node).Concat(changes.Modified.Select(static modified => modified.NewNode))
                .OfType<PublicationDefinition>()
                .SelectMany(static publication => publication.Schemas))
            .Where(schemaName => !string.IsNullOrWhiteSpace(schemaName)
                && !string.Equals(schemaName, "public", StringComparison.OrdinalIgnoreCase)
                && !IsSystemSchema(schemaName))
            .Distinct(PgProj.Parser.SqlIdentifiers.NameComparer)
            .OrderBy(schemaName => schemaName, StringComparer.OrdinalIgnoreCase);

        foreach (var schemaName in schemaNames)
        {
            operations.Add(new SchemaCreateOperation
            {
                NodeId = $"schema:{schemaName}",
                NodeKind = ChangeNodeKind.Schema,
                SchemaName = schemaName!,
            });
        }
    }

    /// <summary>A schema PostgreSQL has and reserves (G-213): <c>pg_catalog</c>, <c>information_schema</c>, and any <c>pg_</c> one, which cannot be created.</summary>
    private static bool IsSystemSchema(string? schemaName)
        => schemaName is not null
            && (schemaName.StartsWith("pg_", StringComparison.Ordinal) || string.Equals(schemaName, "information_schema", StringComparison.Ordinal));

    private static void AddUnorderedOperations<TChange>(
        ICollection<SqlOperation> operations,
        IEnumerable<TChange> changes,
        IReadOnlySet<string> orderedNodeIds,
        Func<TChange, SqlOperation> createOperation)
        where TChange : ChangeRecord
    {
        foreach (var change in changes)
        {
            if (orderedNodeIds.Contains(change.NodeId))
            {
                continue;
            }

            operations.Add(createOperation(change));
        }
    }
}
