using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy;

public sealed partial class ScriptGenerator
{
    /// <summary>
    /// The composite types that retype a field (its type or its collation) while columns of the database's tables use them (G-119, the user's decisions of
    /// 2026-11-09): PostgreSQL refuses <c>ALTER ATTRIBUTE ... TYPE</c> then, so the type is rebuilt under them, as an enum is (G-191). Adding and dropping a
    /// field stay in place, which PostgreSQL allows while columns use the type, and so does a retype no column is in the way of. A type that a domain, another
    /// composite type, or a routine uses, that a typed table is of, or whose columns a swap cannot move, is refused, each with a
    /// <see cref="BlockedOperation"/> in <paramref name="blocked"/> naming why.
    /// </summary>
    private static Dictionary<string, TypeSwap> FindCompositeSwaps(IEnumerable<ModifiedChange> modifiedChanges, DependencyGraph graph, List<SqlOperation> blocked)
    {
        var swaps = new Dictionary<string, TypeSwap>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in modifiedChanges)
        {
            if (!IsAlterSafeCompositeChange(change)
                || change.OldNode is not TypeDefinition oldType
                || change.NewNode is not TypeDefinition newType
                || !RetypesAField(oldType, newType))
            {
                continue;
            }

            if (!TryCollectSwapColumns(change.NodeId, graph, out var columns, out var byTable, out var problem))
            {
                blocked.Add(BlockedCompositeRetype(change, problem));
                continue;
            }

            if (columns.Count == 0)
            {
                continue; // nothing is in the way: ALTER ATTRIBUTE ... TYPE in place
            }

            if (OtherTypeUsers(change.NodeId, graph) is { } others)
            {
                blocked.Add(BlockedCompositeRetype(change, $"{others}, and pgproj rebuilds a composite type only under table columns, not under a domain, another composite type, or a routine"));
                continue;
            }

            if (oldType.HasTypedTables || newType.HasTypedTables)
            {
                blocked.Add(BlockedCompositeRetype(change, "a typed table is of it, which cannot be moved to another type"));
                continue;
            }

            swaps[change.NodeId] = new TypeSwap(change, columns, byTable, []);
        }

        return swaps;
    }

    /// <summary>True when a field both types have changes its type or its collation: the change PostgreSQL refuses while a column uses the type.</summary>
    private static bool RetypesAField(TypeDefinition oldType, TypeDefinition newType)
    {
        var oldFields = oldType.CompositeFields.ToDictionary(static field => field.Name, StringComparer.Ordinal);
        return newType.CompositeFields.Any(field => oldFields.TryGetValue(field.Name, out var oldField)
            && (!string.Equals(ComparableField(oldField), ComparableField(field), StringComparison.Ordinal) || !CollationNames.Same(oldField.CollationName, field.CollationName)));
    }

    private static string ComparableField(CompositeTypeFieldDefinition field) => field.ComparableType ?? field.DataType ?? field.RawType ?? string.Empty;

    private static BlockedOperation BlockedCompositeRetype(ModifiedChange change, string reason)
        => new()
        {
            NodeId = change.NodeId,
            NodeKind = change.NodeKind,
            Message = $"The composite type {change.NodeId} retypes a field while columns use it, which PostgreSQL cannot do in place, so pgproj rebuilds the type under those columns; "
                + $"it cannot here: {reason}. Change that use first, or make the change by hand: rename the type, create the new one, change the columns to it "
                + "(ALTER COLUMN ... TYPE ... USING ROW((column).field, ...)::text::new_type), and drop the old type. Adding and dropping a field are done in place.",
        };

    /// <summary>The operation that moves a swapped composite type's columns (G-119), after the new type is created.</summary>
    private static CompositeSwapOperation CompositeSwap(TypeSwap swap, ModifiedChange modified)
        => new()
        {
            NodeId = modified.NodeId,
            NodeKind = modified.NodeKind,
            SchemaName = swap.NewType.SchemaName,
            TypeName = swap.NewType.Name,
            Columns = swap.Columns,
            OldFieldNames = [.. swap.OldType.CompositeFields.Select(static field => field.Name)],
            NewFieldNames = [.. swap.NewType.CompositeFields.Select(static field => field.Name)],
            DestructiveReasons = CompositeSwapOperation.GetDestructiveReasons(swap.OldType, swap.NewType),
            Reason = $"The columns of {modified.NodeId} move to the new type, each field by name and through its text; a value that does not fit stops the publish.",
        };
}
