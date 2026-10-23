using PgProj.Diff;

namespace PgProj.Deploy;

/// <summary>
/// One <c>COMMENT ON</c> statement taken out of another operation's SQL for <c>publish --comments-only</c> (G-169). It runs inside a
/// <c>DO</c> block that skips it, with a warning, when the object it names does not exist in the database yet (a new table, a new
/// column), because the other operations that would create the object are not part of a comments-only script.
/// </summary>
public sealed record class CommentStatementOperation : SqlOperation
{
    /// <summary>The statement as pgproj generated it, without its terminating semicolon.</summary>
    public required string Statement { get; init; }

    /// <summary>The tag of the <c>DO</c> block's body, which the statement (a comment's own text is dollar-quoted too) must not contain.</summary>
    private const string BodyTag = "$pgproj_comments_only$";

    public override string GenerateSql()
        => $"""
            DO {BodyTag}
            BEGIN
                {Statement};
            EXCEPTION WHEN undefined_table OR undefined_column OR undefined_object OR undefined_function OR invalid_schema_name THEN
                RAISE WARNING 'pgproj publish --comments-only skipped a comment: % (%)', {Quote(Head(Statement))}, SQLERRM;
            END
            {BodyTag};
            """;

    /// <summary>The statement up to its <c>IS</c>: what the comment is on, for the warning.</summary>
    internal static string Head(string statement)
    {
        var match = System.Text.RegularExpressions.Regex.Match(statement, @"^(.*?)\s+IS\s", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var head = (match.Success ? match.Groups[1].Value : statement).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return head.Length > 200 ? head[..200] + "..." : head;
    }

    private static string Quote(string text)
        => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The comment statements of <paramref name="batch"/>, in order, as a batch of their own: no other operation, and none of the
    /// project's predeploy or postdeploy scripts (G-169).
    /// </summary>
    public static OperationBatch CommentsOnly(OperationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var operations = new List<SqlOperation>();
        foreach (var operation in batch.Operations)
        {
            foreach (var statement in SqlStatementSplitter.Split(operation.GenerateSql()))
            {
                var text = WithoutLeadingComments(statement);
                if (text.StartsWith("COMMENT ON ", StringComparison.OrdinalIgnoreCase))
                {
                    operations.Add(new CommentStatementOperation { NodeId = operation.NodeId, NodeKind = operation.NodeKind, Statement = text });
                }
            }
        }

        return new OperationBatch { Operations = operations };
    }

    /// <summary>The statement without the <c>-- reason</c> lines and block comments before it.</summary>
    public static string WithoutLeadingComments(string statement)
    {
        var text = statement.TrimStart();
        while (true)
        {
            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                var end = text.IndexOf('\n', StringComparison.Ordinal);
                text = end < 0 ? string.Empty : text[(end + 1)..].TrimStart();
            }
            else if (text.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = text.IndexOf("*/", 2, StringComparison.Ordinal);
                text = end < 0 ? string.Empty : text[(end + 2)..].TrimStart();
            }
            else
            {
                return text;
            }
        }
    }
}
