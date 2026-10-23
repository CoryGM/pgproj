using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>
/// Dropping and recreating the objects in the way of a change PostgreSQL refuses while they exist (G-103). PostgreSQL won't change a
/// column's type, or drop the column, while a view uses it (<c>0A000</c>, <c>2BP01</c>). The views that use a column this publish changes,
/// and the views built on those, are dropped before the change (dependent first) and created again after it from the project, with their
/// grants and owner (G-116); a comment in the script says why. A dependent the project doesn't have (another project's view in the same
/// database) can't be recreated from the project, so the publish is refused and names it, rather than failing on the server.
/// </summary>
public sealed partial class ScriptGenerator
{
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QuoteQualified(string? schemaName, string name)
        => string.IsNullOrWhiteSpace(schemaName) ? Quote(name) : $"{Quote(schemaName)}.{Quote(name)}";

    private sealed record DependentRebuilds(IReadOnlyDictionary<string, string> Reasons, IReadOnlyDictionary<string, string> Kinds)
    {
        public static DependentRebuilds None { get; } = new(new Dictionary<string, string>(), new Dictionary<string, string>());
    }

    /// <summary>
    /// The live objects in the way of this publish, each with why: those that use a column a table alter drops or retypes (G-103), and
    /// those that use a routine it drops and recreates (G-105); then, transitively, whatever uses one of those (a view on such a view, a
    /// routine calling such a routine), since PostgreSQL won't drop an object another one uses.
    /// </summary>
    private static DependentRebuilds FindDependentRebuilds(IEnumerable<ModifiedChange> modifiedChanges, IReadOnlySet<string> droppedRoutines, DependencyGraph graph)
    {
        // A view whose output columns change in a way CREATE OR REPLACE VIEW can't make is itself dropped and recreated (G-104); one whose columns are
        // only renamed, with columns perhaps added after them, is renamed and replaced in place (G-276).
        var reshapedViews = modifiedChanges
            .Where(static change => change.OldNode is ViewDefinition { IsMaterialized: false } oldView && change.NewNode is ViewDefinition { IsMaterialized: false } newView
                && !IsAppendOnly(oldView.ServerOutputColumns, newView.ServerOutputColumns))
            .Select(static change => change.NodeId)
            .ToArray();
        if (graph.ReplacedDependencies.Count == 0 && reshapedViews.Length == 0)
        {
            return DependentRebuilds.None;
        }

        var changedColumns = new Dictionary<string, IReadOnlySet<string>>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var modified in modifiedChanges)
        {
            if (modified.OldNode is TableDefinition oldTable && modified.NewNode is TableDefinition newTable
                && ColumnsChangedUnderDependents(oldTable, newTable) is { Count: > 0 } columns)
            {
                changedColumns[modified.NodeId] = columns;
            }
        }

        if (changedColumns.Count == 0 && droppedRoutines.Count == 0 && reshapedViews.Length == 0)
        {
            return DependentRebuilds.None;
        }

        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Key, string Kind)>();
        void Add(LiveDependency dependency, string reason)
        {
            if (droppedRoutines.Contains(dependency.DependentKey) || !reasons.TryAdd(dependency.DependentKey, reason))
            {
                return;
            }

            kinds[dependency.DependentKey] = dependency.DependentKind;
            pending.Enqueue((dependency.DependentKey, dependency.DependentKind));
        }

        foreach (var view in reshapedViews)
        {
            reasons[view] = "its output columns change in a way CREATE OR REPLACE VIEW can't make (one is removed, reordered, renamed, or retyped)";
            kinds[view] = "view";
            pending.Enqueue((view, "view"));
        }

        foreach (var dependency in graph.ReplacedDependencies)
        {
            if (dependency.ReferencedKind is "routine" or "operator")
            {
                if (droppedRoutines.Contains(dependency.ReferencedKey))
                {
                    Add(dependency, dependency.ReferencedKind == "operator"
                        ? $"it uses the operator {dependency.ReferencedKey}, which this publish drops and creates again (an operator cannot be altered), and PostgreSQL won't drop it while the {dependency.DependentKind} exists"
                        : $"it uses {dependency.ReferencedKey}, which this publish drops and creates again (a change CREATE OR REPLACE can't make), and PostgreSQL won't drop it while the {dependency.DependentKind} exists");
                }

                continue;
            }

            if (changedColumns.TryGetValue(dependency.ReferencedKey, out var columns)
                && (dependency.ReferencedColumn is null || columns.Contains(dependency.ReferencedColumn)))
            {
                // A generated column the same alter drops or regenerates is out of the way already.
                if (dependency.DependentKind == "generated column"
                    && dependency.DependentKey.StartsWith(dependency.ReferencedKey + ".", StringComparison.OrdinalIgnoreCase)
                    && columns.Contains(dependency.DependentKey[(dependency.ReferencedKey.Length + 1)..]))
                {
                    continue;
                }

                var what = dependency.ReferencedColumn is null
                    ? $"table {dependency.ReferencedKey}"
                    : $"column {dependency.ReferencedKey}.{dependency.ReferencedColumn}";
                Add(dependency, $"it uses {what}, which this publish changes, and PostgreSQL won't change it while the {dependency.DependentKind} exists");
            }
        }

        while (pending.TryDequeue(out var rebuilt))
        {
            var referencedKind = rebuilt.Kind switch
            {
                "view" or "materialized view" => "relation",
                "function" or "procedure" => "routine",
                "operator" => "operator",
                _ => null,
            };
            if (referencedKind is null)
            {
                continue;
            }

            foreach (var dependency in graph.ReplacedDependencies)
            {
                if (dependency.ReferencedKind == referencedKind && string.Equals(dependency.ReferencedKey, rebuilt.Key, StringComparison.Ordinal))
                {
                    Add(dependency, $"it uses {rebuilt.Key}, which is dropped and recreated too");
                }
            }
        }

        return new DependentRebuilds(reasons, kinds);
    }

    /// <summary>
    /// True when <paramref name="newColumns"/> is <paramref name="oldColumns"/> with columns added at the end, the only change
    /// <c>CREATE OR REPLACE VIEW</c> can make to a view's output (G-104), or with columns renamed first (G-276). Also true when either list is unknown (the project view could
    /// not be rendered), which keeps the replace it had before.
    /// </summary>
    private static bool IsAppendOnly(IReadOnlyList<string> oldColumns, IReadOnlyList<string> newColumns)
        => oldColumns.Count == 0 || newColumns.Count == 0
            || ViewColumnRenames.Plan(oldColumns, newColumns) is not null;

    /// <summary>
    /// The live names of the columns of a table that the alter to <paramref name="newTable"/> drops, retypes, or drops and adds (a changed
    /// generated expression): the changes PostgreSQL refuses while a view uses the column. Matches the statements
    /// <see cref="AlterOperation"/> emits, including explicit renames.
    /// </summary>
    public static IReadOnlySet<string> ColumnsChangedUnderDependents(TableDefinition oldTable, TableDefinition newTable)
    {
        var oldColumns = oldTable.Columns.ToDictionary(static column => column.Name, PgProj.Parser.SqlIdentifiers.NameComparer);
        var newColumns = newTable.Columns.ToDictionary(static column => column.Name, PgProj.Parser.SqlIdentifiers.NameComparer);
        var counterparts = new Dictionary<string, ColumnDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var newColumn in newTable.Columns)
        {
            if (!string.IsNullOrWhiteSpace(newColumn.RenamedFrom)
                && oldColumns.TryGetValue(newColumn.RenamedFrom, out var renamed)
                && !newColumns.ContainsKey(renamed.Name))
            {
                counterparts[renamed.Name] = newColumn;
            }
            else if (oldColumns.ContainsKey(newColumn.Name))
            {
                counterparts.TryAdd(newColumn.Name, newColumn);
            }
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var oldColumn in oldTable.Columns)
        {
            if (!counterparts.TryGetValue(oldColumn.Name, out var newColumn))
            {
                changed.Add(oldColumn.Name);
                continue;
            }

            var (oldGenerated, newGenerated) = ColumnDefinition.GetComparableGeneratedExpressions(oldColumn, newColumn);
            var expressionChanged = oldColumn.IsGenerated && newColumn.IsGenerated
                && (!string.Equals(oldGenerated, newGenerated, StringComparison.Ordinal) || oldColumn.IsVirtual != newColumn.IsVirtual);
            if (expressionChanged
                || !string.Equals(oldColumn.GetComparableTypeSignature(), newColumn.GetComparableTypeSignature(), StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(oldColumn.Name);
            }
        }

        return changed;
    }

    /// <summary>The script\'s refusal for a dependent the project doesn\'t have, which publish reports and will not run (G-103).</summary>
    /// <summary>
    /// The modified objects that PostgreSQL cannot change in place and that other objects of the project use, with those objects: a collation (G-148), and a text search
    /// dictionary or configuration whose template or parser changed (G-149), an enum type that loses or reorders a label (G-190), and a domain whose collation changed (G-196). Each would have to be dropped and created again, which PostgreSQL refuses while anything uses it.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> FindChangedObjectsInUse(IEnumerable<ModifiedChange> modifiedChanges, DependencyGraph graph)
    {
        var inUse = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in modifiedChanges.Where(static change => (change.NewNode is CollationDefinition or TextSearchDictionaryDefinition or TextSearchConfigurationDefinition or TypeDefinition { EnumLabels.Count: > 0 } || IsDomainCollationChange(change))
                     && IsDropAndRecreateObject(change)))
        {
            // A routine, view, or other object that uses an enum is dropped and created again around it; what cannot be is a table (its columns) or another type.
            var users = graph.GetDependents(change.NodeId)
                .Where(node => change.NewNode is not TypeDefinition || node.SemanticNode is TableDefinition or ColumnDefinition or TypeDefinition)
                .Select(static node => node.NodeId)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (users.Length > 0)
            {
                inUse[change.NodeId] = users;
            }
        }

        return inUse;
    }

    /// <summary>A domain whose collation changed (G-196): PostgreSQL has no <c>ALTER DOMAIN ... COLLATE</c>, so it is dropped and created again.</summary>
    private static bool IsDomainCollationChange(ModifiedChange change)
        => change.OldNode is TypeDefinition { DomainBaseType: not null } oldType && change.NewNode is TypeDefinition { DomainBaseType: not null } newType
            && !CollationNames.Same(oldType.DomainCollationName, newType.DomainCollationName);

    private static SqlOperation BlockedChangedObject(ModifiedChange change, IReadOnlyList<string> users)
    {
        var userList = string.Join(", ", users.Take(5)) + (users.Count > 5 ? $" and {users.Count - 5} more" : string.Empty);
        if (IsDomainCollationChange(change))
        {
            return new BlockedOperation
            {
                NodeId = change.NodeId,
                NodeKind = change.NodeKind,
                Message = $"The domain {change.NodeId} changes its collation, but PostgreSQL cannot do that in place and refuses to drop a domain that {userList} use{(users.Count == 1 ? "s" : string.Empty)}. "
                    + "pgproj does not rebuild what uses it: create the domain under a new name with the new collation, change those columns to it (ALTER COLUMN ... TYPE new_domain), drop the old domain, and rename the new one.",
            };
        }

        if (change.NewNode is TypeDefinition)
        {
            // PostgreSQL has no ALTER TYPE ... DROP VALUE and no way to reorder labels; only inserting a label (ADD VALUE) is done in place (G-183).
            return new BlockedOperation
            {
                NodeId = change.NodeId,
                NodeKind = change.NodeKind,
                Message = $"The enum type {change.NodeId} loses or reorders a label, but PostgreSQL cannot do that in place and refuses to drop a type that {userList} use{(users.Count == 1 ? "s" : string.Empty)}. "
                    + "pgproj does not rebuild what uses it: create the type under a new name, change those columns to it (ALTER COLUMN ... TYPE ... USING column::text::new_type), drop the old type, and rename the new one. Adding a label is done in place.",
            };
        }

        var (what, kind, way) = change.NewNode switch
        {
            TextSearchDictionaryDefinition => ("text search dictionary", "template", "create the dictionary under a new name, point the configurations that use it at the new one, and drop the old one"),
            TextSearchConfigurationDefinition => ("text search configuration", "parser", "create the configuration under a new name, move what uses it, and drop the old one"),
            _ => ("collation", "definition", "create the collation under a new name, move the columns and indexes to it, and drop the old one"),
        };
        var list = string.Join(", ", users.Take(5).Select(DatabaseModel.DisplayKey)) + (users.Count > 5 ? $" and {users.Count - 5} more" : string.Empty);
        return new BlockedOperation
        {
            NodeId = change.NodeId,
            NodeKind = change.NodeKind,
            Message = $"The {what} {DatabaseModel.DisplayKey(change.NodeId)} changed its {kind}, but PostgreSQL cannot alter that and refuses to drop one that {list} use{(users.Count == 1 ? "s" : string.Empty)}. pgproj does not rebuild what uses it: {way}.",
        };
    }

    private static SqlOperation BlockedDependent(string key, string kind, string reason)
        => new BlockedOperation
        {
            NodeId = key,
            NodeKind = kind switch
            {
                "policy" => ChangeNodeKind.Policy,
                "trigger" => ChangeNodeKind.Trigger,
                "function" => ChangeNodeKind.Function,
                "procedure" => ChangeNodeKind.Procedure,
                "generated column" => ChangeNodeKind.Table,
                "operator" => ChangeNodeKind.Operator,
                "rule" => ChangeNodeKind.Rule,
                _ => ChangeNodeKind.View,
            },
            Message = kind == "other"
                ? $"{key} is in the way because {reason}. It is not something pgproj models, so it can't be recreated: drop it first, "
                    + "or change the routine only in ways CREATE OR REPLACE can make (the body, not its arguments or return type)."
                : kind == "generated column"
                // PostgreSQL 16 has no ALTER COLUMN ... SET EXPRESSION, so the column would have to be dropped and added, rewriting the
                // table; that is left to the user (the approved plan, decision 2).
                ? $"The generated column {key} is in the way because {reason}. PostgreSQL cannot change it in place, so pgproj does not "
                    + "drop and re-add it. Remove the generated column in one publish, make this change, and add it back in the next."
                : $"The {kind} {key} must be dropped and recreated because {reason}, but it is not in this project, so pgproj cannot recreate it. "
                    + "Add it to the project (or to its \"schemas\"), or drop it first.",
        };

    /// <summary>
    /// A column default or a domain CHECK that uses a routine being dropped (G-105) is taken off before anything is dropped and put back
    /// from the project once everything is created: <c>ALTER TABLE ... ALTER COLUMN ... DROP DEFAULT</c>/<c>SET DEFAULT</c>, and
    /// <c>ALTER DOMAIN ... DROP CONSTRAINT</c>/<c>ADD CONSTRAINT ... NOT VALID</c> then <c>VALIDATE CONSTRAINT</c>. Neither is an object that
    /// can be dropped on its own. False when the table or domain isn't the project's, which the caller refuses.
    /// </summary>
    private static bool TryDetachAndReattach(string key, string kind, string reason, DependencyGraph graph, List<SqlOperation> detaches, List<SqlOperation> reattaches)
    {
        var separator = key.LastIndexOf('.');
        if (separator <= 0 || !graph.IsTargetNode(key[..separator]) || !graph.Nodes.TryGetValue(key[..separator], out var owner))
        {
            return false;
        }

        var name = key[(separator + 1)..];
        if (kind == "default" && owner.SemanticNode is TableDefinition table)
        {
            var qualified = QuoteQualified(table.SchemaName, table.Name);
            detaches.Add(Statement(key, ChangeNodeKind.Table, $"ALTER TABLE {qualified} ALTER COLUMN {Quote(name)} DROP DEFAULT", $"the default of {key} is taken off here and put back below: {reason}."));
            if (table.Columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.Ordinal)) is { IsGenerated: false, RawDefaultExpression: { Length: > 0 } expression })
            {
                reattaches.Add(Statement(key, ChangeNodeKind.Table, $"ALTER TABLE {qualified} ALTER COLUMN {Quote(name)} SET DEFAULT {expression}", $"the default of {key} is put back."));
            }

            return true;
        }

        if (kind == "domain check" && owner.SemanticNode is TypeDefinition domain)
        {
            var qualified = QuoteQualified(domain.SchemaName, domain.Name);
            detaches.Add(Statement(key, ChangeNodeKind.Type, $"ALTER DOMAIN {qualified} DROP CONSTRAINT {Quote(name)}", $"the CHECK {key} is taken off here and put back below: {reason}."));
            if (domain.DomainCheckConstraints.FirstOrDefault(check => string.Equals(check.Name, name, StringComparison.Ordinal)) is { } check)
            {
                reattaches.Add(Statement(key, ChangeNodeKind.Type, $"ALTER DOMAIN {qualified} ADD CONSTRAINT {Quote(name)} CHECK ({check.RawExpression}) NOT VALID", $"the CHECK {key} is put back, then validated."));
                reattaches.Add(Statement(key, ChangeNodeKind.Type, $"ALTER DOMAIN {qualified} VALIDATE CONSTRAINT {Quote(name)}", null));
            }

            return true;
        }

        return false;
    }

    /// <summary>A table CHECK put back around a routine's drop: added <c>NOT VALID</c>, then validated (G-105, decision 3).</summary>
    private static SqlOperation[] ReaddCheck(CreateOperation create, ConstraintDefinition check)
    {
        // A check the project states NOT VALID is put back as it is and not validated (G-234).
        if (check.IsNotValid)
        {
            return [create];
        }

        return
        [
            Statement(create.NodeId, create.NodeKind, create.GenerateSql().TrimEnd().TrimEnd(';') + " NOT VALID", create.Reason),
            Statement(create.NodeId, create.NodeKind, $"ALTER TABLE {QuoteQualified(check.SchemaName, check.TableName ?? string.Empty)} VALIDATE CONSTRAINT {Quote(check.Name)}", null),
        ];
    }

    /// <summary>
    /// Adds to the graph, for each object being rebuilt that PostgreSQL records as using a routine being dropped, the edge the project may
    /// not have (a policy's expression, a CHECK), so its drop comes before the routine's and its create after (G-105). An edge that would
    /// close a cycle is left out. True when any was added, so the order must be computed again.
    /// </summary>
    private static bool AddLiveRoutineEdges(DependencyGraph graph, IReadOnlySet<string> rebuilt, IReadOnlySet<string> droppedRoutines)
    {
        var added = false;
        foreach (var dependency in graph.ReplacedDependencies)
        {
            if (dependency.ReferencedKind is "routine" or "operator"
                && droppedRoutines.Contains(dependency.ReferencedKey)
                && rebuilt.Contains(dependency.DependentKey)
                && graph.Nodes.ContainsKey(dependency.DependentKey)
                && graph.Nodes.ContainsKey(dependency.ReferencedKey)
                && !Reaches(graph, dependency.ReferencedKey, dependency.DependentKey))
            {
                graph.AddEdge(dependency.DependentKey, dependency.ReferencedKey);
                added = true;
            }
        }

        return added;
    }

    private static bool Reaches(DependencyGraph graph, string from, string to)
    {
        var seen = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var pending = new Stack<string>([from]);
        while (pending.TryPop(out var current))
        {
            if (string.Equals(current, to, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (seen.Add(current))
            {
                foreach (var next in graph.GetDependencies(current))
                {
                    pending.Push(next.NodeId);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>COMMENT ON ...</c> that follows the creation of a constraint, a trigger, or a policy (G-161, G-163, G-164). These are created by statements that carry no comment (<c>ADD CONSTRAINT</c>, <c>CREATE TRIGGER</c>, <c>CREATE POLICY</c>), so a
    /// stated comment is set after them; and one that is dropped and created again would otherwise lose the comment the database had, so when the project states none the live one is put back
    /// (<paramref name="replaced"/>, from <see cref="DependencyGraph.ReplacedComments"/>).
    /// </summary>
    private static SqlOperation[] CommentAfterCreate(ISemanticNode node, string nodeId, ChangeNodeKind kind, DependencyGraph graph, bool replaced)
    {
        var target = node switch
        {
            ConstraintDefinition constraint => (Stated: constraint.CommentStated, Comment: constraint.Comment, constraint.SchemaName, constraint.TableName, constraint.Name, Generate: (Func<string?, string, string, string?, string>)CommentSqlHelper.GenerateConstraintComment),
            TriggerDefinition trigger => (Stated: trigger.CommentStated, Comment: trigger.Comment, trigger.SchemaName, trigger.TableName, trigger.Name, Generate: CommentSqlHelper.GenerateTriggerComment),
            PolicyDefinition policy => (Stated: policy.CommentStated, Comment: policy.Comment, policy.SchemaName, policy.TableName, policy.Name, Generate: CommentSqlHelper.GeneratePolicyComment),
            _ => default,
        };
        if (target.Generate is null)
        {
            return [];
        }

        var comment = target.Stated
            ? target.Comment
            : replaced && graph.ReplacedComments.TryGetValue(DependencyGraph.CommentKey(node), out var live) ? live : null;
        var statements = new List<SqlOperation>();
        if (comment is not null)
        {
            statements.Add(Statement(nodeId, kind, target.Generate(target.SchemaName, target.TableName ?? string.Empty, target.Name, comment), null));
        }

        // A key's or exclusion constraint's index has a comment of its own, stated with COMMENT ON INDEX (G-295).
        if (node is ConstraintDefinition { IndexCommentStated: true, IndexComment: { } indexComment } keyConstraint)
        {
            statements.Add(Statement(nodeId, kind, CommentSqlHelper.GenerateConstraintIndexComment(keyConstraint.SchemaName, keyConstraint.Name, indexComment), null));
        }

        return [.. statements];
    }

    private static PreservedStatementOperation Statement(string nodeId, ChangeNodeKind kind, string sql, string? reason)
        => new() { NodeId = nodeId, NodeKind = kind, Statement = sql, Reason = reason };
}
