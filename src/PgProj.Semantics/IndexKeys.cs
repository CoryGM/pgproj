using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Finds the model key of the index a parsed <see cref="IndexNode"/> declared. Usually that is the key its own schema and name make, but an
/// index declared without a schema takes its table's schema (G-113, <see cref="DeclarationPass"/>), so its key is no longer what the node
/// alone says; the index whose declaration is this node is found instead.
/// </summary>
internal static class IndexKeys
{
    public static string Find(IReadOnlyDictionary<string, IndexDefinition> indexes, IndexNode node)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(node);

        var indexName = node.Name ?? (node.TableName is null ? "unknown_index" : $"{node.TableName}_idx");
        var key = DatabaseModel.CreateKey(node.SchemaName, indexName);
        if (indexes.ContainsKey(key) || !string.IsNullOrWhiteSpace(node.SchemaName))
        {
            return key;
        }

        return indexes.Values.FirstOrDefault(index => ReferenceEquals(index.DeclarationNode, node))?.Key ?? key;
    }
}
