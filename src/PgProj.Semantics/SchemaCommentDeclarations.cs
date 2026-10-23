using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Reads <c>CREATE SCHEMA</c> (G-284) and <c>COMMENT ON SCHEMA</c> (G-160) statements into <see cref="DatabaseModel.SchemaDefinitions"/>. It runs after every file is declared,
/// so a comment may be in a different file from the <c>CREATE SCHEMA</c> (or from every object in the schema); when a schema is commented more than once the last statement
/// wins, as it does in the database.
/// </summary>
internal static class SchemaCommentDeclarations
{
    public static void Declare(IEnumerable<SqlSyntaxTree> trees, DatabaseModel model)
    {
        if (model.SchemaDefinitions is not IDictionary<string, SchemaDefinition> schemas)
        {
            throw new InvalidOperationException($"{nameof(DatabaseModel)}.{nameof(DatabaseModel.SchemaDefinitions)} must be backed by a mutable dictionary during declaration.");
        }

        var treeList = trees as IReadOnlyCollection<SqlSyntaxTree> ?? trees.ToArray();
        foreach (var node in treeList.SelectMany(static tree => tree.Schemas))
        {
            if (string.IsNullOrWhiteSpace(node.Name))
            {
                continue;
            }

            var key = SchemaKeys.Create(node.Name);
            schemas[key] = schemas.TryGetValue(key, out var declared)
                ? declared with { IsDeclared = true }
                : new SchemaDefinition(key, node.Name.Trim()) { DeclarationNode = node, IsDeclared = true };
        }

        foreach (var tree in treeList)
        {
            foreach (var node in tree.ObjectComments.Where(static node => node.ObjectKind == "Schema"))
            {
                var name = node.TableName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var key = SchemaKeys.Create(name);
                schemas[key] = (schemas.TryGetValue(key, out var existing) ? existing : new SchemaDefinition(key, name.Trim()) { DeclarationNode = node }) with
                {
                    // PostgreSQL stores an empty comment as none.
                    Comment = string.IsNullOrEmpty(node.CommentText) ? null : node.CommentText,
                    IsCommentStated = true,
                };
            }
        }
    }
}
