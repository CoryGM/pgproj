namespace PgProj.Parser;

public sealed record IndexKeyElementNode(
    string? Name,
    string? RawExpression,
    string? CollationName,
    string? OperatorClassName,
    IReadOnlyList<string> OperatorClassOptions,
    bool IsDescending,
    bool? NullsFirst)
{
    /// <summary>The pg_query tree (JSON) of a key expression, kept for the canonical comparison (G-73).</summary>
    public string? ExpressionAst { get; init; }
}

public sealed record IndexNode(
    string? Name,
    string? SchemaName,
    string? TableName,
    string? AccessMethod) : SqlNode("Index", Name, SchemaName)
{
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    public bool IsUnique { get; init; }

    /// <summary>True for <c>NULLS NOT DISTINCT</c> on a unique index (PostgreSQL 15; G-262): a NULL is equal to another NULL.</summary>
    public bool NullsNotDistinct { get; init; }

    /// <summary>True when the project states <c>ALTER TABLE t CLUSTER ON</c> this one (G-256).</summary>
    public bool IsClustered { get; init; }

    /// <summary>True when the project states <c>ALTER TABLE t REPLICA IDENTITY USING INDEX</c> this one (G-255).</summary>
    public bool IsReplicaIdentity { get; init; }

    public IReadOnlyList<IndexKeyElementNode> KeyElements { get; init; } = [];

    public IReadOnlyList<string> IncludeColumns { get; init; } = [];

    public string? RawPredicateExpression { get; init; }

    /// <summary>The pg_query tree (JSON) of the WHERE predicate, kept for the canonical comparison (G-73).</summary>
    public string? PredicateAst { get; init; }

    /// <summary>The index's <c>WITH (...)</c> storage parameters, each as written (G-195).</summary>
    public IReadOnlyList<TableStorageOptionNode> StorageOptions { get; init; } = [];

    /// <summary>
    /// True when the index was written without a name (<c>CREATE INDEX ON t (...)</c>) and <see cref="ConstraintNameAssigner"/> gave it the name PostgreSQL gives
    /// it (G-225). It is deployed without a name, as written.
    /// </summary>
    public bool IsNameGenerated { get; init; }

    /// <summary>
    /// The key of the table a <c>LIKE</c> clause copied this index from (G-221); null for one the project declares itself. The table's <c>CREATE TABLE</c>
    /// makes it, so it is not created on its own with the table; once the table exists it is changed like any other. Not compared.
    /// </summary>
    public string? CopiedByLike { get; init; }
}
