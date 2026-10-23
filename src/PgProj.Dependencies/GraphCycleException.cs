namespace PgProj.Dependencies;

public sealed class GraphCycleException : Exception
{
    public GraphCycleException(IReadOnlyList<string> cyclePath)
        : base(CreateMessage(cyclePath))
    {
        CyclePath = cyclePath;
    }

    public IReadOnlyList<string> CyclePath { get; }

    private static string CreateMessage(IReadOnlyList<string> cyclePath)
    {
        ArgumentNullException.ThrowIfNull(cyclePath);

        return cyclePath.Count == 0
            ? "A cycle was detected in the dependency graph."
            : $"A cycle was detected in the dependency graph: {string.Join(" -> ", cyclePath)}.";
    }
}
