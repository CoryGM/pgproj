using System.Text;

namespace PgProj.Semantics;

internal sealed class SemanticCycleExplanationPass
{
    public void Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Diagnostics is not IList<SemanticDiagnostic> diagnostics)
        {
            return;
        }

        var graph = model.SemanticGraph;
        var cycles = FindCycles(graph);

        foreach (var detectedCycle in cycles)
        {
            if (detectedCycle.Length == 0)
            {
                continue;
            }

            var cycle = BuildCyclePath(detectedCycle[0], graph);
            if (cycle.Length == 0)
            {
                cycle = detectedCycle;
            }

            var explanation = new StringBuilder();
            explanation.AppendLine($"Dependency cycle explanation: {string.Join(" -> ", cycle.Select(node => FormatNodeLabel(node, graph)))}.");
            explanation.AppendLine("Cycle edges:");

            for (var i = 0; i < cycle.Length - 1; i++)
            {
                explanation.Append("- ");
                explanation.AppendLine(ExplainEdge(cycle[i], cycle[i + 1], graph));
            }

            // TODO: Include expression-level snippets once dependency edges capture richer expression provenance.
            // TODO: Highlight the specific AST nodes that produced each dependency edge when locations become available.
            // TODO: Group related or overlapping cycles into a single diagnostic when multi-cycle reporting is implemented.

            diagnostics.Add(SemanticDiagnostics.Error(
                explanation.ToString().TrimEnd(),
                // The first object of the cycle is where it is shown (G-309e).
                graph.Nodes.TryGetValue(cycle[0], out var first) ? first.Source.Location : null,
                nameof(SemanticCycleExplanationPass)));
        }
    }

    private static IReadOnlyList<string[]> FindCycles(SemanticGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var visitState = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var cycles = new List<string[]>();
        var reportedCycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in graph.Nodes.Keys)
        {
            VisitNode(key, graph, visitState, stack, cycles, reportedCycles);
        }

        return cycles;
    }

    private static string[] BuildCyclePath(string startNode, SemanticGraph graph)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startNode);
        ArgumentNullException.ThrowIfNull(graph);

        if (!graph.Nodes.ContainsKey(startNode))
        {
            return [];
        }

        var path = new List<string> { startNode };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startNode };

        return TryBuildCyclePath(startNode, startNode, graph, visited, path, out var cycle)
            ? cycle
            : [];
    }

    private static string ExplainEdge(string from, string to, SemanticGraph graph)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentNullException.ThrowIfNull(graph);

        var fromLabel = FormatNodeLabel(from, graph);
        var toLabel = FormatNodeLabel(to, graph);

        if (!graph.Nodes.TryGetValue(from, out var fromNode) || !graph.Nodes.TryGetValue(to, out var toNode))
        {
            return $"{fromLabel} depends on {toLabel}.";
        }

        if (fromNode.Source.ResolvedTables.Any(reference => string.Equals(reference.Key, to, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{fromLabel} resolves table dependency on {toLabel}.";
        }

        if (fromNode.Source.ResolvedFunctions.Any(reference => string.Equals(reference.Key, to, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{fromLabel} resolves function dependency on {toLabel}.";
        }

        if (fromNode.Source.ResolvedTypes.Any(reference => string.Equals(reference.Key, to, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{fromLabel} resolves type dependency on {toLabel}.";
        }

        if (fromNode.Source.ReferencedTables.Any(reference => string.Equals(reference, to, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{fromLabel} references table {toLabel}.";
        }

        if (fromNode.Source.ReferencedFunctions.Any(reference => string.Equals(reference, to, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{fromLabel} references function {toLabel}.";
        }

        if (fromNode.Source.ReferencedTypes.Any(reference => string.Equals(reference, to, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{fromLabel} references type {toLabel}.";
        }

        return $"{fromLabel} depends on {toLabel} through a semantic graph edge from {fromNode.Kind} to {toNode.Kind}.";
    }

    private static void VisitNode(
        string key,
        SemanticGraph graph,
        IDictionary<string, VisitState> visitState,
        Stack<string> stack,
        ICollection<string[]> cycles,
        ISet<string> reportedCycles)
    {
        if (visitState.TryGetValue(key, out var state))
        {
            if (state == VisitState.Visited)
            {
                return;
            }

            if (state == VisitState.Visiting)
            {
                ReportCycle(key, stack, cycles, reportedCycles);
            }

            return;
        }

        visitState[key] = VisitState.Visiting;
        stack.Push(key);

        if (graph.Edges.TryGetValue(key, out var dependencies))
        {
            foreach (var dependency in dependencies)
            {
                VisitNode(dependency, graph, visitState, stack, cycles, reportedCycles);
            }
        }

        stack.Pop();
        visitState[key] = VisitState.Visited;
    }

    private static void ReportCycle(
        string key,
        Stack<string> stack,
        ICollection<string[]> cycles,
        ISet<string> reportedCycles)
    {
        var path = stack.Reverse().ToArray();
        var cycleStart = Array.FindIndex(path, candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
        if (cycleStart < 0)
        {
            return;
        }

        var cycle = path[cycleStart..].Concat([key]).ToArray();
        var signature = CreateCycleSignature(cycle);
        if (!reportedCycles.Add(signature))
        {
            return;
        }

        cycles.Add(cycle);
    }

    private static bool TryBuildCyclePath(
        string current,
        string startNode,
        SemanticGraph graph,
        ISet<string> visited,
        List<string> path,
        out string[] cycle)
    {
        if (!graph.Edges.TryGetValue(current, out var dependencies))
        {
            cycle = [];
            return false;
        }

        foreach (var dependency in dependencies)
        {
            if (string.Equals(dependency, startNode, StringComparison.OrdinalIgnoreCase))
            {
                cycle = [.. path, startNode];
                return true;
            }

            if (!visited.Add(dependency))
            {
                continue;
            }

            path.Add(dependency);

            if (TryBuildCyclePath(dependency, startNode, graph, visited, path, out cycle))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
            visited.Remove(dependency);
        }

        cycle = [];
        return false;
    }

    private static string CreateCycleSignature(IReadOnlyList<string> cycle)
    {
        if (cycle.Count == 0)
        {
            return string.Empty;
        }

        var loop = cycle.Take(cycle.Count - 1).ToArray();
        if (loop.Length == 0)
        {
            return cycle[0].ToUpperInvariant();
        }

        var rotations = Enumerable.Range(0, loop.Length)
            .Select(index => loop[index..].Concat(loop[..index]).Select(static value => value.ToUpperInvariant()))
            .Select(parts => string.Join("->", parts))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return rotations[0];
    }

    private static string FormatNodeLabel(string key, SemanticGraph graph)
    {
        if (!graph.Nodes.TryGetValue(key, out var node))
        {
            return key;
        }

        var displayName = string.IsNullOrWhiteSpace(node.Source.SchemaName)
            ? node.Source.Name
            : $"{node.Source.SchemaName}.{node.Source.Name}";

        return $"{node.Kind} {displayName}";
    }

    private enum VisitState
    {
        Visiting,
        Visited,
    }
}
