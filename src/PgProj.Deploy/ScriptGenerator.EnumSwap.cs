using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy;

public sealed partial class ScriptGenerator
{
    /// <summary>An enum type rebuilt under the columns that use it (G-191): the change, the columns moved, by table, and the labels lost.</summary>
    private sealed record EnumSwap(ModifiedChange Change, IReadOnlyList<EnumSwapColumn> Columns, IReadOnlyDictionary<string, IReadOnlySet<string>> ColumnsByTable, IReadOnlyList<string> RemovedLabels)
    {
        public TypeDefinition NewType => (TypeDefinition)Change.NewNode;
    }

    /// <summary>
    /// The enum types that lose or reorder a label while columns use them and can be rebuilt under them (G-191, the user's decisions of 2026-11-09): every
    /// user is a column of a table the database has and the project keeps, the column is not a partition key, and the table neither inherits nor is a
    /// partition (an inherited column moves with its parent). A domain over the type, a composite field of it, or a routine that uses it is not rebuilt
    /// (decision 4); such a type stays refused, with the reason in <paramref name="refusals"/>.
    /// </summary>
    private static Dictionary<string, EnumSwap> FindEnumSwaps(
        IReadOnlyDictionary<string, IReadOnlyList<string>> inUse,
        IEnumerable<ModifiedChange> modifiedChanges,
        DependencyGraph graph,
        Dictionary<string, string> refusals)
    {
        var swaps = new Dictionary<string, EnumSwap>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in modifiedChanges)
        {
            if (!inUse.ContainsKey(change.NodeId) || change.OldNode is not TypeDefinition { IsEnum: true } oldType || change.NewNode is not TypeDefinition { IsEnum: true } newType)
            {
                continue;
            }

            var others = graph.GetDependents(change.NodeId)
                .Where(static node => node.SemanticNode is TypeDefinition or FunctionDefinition or ProcedureDefinition or AggregateDefinition)
                .Select(static node => node.NodeId)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (others.Length > 0)
            {
                refusals[change.NodeId] = $"{string.Join(", ", others.Take(5))}{(others.Length > 5 ? $" and {others.Length - 5} more" : string.Empty)} use{(others.Length == 1 ? "s" : string.Empty)} it, and pgproj rebuilds an enum type only under table columns, not under a domain, a composite type, or a routine";
                continue;
            }

            var columns = new List<EnumSwapColumn>();
            var byTable = new Dictionary<string, IReadOnlySet<string>>(PgProj.Parser.SqlIdentifiers.NameComparer);
            string? problem = null;
            foreach (var table in graph.ReplacedTables.Values.OrderBy(static table => table.Key, StringComparer.Ordinal))
            {
                var using_ = table.Columns.Where(column => column.ReferencedTypes.Contains(change.NodeId, PgProj.Parser.SqlIdentifiers.NameComparer)).ToArray();
                if (using_.Length == 0 || !graph.IsTargetNode(table.Key))
                {
                    continue; // a table the project drops is dropped before the type is renamed
                }

                if (table.PartitionOfTableName is not null || table.InheritsFrom.Count > 0)
                {
                    problem = $"{table.Key} uses it and is a partition or an inheritance child, whose columns pgproj does not move on their own";
                    break;
                }

                if (using_.FirstOrDefault(column => table.PartitionKeyElements.Any(key => PgProj.Parser.SqlIdentifiers.NameComparer.Equals(key.Name, column.Name))) is { } key)
                {
                    problem = $"{table.Key}.{key.Name} uses it and is a partition key, which cannot change type";
                    break;
                }

                var projectTable = graph.Nodes.TryGetValue(table.Key, out var node) ? node.SemanticNode as TableDefinition : null;
                foreach (var column in using_)
                {
                    var projectColumn = projectTable?.Columns.FirstOrDefault(candidate => PgProj.Parser.SqlIdentifiers.NameComparer.Equals(candidate.Name, column.Name));
                    columns.Add(new EnumSwapColumn(
                        table.SchemaName,
                        table.Name,
                        column.Name,
                        IsArray(column),
                        projectColumn?.RawDefaultExpression,
                        !string.IsNullOrWhiteSpace(column.RawDefaultExpression)));
                }

                byTable[table.Key] = using_.Select(static column => column.Name).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
            }

            if (problem is not null)
            {
                refusals[change.NodeId] = problem;
                continue;
            }

            swaps[change.NodeId] = new EnumSwap(change, columns, byTable, EnumLabelAdditions.Removed(oldType.EnumLabels, newType.EnumLabels, newType.EnumLabelRenames));
        }

        return swaps;
    }

    private static bool IsArray(ColumnDefinition column)
        => column.ArrayDimensions > 0 || (column.RawType ?? column.DataType ?? string.Empty).TrimEnd().EndsWith(']');

    /// <summary>
    /// The project's checks and indexes on the swapped columns that PostgreSQL cannot carry through the move (G-191): a <c>CHECK</c> that names the column,
    /// and an index with a predicate or an expression on it, which compare it with the old type's labels. A plain index or key on the column is rebuilt by
    /// PostgreSQL itself. Each is dropped before the swap and created again after it.
    /// </summary>
    private static IEnumerable<ISemanticNode> SwapDependents(EnumSwap swap, DependencyGraph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            if (!graph.IsTargetNode(node.NodeId))
            {
                continue;
            }

            switch (node.SemanticNode)
            {
                case ConstraintDefinition { ConstraintType: { } type } check
                    when string.Equals(type.Trim(), "CHECK", StringComparison.OrdinalIgnoreCase) && Uses(swap, check.SchemaName, check.TableName, check.ReferencedColumns):
                    yield return check;
                    break;
                case IndexDefinition index
                    when (index.RawPredicateExpression is not null || index.NormalizedExpressions.Count > 0)
                        && Uses(swap, index.SchemaName, index.TableName, index.ReferencedColumns):
                    yield return index;
                    break;
            }
        }
    }

    private static bool Uses(EnumSwap swap, string? schema, string? table, IReadOnlyList<string> referencedColumns)
    {
        var key = schema is null ? table : $"{schema}.{table}";
        return key is not null
            && swap.ColumnsByTable.TryGetValue(key, out var columns)
            && referencedColumns.Any(reference => columns.Contains(reference) || columns.Contains(reference[(reference.LastIndexOf('.') + 1)..]));
    }
}
