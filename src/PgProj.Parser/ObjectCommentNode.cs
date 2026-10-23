namespace PgProj.Parser;

/// <summary>
/// A transient marker produced from a standalone <c>COMMENT ON ...</c> statement.
/// It carries no independent deploy identity; it is consumed by the semantic layer
/// to annotate an already-known table, column, view, routine, index, sequence, or
/// type definition with <see cref="CommentText"/>.
/// </summary>
public sealed record ObjectCommentNode(
    string ObjectKind,
    string? SchemaName,
    string? TableName,
    string? ColumnName,
    string? CommentText) : SqlNode("ObjectComment", TableName, SchemaName)
{
    /// <summary>
    /// For <c>ObjectKind == "Function"</c> or <c>"Procedure"</c>, the comma-joined raw
    /// argument-type signature captured from the <c>COMMENT ON FUNCTION/PROCEDURE name(types)</c>
    /// argument list, in the same format produced by <see cref="RoutineSignature.RenderArgumentTypeSignature"/>.
    /// Null for non-routine object kinds or when no argument list was present.
    /// </summary>
    public string? ArgumentTypeSignature { get; init; }

    /// <summary>
    /// For <c>ObjectKind == "Constraint"</c>, <c>"Trigger"</c>, or <c>"Policy"</c> (<c>COMMENT ON CONSTRAINT|TRIGGER|POLICY name ON table</c>), the name of the constraint, trigger, or policy;
    /// <c>TableName</c> is the table's (G-161, G-163).
    /// </summary>
    public string? ObjectName { get; init; }
}

