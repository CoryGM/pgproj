namespace PgProj.Semantics;

internal sealed class ErrorRecoveryPass
{
    public void Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var diagnostics = CollectDiagnostics(model);
        model.RecoveryDiagnostics = diagnostics;

        var hasRecoveryDiagnostics = diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error);
        var recoveredNodeCount = 0;

        foreach (var node in model.GetAllNodes())
        {
            node.IsRecovered = false;
            node.HasErrors = false;

            var nodeDiagnostics = diagnostics
                .Where(diagnostic => MatchesNode(node, diagnostic))
                .ToArray();

            if (nodeDiagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error))
            {
                MarkInvalidObject(node, nodeDiagnostics[0].Message);
                recoveredNodeCount++;
                continue;
            }

            if (RequiresPartialState(node))
            {
                ContinueWithPartialState(node);
                recoveredNodeCount++;
            }
        }

        model.RecoveryActive = hasRecoveryDiagnostics || recoveredNodeCount > 0;

        // TODO: Add savepoint-style rollback for isolated semantic sections once passes expose transactional boundaries.
        // TODO: Allow partial dependency graph construction when some nodes fail binding or cycle analysis.
        // TODO: Emit recovery-aware partial script generation warnings once deployment planning consumes recovery metadata.
        // TODO: Make semantic diffing recovery-aware so invalid nodes can be compared with degraded fidelity.
    }

    private static void MarkInvalidObject(ISemanticNode node, string message)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        node.IsRecovered = true;
        node.HasErrors = true;
    }

    private static void ContinueWithPartialState(ISemanticNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        node.IsRecovered = true;
    }

    private static IReadOnlyList<SemanticDiagnostic> CollectDiagnostics(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.Diagnostics
            .Where(static diagnostic => diagnostic is not null)
            .OrderBy(static diagnostic => diagnostic.Severity)
            .ThenBy(static diagnostic => diagnostic.Source, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool RequiresPartialState(ISemanticNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return HasPartialReferences(node.ReferencedTables, node.ResolvedTables)
            || HasPartialReferences(node.ReferencedColumns, node.ResolvedColumns)
            || HasPartialReferences(node.ReferencedTypes, node.ResolvedTypes)
            || HasPartialReferences(node.ReferencedFunctions, node.ResolvedFunctions);
    }

    private static bool HasPartialReferences<TNode>(IReadOnlyList<string> references, IReadOnlyList<TNode> resolved)
        where TNode : ISemanticNode
        => references.Count > 0 && resolved.Count < references.Count;

    private static bool MatchesNode(ISemanticNode node, SemanticDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(diagnostic);

        if (string.IsNullOrWhiteSpace(diagnostic.Message))
        {
            return false;
        }

        return diagnostic.Message.Contains($"'{node.Key}'", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Message.Contains($"'{node.Name}'", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Message.Contains($"{node.Kind} '{node.Key}'", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Message.Contains($"{node.Kind} '{node.Name}'", StringComparison.OrdinalIgnoreCase);
    }
}
