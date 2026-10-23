// Implement the SemanticCyclePass for the semantic pipeline.
// This pass detects semantic-level cycles BEFORE dependency graph construction.
// It ensures that the semantic model is internally consistent and that no object
// depends (directly or indirectly) on itself.
//
// === Goals ===
// 1. Detect cycles among semantic objects using their ReferencedTables,
//    ReferencedColumns, ReferencedTypes, and ReferencedFunctions.
// 2. Surface cycles as SemanticDiagnostic errors (never throw exceptions).
// 3. Prepare the semantic model for DependencyGraph construction.
// 4. Keep the implementation lightweight — this is NOT the full dependency graph.
//
// === Requirements ===
// - Create a SemanticCyclePass class with:
//       void Execute(DatabaseModel model)
// - Build a simple directed graph of semantic nodes using their resolved references.
// - Perform DFS-based cycle detection:
//       - Track visited nodes
//       - Track recursion stack
//       - Report cycles when a node is revisited while in the recursion stack
// - Add diagnostics using SemanticDiagnostics.Error().
// - Leave TODO comments for deeper cycle detection once expression mapping expands.
//
// === Copilot Guidance ===
// Generate scaffolding and initial implementations.
// Focus on structure, not completeness.
// Leave TODO comments for deeper cycle logic.
// Begin by generating SemanticCyclePass.cs with an Execute() method and a DFS-based
// cycle detection helper.


namespace PgProj.Semantics;

internal sealed class SemanticCyclePass
{
    public void Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Diagnostics is not IList<SemanticDiagnostic> diagnostics)
        {
            return;
        }

        var nodes = model.GetAllNodes().ToDictionary(static node => node.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var edges = nodes.ToDictionary(
            static pair => pair.Key,
            pair => GetSemanticDependencies(pair.Value)
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .Where(nodes.ContainsKey)
                .Where(reference => !string.Equals(reference, pair.Key, StringComparison.Ordinal))
                .Distinct(PgProj.Parser.SqlIdentifiers.NameComparer)
                .ToArray(),
            PgProj.Parser.SqlIdentifiers.NameComparer);

        var visitState = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var reportedCycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in nodes.Keys)
        {
            VisitNode(key, edges, visitState, stack, reportedCycles, diagnostics, key => SemanticPlaces.Find(model, key));
        }
    }

    private static IEnumerable<string> GetSemanticDependencies(ISemanticNode node)
    {
        foreach (var reference in node.ResolvedTables.Select(static table => table.Key))
        {
            yield return reference;
        }

        foreach (var reference in node.ResolvedFunctions.Select(static function => function.Key))
        {
            yield return reference;
        }

        foreach (var reference in node.ResolvedTypes.Select(static type => type.Key))
        {
            yield return reference;
        }

        foreach (var reference in node.ResolvedColumns
                     .Select(static column => TryResolveOwningTableKey(column.Key))
                     .OfType<string>())
        {
            yield return reference;
        }

        foreach (var reference in node.ReferencedTables)
        {
            yield return reference;
        }

        foreach (var reference in node.ReferencedFunctions)
        {
            yield return reference;
        }

        foreach (var reference in node.ReferencedTypes)
        {
            yield return reference;
        }

        foreach (var reference in node.ReferencedColumns
                     .Select(TryResolveOwningTableKey)
                     .OfType<string>())
        {
            yield return reference;
        }

        // TODO: Expand dependency extraction when expression mapping surfaces richer semantic edges for CASE, WINDOW, CTE, and subquery constructs.
        // TODO: Add deeper intra-object cycle detection once semantic nodes expose nested expression-to-expression dependency relationships.
    }

    private static string? TryResolveOwningTableKey(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var separatorIndex = reference.LastIndexOf('.');
        if (separatorIndex < 0)
        {
            return null;
        }

        return reference[..separatorIndex];
    }

    private static void VisitNode(
        string key,
        IReadOnlyDictionary<string, string[]> edges,
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
            $"Semantic dependency cycle detected: {string.Join(" -> ", cycle)}.",
            // The first object of the cycle is where it is shown (G-309e).
            place(cycle[0]),
            nameof(SemanticCyclePass)));
    }

    private enum VisitState
    {
        Visiting,
        Visited,
    }
}
