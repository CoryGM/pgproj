namespace PgProj.Parser;

public sealed record ConstraintExclusionElementNode(
    string? Name,
    string? RawExpression,
    string OperatorName)
{
    /// <summary>The pg_query JSON of the expression, for the comparison (G-208); null for a column.</summary>
    public string? ExpressionAst { get; init; }
}

public sealed record ConstraintNode(
    string? Name,
    string? SchemaName,
    string? TableName,
    string? ConstraintType) : SqlNode("Constraint", Name, SchemaName)
{
    /// <summary>Ordered key columns for table-level or inline PK/UNIQUE constraints.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>Schema of the foreign key's referenced (target) table, if known.</summary>
    public string? ForeignKeyReferencedSchemaName { get; init; }

    /// <summary>True when <see cref="ForeignKeyReferencedSchemaName"/> was not written but defaulted (G-93); see <see cref="SqlNode.IsSchemaDefaulted"/>.</summary>
    public bool IsForeignKeyReferencedSchemaDefaulted { get; init; }

    /// <summary>Name of the foreign key's referenced (target) table.</summary>
    public string? ForeignKeyReferencedTableName { get; init; }

    /// <summary>Ordered local columns participating in the foreign key.</summary>
    public IReadOnlyList<string> ForeignKeyColumns { get; init; } = [];

    /// <summary>Ordered columns on the referenced table that the foreign key targets.</summary>
    public IReadOnlyList<string> ForeignKeyReferencedColumns { get; init; } = [];

    /// <summary>Raw pg_query update action code (e.g. "a", "r", "c", "n", "d").</summary>
    public string? ForeignKeyUpdateAction { get; init; }

    /// <summary>Raw pg_query delete action code (e.g. "a", "r", "c", "n", "d").</summary>
    public string? ForeignKeyDeleteAction { get; init; }

    /// <summary>The columns of <c>ON DELETE SET NULL (cols)</c> or <c>SET DEFAULT (cols)</c> (PostgreSQL 15), in the order written; empty when the action applies to every referencing column (G-182).</summary>
    public IReadOnlyList<string> ForeignKeyDeleteSetColumns { get; init; } = [];

    /// <summary>Raw pg_query match type code (e.g. "s", "f", "p").</summary>
    public string? ForeignKeyMatchType { get; init; }

    /// <summary>Raw SQL text of a CHECK constraint's predicate expression.</summary>
    public string? RawCheckExpression { get; init; }

    /// <summary>The pg_query tree of a CHECK constraint's expression (JSON), kept for the canonical comparison (G-73).</summary>
    public string? CheckExpressionAst { get; init; }

    /// <summary>Whether the constraint is DEFERRABLE.</summary>
    public bool IsDeferrable { get; init; }

    /// <summary>Whether a DEFERRABLE constraint is INITIALLY DEFERRED (true) or INITIALLY IMMEDIATE (false/null).</summary>
    public bool? IsInitiallyDeferred { get; init; }

    /// <summary>
    /// Whether a CHECK or FOREIGN KEY constraint is enforced (PostgreSQL 18's <c>NOT ENFORCED</c> makes it false; G-121). Always true for any
    /// other kind, and for every constraint written for an earlier version.
    /// </summary>
    public bool IsEnforced { get; init; } = true;

    /// <summary>
    /// Whether a CHECK or FOREIGN KEY constraint was written <c>NOT VALID</c> (G-234): added without checking the rows already in the table. A
    /// constraint is validated later with <c>VALIDATE CONSTRAINT</c>. False for every other kind and for a <c>NOT ENFORCED</c> one.
    /// </summary>
    public bool IsNotValid { get; init; }

    /// <summary>
    /// Whether the last column of a PRIMARY KEY or UNIQUE constraint is <c>WITHOUT OVERLAPS</c> (PostgreSQL 18's temporal key; G-121): its
    /// range may not overlap for the same values of the other key columns. The column is in <see cref="KeyColumns"/> like the others.
    /// </summary>
    public bool WithoutOverlaps { get; init; }

    /// <summary>True for <c>UNIQUE NULLS NOT DISTINCT</c> (PostgreSQL 15; G-262): a NULL is equal to another NULL.</summary>
    public bool NullsNotDistinct { get; init; }

    /// <summary>
    /// Whether the last column of a FOREIGN KEY is <c>PERIOD</c> on both sides (PostgreSQL 18; G-121): the range must be covered by the
    /// referenced rows' ranges. The column is in <see cref="ForeignKeyColumns"/> and <see cref="ForeignKeyReferencedColumns"/> like the others.
    /// </summary>
    public bool ForeignKeyUsesPeriod { get; init; }

    /// <summary>Access method for an EXCLUDE constraint (e.g. "gist").</summary>
    public string? ExclusionAccessMethod { get; init; }

    /// <summary>Ordered exclusion elements ("column WITH operator") for an EXCLUDE constraint.</summary>
    public IReadOnlyList<ConstraintExclusionElementNode> ExclusionElements { get; init; } = [];

    /// <summary>Raw SQL text of an EXCLUDE constraint's WHERE predicate, if any.</summary>
    public string? RawExclusionPredicateExpression { get; init; }

    /// <summary>True for <c>CHECK (...) NO INHERIT</c>: the check is the table's alone, not its children's (G-194).</summary>
    public bool IsNoInherit { get; init; }

    /// <summary>True when the project states <c>ALTER TABLE t CLUSTER ON</c> this key constraint's index (G-256).</summary>
    public bool IsClustered { get; init; }

    /// <summary>True when the project states <c>ALTER TABLE t REPLICA IDENTITY USING INDEX</c> this key constraint's index (G-255).</summary>
    public bool IsReplicaIdentity { get; init; }

    /// <summary>The <c>WITH (...)</c> storage parameters of a primary key's, unique constraint's, or exclusion constraint's index, each as written (G-195).</summary>
    public IReadOnlyList<TableStorageOptionNode> IndexStorageOptions { get; init; } = [];

    /// <summary>The <c>INCLUDE (...)</c> columns of a primary key's, unique constraint's, or exclusion constraint's index, in order (G-200).</summary>
    public IReadOnlyList<string> IncludeColumns { get; init; } = [];

    /// <summary>
    /// Distinct column names referenced by a CHECK constraint's expression, in order of first appearance.
    /// PostgreSQL names an unnamed CHECK after its column only when exactly one is referenced.
    /// </summary>
    public IReadOnlyList<string> CheckReferencedColumnNames { get; init; } = [];

    /// <summary>
    /// True when <see cref="SqlNode.Name"/> was not written in the SQL and was generated by
    /// <see cref="ConstraintNameAssigner"/> to match the default name PostgreSQL assigns (for example
    /// <c>orders_total_check</c>). The parser itself leaves unnamed constraints unnamed.
    /// </summary>
    public bool IsNameGenerated { get; init; }

    /// <summary>
    /// True when the constraint was declared inside a <c>CREATE TABLE</c> statement (inline or table-level)
    /// rather than added later with <c>ALTER TABLE</c>. PostgreSQL discards a redundant PRIMARY KEY/UNIQUE
    /// constraint only in that case.
    /// </summary>
    public bool IsDeclaredInCreateTable { get; init; }

    /// <summary>True when the constraint is written in the element list of <c>CREATE TABLE ... PARTITION OF parent (...)</c> (G-266), whose <c>CREATE TABLE</c> makes it.</summary>
    public bool IsInPartitionOfList { get; init; }

    /// <summary>
    /// The key of the table a <c>LIKE</c> clause copied this constraint from (G-221); null for one the project declares itself. The table's <c>CREATE TABLE</c>
    /// makes it, so it is not created on its own with the table; once the table exists it is changed like any other. Not compared.
    /// </summary>
    public string? CopiedByLike { get; init; }

    private readonly PlaceSlot<SourceRange> _referencedTableRange = PlaceSlot<SourceRange>.Empty;

    /// <summary>Where a foreign key's referenced table is named (<c>REFERENCES app.customer</c>: <c>app.customer</c>), G-309e; never part of the node's equality.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SourceRange? ReferencedTableRange
    {
        get => _referencedTableRange.Value;
        init => _referencedTableRange = new PlaceSlot<SourceRange>(value);
    }
}
