using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy;

public sealed partial class ScriptGenerator
{
    /// <summary>
    /// A type rebuilt under the columns that use it: an enum that loses or reorders a label (G-191), or a composite type that retypes a field (G-119). The
    /// change, the columns moved, by table, and for an enum the labels lost.
    /// </summary>
    private sealed record TypeSwap(ModifiedChange Change, IReadOnlyList<EnumSwapColumn> Columns, IReadOnlyDictionary<string, IReadOnlySet<string>> ColumnsByTable, IReadOnlyList<string> RemovedLabels)
    {
        public TypeDefinition NewType => (TypeDefinition)Change.NewNode;

        public TypeDefinition OldType => (TypeDefinition)Change.OldNode;

        public bool IsEnum => NewType.IsEnum;
    }

    /// <summary>
    /// The enum types that lose or reorder a label while columns use them and can be rebuilt under them (G-191, the user's decisions of 2026-11-09): every
    /// user is a column of a table the database has and the project keeps, the column is not a partition key, and the table neither inherits nor is a
    /// partition (an inherited column moves with its parent). A domain over the type, a composite field of it, or a routine that uses it is not rebuilt
    /// (decision 4); such a type stays refused, with the reason in <paramref name="refusals"/>.
    /// </summary>
    private static Dictionary<string, TypeSwap> FindEnumSwaps(
        IReadOnlyDictionary<string, IReadOnlyList<string>> inUse,
        IEnumerable<ModifiedChange> modifiedChanges,
        DependencyGraph graph,
        Dictionary<string, string> refusals)
    {
        var swaps = new Dictionary<string, TypeSwap>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in modifiedChanges)
        {
            if (!inUse.ContainsKey(change.NodeId) || change.OldNode is not TypeDefinition { IsEnum: true } oldType || change.NewNode is not TypeDefinition { IsEnum: true } newType)
            {
                continue;
            }

            if (OtherTypeUsers(change.NodeId, graph) is { } others)
            {
                refusals[change.NodeId] = $"{others}, and pgproj rebuilds an enum type only under table columns, not under a domain, a composite type, or a routine";
                continue;
            }

            if (!TryCollectSwapColumns(change.NodeId, graph, out var columns, out var byTable, out var problem))
            {
                refusals[change.NodeId] = problem;
                continue;
            }

            swaps[change.NodeId] = new TypeSwap(change, columns, byTable, EnumLabelAdditions.Removed(oldType.EnumLabels, newType.EnumLabels, newType.EnumLabelRenames));
        }

        return swaps;
    }

    /// <summary>
    /// The domains, composite types, and routines of the project that use the type, as a clause (<c>app.d uses it</c>), or null when there are none: a
    /// swap does not rebuild them, so a type they use stays refused.
    /// </summary>
    private static string? OtherTypeUsers(string nodeId, DependencyGraph graph)
    {
        var others = graph.GetDependents(nodeId)
            .Where(static node => node.SemanticNode is TypeDefinition or FunctionDefinition or ProcedureDefinition or AggregateDefinition)
            .Select(static node => node.NodeId)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return others.Length == 0
            ? null
            : $"{string.Join(", ", others.Take(5))}{(others.Length > 5 ? $" and {others.Length - 5} more" : string.Empty)} use{(others.Length == 1 ? "s" : string.Empty)} it";
    }

    /// <summary>
    /// The columns of the database's tables that use the type and that a swap moves (G-191, G-119): those of a table the project keeps (one it drops is
    /// dropped before the type is renamed). False, with the reason, when a partition or an inheritance child uses it, or a partition key does.
    /// </summary>
    private static bool TryCollectSwapColumns(
        string nodeId,
        DependencyGraph graph,
        out List<EnumSwapColumn> columns,
        out Dictionary<string, IReadOnlySet<string>> byTable,
        out string problem)
    {
        columns = [];
        byTable = new Dictionary<string, IReadOnlySet<string>>(PgProj.Parser.SqlIdentifiers.NameComparer);
        problem = string.Empty;
        foreach (var table in graph.ReplacedTables.Values.OrderBy(static table => table.Key, StringComparer.Ordinal))
        {
            var using_ = table.Columns.Where(column => column.ReferencedTypes.Contains(nodeId, PgProj.Parser.SqlIdentifiers.NameComparer)).ToArray();
            if (using_.Length == 0 || !graph.IsTargetNode(table.Key))
            {
                continue; // a table the project drops is dropped before the type is renamed
            }

            if (table.PartitionOfTableName is not null || table.InheritsFrom.Count > 0)
            {
                problem = $"{table.Key} uses it and is a partition or an inheritance child, whose columns pgproj does not move on their own";
                return false;
            }

            if (using_.FirstOrDefault(column => table.PartitionKeyElements.Any(key => PgProj.Parser.SqlIdentifiers.NameComparer.Equals(key.Name, column.Name))) is { } key)
            {
                problem = $"{table.Key}.{key.Name} uses it and is a partition key, which cannot change type";
                return false;
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

        return true;
    }

    private static bool IsArray(ColumnDefinition column)
        => column.ArrayDimensions > 0 || (column.RawType ?? column.DataType ?? string.Empty).TrimEnd().EndsWith(']');

    /// <summary>
    /// The project's checks and indexes on the swapped columns that PostgreSQL cannot carry through the move (G-191, G-119): a <c>CHECK</c> that names the
    /// column, and an index with a predicate or an expression on it, which hold the old type's labels or fields. A plain index or key on the column is
    /// rebuilt by PostgreSQL itself. Each is dropped before the swap and created again after it.
    /// </summary>
    private static IEnumerable<ISemanticNode> SwapDependents(TypeSwap swap, DependencyGraph graph)
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
                // An expression key counts as a predicate does (G-345): NormalizedExpressions holds only the predicate's, so an index on ((c = 'r')) was
                // left for PostgreSQL to rebuild, which it cannot, since its expression compares the column with the old type.
                case IndexDefinition index
                    when (index.RawPredicateExpression is not null || index.NormalizedExpressions.Count > 0 || index.KeyElements.Any(static key => key.RawExpression is not null))
                        && Uses(swap, index.SchemaName, index.TableName, index.ReferencedColumns):
                    yield return index;
                    break;
            }
        }
    }

    private static bool Uses(TypeSwap swap, string? schema, string? table, IReadOnlyList<string> referencedColumns)
    {
        var key = schema is null ? table : $"{schema}.{table}";
        return key is not null
            && swap.ColumnsByTable.TryGetValue(key, out var columns)
            && referencedColumns.Any(reference => columns.Contains(reference) || columns.Contains(reference[(reference.LastIndexOf('.') + 1)..]));
    }
}
