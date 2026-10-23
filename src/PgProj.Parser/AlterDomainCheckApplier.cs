namespace PgProj.Parser;

/// <summary>
/// Applies <c>ALTER DOMAIN ... ADD CONSTRAINT ... CHECK</c> across all syntax trees (G-233). It is the only way to write a domain's check <c>NOT VALID</c>
/// (<c>CREATE DOMAIN</c> has no such clause), and the domain and the <c>ALTER DOMAIN</c> may be in different files, so this runs over every tree before
/// any semantic pass, like <see cref="AlterTableColumnApplier"/>: the check is appended to its domain's <see cref="DomainNode.CheckConstraints"/>
/// (in tree order, then statement order), and everything downstream sees an ordinary check. A domain the project does not declare is a warning, the
/// constraint ignored. The result contains none of the marker nodes.
/// </summary>
public static class AlterDomainCheckApplier
{
    /// <summary>The <see cref="SqlDiagnostic.Source"/> of the diagnostics this class adds to a syntax tree.</summary>
    public const string DiagnosticSource = "AlterDomain";

    public static IReadOnlyList<SqlSyntaxTree> Apply(IReadOnlyList<SqlSyntaxTree> trees)
    {
        ArgumentNullException.ThrowIfNull(trees);

        if (!trees.Any(static tree => tree.Nodes.Any(static node => node is AlterDomainCheckNode)))
        {
            return trees;
        }

        var nodesByTree = trees.Select(static tree => tree.Nodes.ToList()).ToList();
        var diagnosticsByTree = trees.Select(static tree => tree.Diagnostics.ToList()).ToList();

        var domains = new Dictionary<string, (int TreeIndex, int NodeIndex)>(SqlIdentifiers.NameComparer);
        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            for (var nodeIndex = 0; nodeIndex < nodesByTree[treeIndex].Count; nodeIndex++)
            {
                if (nodesByTree[treeIndex][nodeIndex] is DomainNode { Name: { } name } domain)
                {
                    domains.TryAdd(Key(domain.SchemaName, name), (treeIndex, nodeIndex));
                }
            }
        }

        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            foreach (var alter in nodesByTree[treeIndex].OfType<AlterDomainCheckNode>().ToList())
            {
                if (alter.DomainName is null || !domains.TryGetValue(Key(alter.SchemaName, alter.DomainName), out var location))
                {
                    diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                        SqlDiagnosticSeverity.Warning,
                        $"ALTER DOMAIN {Describe(alter.SchemaName, alter.DomainName)} ADD CONSTRAINT: the project does not declare that domain, so the constraint is ignored.",
                        DiagnosticSource));
                    continue;
                }

                var domain = (DomainNode)nodesByTree[location.TreeIndex][location.NodeIndex];
                nodesByTree[location.TreeIndex][location.NodeIndex] = domain with { CheckConstraints = [.. domain.CheckConstraints, alter.Check] };
            }
        }

        var result = new List<SqlSyntaxTree>(trees.Count);
        for (var treeIndex = 0; treeIndex < trees.Count; treeIndex++)
        {
            result.Add(trees[treeIndex] with
            {
                Nodes = nodesByTree[treeIndex].Where(static node => node is not AlterDomainCheckNode).ToArray(),
                Diagnostics = diagnosticsByTree[treeIndex],
            });
        }

        return result;
    }

    private static string Key(string? schemaName, string name) => string.IsNullOrEmpty(schemaName) ? name : schemaName + "." + name;

    private static string Describe(string? schemaName, string? name) => string.IsNullOrEmpty(schemaName) ? name ?? string.Empty : schemaName + "." + name;
}
