namespace PgProj.Semantics;

public sealed class SemanticNode
{
    public required string Key { get; init; }

    public required string Kind { get; init; }

    public required ISemanticNode Source { get; init; }
}

public sealed class SemanticGraph
{
    public static SemanticGraph Empty => new();

    public Dictionary<string, SemanticNode> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> Edges { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> TopologicalOrder { get; } = [];
}

internal sealed class SemanticGraphPass
{
    public void Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Diagnostics is not IList<SemanticDiagnostic> diagnostics)
        {
            return;
        }

        BuildNodes(model);
        BuildEdges(model);
        DetectCycles(model.SemanticGraph, diagnostics);
        TopologicalSort(model.SemanticGraph);
    }

    private static void BuildNodes(DatabaseModel model)
    {
        var graph = model.SemanticGraph;

        foreach (var node in model.GetAllNodes())
        {
            graph.Nodes.TryAdd(node.Key, new SemanticNode
            {
                Key = node.Key,
                Kind = node.Kind,
                Source = node,
            });
        }
    }

    private static void BuildEdges(DatabaseModel model)
    {
        var graph = model.SemanticGraph;

        foreach (var pair in graph.Nodes)
        {
            var dependencies = GetDependencies(pair.Value.Source, model)
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .Where(graph.Nodes.ContainsKey)
                .Where(reference => !string.Equals(reference, pair.Key, StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            graph.Edges[pair.Key] = dependencies;
        }

        // TODO: Add weighted edges to distinguish strong dependencies (FK, type inheritance) from weak (expression references).
        // TODO: Add dependency categories (strong vs weak) for more granular script generation control.
        // TODO: Add script-phase grouping (pre, main, post) for ordering CREATE, ALTER, and constraint statements.
    }

    private static void DetectCycles(SemanticGraph graph, ICollection<SemanticDiagnostic> diagnostics)
    {
        var visitState = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var reportedCycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in graph.Nodes.Keys)
        {
            VisitNode(key, graph.Edges, visitState, stack, reportedCycles, diagnostics, key => graph.Nodes.TryGetValue(key, out var node) ? node.Source.Location : null);
        }
    }

    private static void TopologicalSort(SemanticGraph graph)
    {
        // Kahn's algorithm: Edges[key] = dependencies of key; dependencies must precede key.
        var inDegree = new Dictionary<string, int>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var reverseEdges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in graph.Nodes.Keys)
        {
            graph.Edges.TryGetValue(key, out var deps);
            inDegree[key] = deps?.Count ?? 0;

            if (deps is null)
            {
                continue;
            }

            foreach (var dep in deps)
            {
                if (!reverseEdges.TryGetValue(dep, out var dependents))
                {
                    dependents = [];
                    reverseEdges[dep] = dependents;
                }

                dependents.Add(key);
            }
        }

        var queue = new Queue<string>(graph.Nodes.Keys.Where(key => inDegree.GetValueOrDefault(key) == 0));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            graph.TopologicalOrder.Add(current);

            if (!reverseEdges.TryGetValue(current, out var dependents))
            {
                continue;
            }

            foreach (var dependent in dependents)
            {
                if (--inDegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        // TODO: Add cycle-aware topological sort handling for strongly connected components.
        // TODO: Group nodes by script phase (pre: types/tables, main: views/functions/indexes, post: constraints/grants).
    }

    private static IEnumerable<string> GetDependencies(ISemanticNode node, DatabaseModel model)
    {
        foreach (var table in node.ResolvedTables)
        {
            yield return table.Key;
        }

        foreach (var function in node.ResolvedFunctions)
        {
            yield return function.Key;
        }

        foreach (var type in node.ResolvedTypes)
        {
            yield return type.Key;
        }

        foreach (var table in node.ReferencedTables)
        {
            yield return table;
        }

        foreach (var function in node.ReferencedFunctions)
        {
            yield return function;
        }

        foreach (var type in node.ReferencedTypes)
        {
            yield return type;
        }

        if (node is TableDefinition tableNode)
        {
            foreach (var sequence in model.Sequences.Values)
            {
                if (!string.IsNullOrWhiteSpace(sequence.OwnedByTable)
                    && string.Equals(sequence.OwnedByTable.Trim(), tableNode.Key, StringComparison.OrdinalIgnoreCase))
                {
                    yield return sequence.Key;
                }
            }
        }

        // TODO: Add expression-level dependencies for CASE, WINDOW, CTE, and subquery constructs once expression mapping is richer.
    }

    private static void VisitNode(
        string key,
        IReadOnlyDictionary<string, List<string>> edges,
        IDictionary<string, VisitState> visitState,
        Stack<string> stack,
        ISet<string> reportedCycles,
        ICollection<SemanticDiagnostic> diagnostics,
        Func<string, SemanticLocation?> place)
    {
        if (visitState.TryGetValue(key, out var state))
        {
            if (state == VisitState.Visited)
            {
                return;
            }

            if (state == VisitState.Visiting)
            {
                ReportCycle(key, stack, reportedCycles, diagnostics, place);
            }

            return;
        }

        visitState[key] = VisitState.Visiting;
        stack.Push(key);

        if (edges.TryGetValue(key, out var dependencies))
        {
            foreach (var dependency in dependencies)
            {
                VisitNode(dependency, edges, visitState, stack, reportedCycles, diagnostics, place);
            }
        }

        stack.Pop();
        visitState[key] = VisitState.Visited;
    }

    private static void ReportCycle(
        string key,
        Stack<string> stack,
        ISet<string> reportedCycles,
        ICollection<SemanticDiagnostic> diagnostics,
        Func<string, SemanticLocation?> place)
    {
        var path = stack.Reverse().ToArray();
        var cycleStart = Array.FindIndex(path, candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));

        if (cycleStart < 0)
        {
            return;
        }

        var cycle = path[cycleStart..].Concat([key]).ToArray();
        var signature = string.Join("->", cycle.Select(static value => value.ToUpperInvariant()));

        if (!reportedCycles.Add(signature))
        {
            return;
        }

        diagnostics.Add(SemanticDiagnostics.Error(
            $"Dependency cycle detected in semantic graph: {string.Join(" -> ", cycle)}.",
            // The first object of the cycle is where it is shown (G-309e).
            place(cycle[0]),
            nameof(SemanticGraphPass)));
    }

    private enum VisitState
    {
        Visiting,
        Visited,
    }
}
