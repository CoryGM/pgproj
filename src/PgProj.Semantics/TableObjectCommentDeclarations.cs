using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Applies <c>COMMENT ON TRIGGER|POLICY name ON table</c> to the triggers and policies the project declares (G-163, G-164), after every file is declared, so the comment may be in another file from the object.
/// The last statement wins; <c>IS NULL</c> and an empty string state that the object has no comment (PostgreSQL stores them as none); a comment that names an object the project does not declare is ignored.
/// (A table constraint's comment is applied by <see cref="ConstraintPass"/>, since constraints are built after the declarations.)
/// </summary>
internal static class TableObjectCommentDeclarations
{
    public static void Apply(IEnumerable<SqlSyntaxTree> trees, DatabaseModel model)
    {
        if (model.Triggers is not IDictionary<string, TriggerDefinition> triggers)
        {
            throw new InvalidOperationException($"{nameof(DatabaseModel)}.{nameof(DatabaseModel.Triggers)} must be backed by a mutable dictionary during declaration.");
        }

        if (model.Policies is not IDictionary<string, PolicyDefinition> policies)
        {
            throw new InvalidOperationException($"{nameof(DatabaseModel)}.{nameof(DatabaseModel.Policies)} must be backed by a mutable dictionary during declaration.");
        }

        foreach (var tree in trees)
        {
            foreach (var node in tree.ObjectComments.Where(static node => node.ObjectKind is "Trigger" or "Policy"))
            {
                var comment = string.IsNullOrEmpty(node.CommentText) ? null : node.CommentText;
                if (node.ObjectKind == "Trigger")
                {
                    var key = DatabaseModel.CreateTriggerKey(node.SchemaName, node.TableName, node.ObjectName ?? string.Empty);
                    if (triggers.TryGetValue(key, out var trigger))
                    {
                        triggers[key] = trigger with { Comment = comment, CommentStated = true };
                    }
                }
                else
                {
                    var key = DatabaseModel.CreatePolicyKey(node.SchemaName, node.TableName, node.ObjectName ?? string.Empty);
                    if (policies.TryGetValue(key, out var policy))
                    {
                        policies[key] = policy with { Comment = comment, CommentStated = true };
                    }
                }
            }
        }
    }
}
