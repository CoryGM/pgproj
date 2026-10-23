namespace PgProj.Parser;

/// <summary>
/// Applies <c>ALTER PUBLICATION</c> across all syntax trees (G-258). <c>pg_dump</c> writes a publication as an empty <c>CREATE PUBLICATION</c> and then one
/// <c>ALTER PUBLICATION ... ADD TABLE</c> for each table, and the publication and the statements may be in different files, so this runs over every tree before any semantic pass, like
/// <see cref="AlterDomainCheckApplier"/>: <c>ADD</c> appends tables and schemas, <c>DROP</c> removes them, <c>SET</c> replaces them, and <c>SET (...)</c> changes the options, in tree order
/// and then statement order. A publication the project does not declare is a warning, the statement ignored. The result contains none of the marker nodes.
/// </summary>
public static class AlterPublicationApplier
{
    /// <summary>The <see cref="SqlDiagnostic.Source"/> of the diagnostics this class adds to a syntax tree.</summary>
    public const string DiagnosticSource = "AlterPublication";

    public static IReadOnlyList<SqlSyntaxTree> Apply(IReadOnlyList<SqlSyntaxTree> trees)
    {
        ArgumentNullException.ThrowIfNull(trees);

        if (!trees.Any(static tree => tree.Nodes.Any(static node => node is AlterPublicationNode)))
        {
            return trees;
        }

        var nodesByTree = trees.Select(static tree => tree.Nodes.ToList()).ToList();
        var diagnosticsByTree = trees.Select(static tree => tree.Diagnostics.ToList()).ToList();

        var publications = new Dictionary<string, (int TreeIndex, int NodeIndex)>(StringComparer.Ordinal);
        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            for (var nodeIndex = 0; nodeIndex < nodesByTree[treeIndex].Count; nodeIndex++)
            {
                if (nodesByTree[treeIndex][nodeIndex] is PublicationNode { Name: { } name })
                {
                    publications.TryAdd(name, (treeIndex, nodeIndex));
                }
            }
        }

        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            foreach (var alter in nodesByTree[treeIndex].OfType<AlterPublicationNode>().ToList())
            {
                if (alter.Name is null || !publications.TryGetValue(alter.Name, out var location))
                {
                    diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                        SqlDiagnosticSeverity.Warning,
                        $"ALTER PUBLICATION {alter.Name}: the project does not declare that publication, so the statement is ignored.",
                        DiagnosticSource));
                    continue;
                }

                var publication = (PublicationNode)nodesByTree[location.TreeIndex][location.NodeIndex];
                nodesByTree[location.TreeIndex][location.NodeIndex] = Fold(publication, alter);
            }
        }

        var result = new List<SqlSyntaxTree>(trees.Count);
        for (var treeIndex = 0; treeIndex < trees.Count; treeIndex++)
        {
            result.Add(trees[treeIndex] with
            {
                Nodes = nodesByTree[treeIndex].Where(static node => node is not AlterPublicationNode).ToArray(),
                Diagnostics = diagnosticsByTree[treeIndex],
            });
        }

        return result;
    }

    private static PublicationNode Fold(PublicationNode publication, AlterPublicationNode alter)
    {
        var options = new Dictionary<string, string>(publication.Options, StringComparer.Ordinal);
        foreach (var (name, value) in alter.Options)
        {
            options[name] = value;
        }

        bool SameTable(PublicationTableNode left, PublicationTableNode right)
            => string.Equals(left.SchemaName, right.SchemaName, StringComparison.Ordinal) && string.Equals(left.TableName, right.TableName, StringComparison.Ordinal);

        IReadOnlyList<PublicationTableNode> tables;
        IReadOnlyList<string> schemas;
        switch (alter.Action)
        {
            case "DROP":
                tables = publication.Tables.Where(table => !alter.Tables.Any(dropped => SameTable(table, dropped))).ToArray();
                schemas = publication.Schemas.Where(schema => !alter.Schemas.Contains(schema, StringComparer.Ordinal)).ToArray();
                break;
            case "SET" when alter.Tables.Count > 0 || alter.Schemas.Count > 0:
                tables = alter.Tables;
                schemas = alter.Schemas;
                break;
            default:
                tables = [.. publication.Tables.Where(table => !alter.Tables.Any(added => SameTable(table, added))), .. alter.Tables];
                schemas = [.. publication.Schemas, .. alter.Schemas.Where(schema => !publication.Schemas.Contains(schema, StringComparer.Ordinal))];
                break;
        }

        return publication with { Tables = tables, Schemas = schemas, Options = options };
    }
}
