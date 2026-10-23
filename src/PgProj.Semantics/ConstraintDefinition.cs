using PgProj.Parser;

namespace PgProj.Semantics;

public sealed record ConstraintExclusionElementDefinition(
    string? Name,
    string? RawExpression,
    string OperatorName)
{
    /// <summary>The pg_query JSON of the expression (G-208); null for a column.</summary>
    public string? ExpressionAst { get; init; }

    /// <summary>
    /// The expression's comparison form, from its tree (G-208): the project's text and PostgreSQL's rendering of it differ in spacing and
    /// parentheses. Null for a column, or when no tree was kept; <see cref="RawExpression"/> is what is deployed.
    /// </summary>
    public string? NormalizedExpression { get; init; }

    private readonly PgProj.Parser.PlaceSlot<SemanticLocation> _location = PgProj.Parser.PlaceSlot<SemanticLocation>.Empty;

    /// <summary>
    /// Where the definition was written: its file and range (G-309e), for the diagnostics about it; null for one read from a database. Never part of the
    /// definition's equality, never compared, and not in <c>--dump-model</c>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SemanticLocation? Location
    {
        get => _location.Value;
        init => _location = new PgProj.Parser.PlaceSlot<SemanticLocation>(value);
    }
}

public sealed record ConstraintDefinition(
    string Key,
    string Name,
    string? SchemaName,
    string? TableName,
    string? ConstraintType,
    IReadOnlyList<string> PrimaryKeyColumns,
    IReadOnlyList<string> UniqueColumns,
    string? ForeignKeyReferencedTable,
    IReadOnlyList<string> ForeignKeyReferencedColumns,
    string? RawCheckExpression,
    IReadOnlyList<string> ReferencedTables,
    IReadOnlyList<string> ReferencedColumns,
    IReadOnlyList<string> ReferencedTypes,
    IReadOnlyList<string> ReferencedFunctions,
    IReadOnlyList<TableDefinition> ResolvedTables,
    IReadOnlyList<ColumnDefinition> ResolvedColumns,
    IReadOnlyList<TypeDefinition> ResolvedTypes,
    IReadOnlyList<FunctionDefinition> ResolvedFunctions) : ISemanticNode
{
    /// <summary>The keys of the project operators (G-129) this object expressions use (G-138): it is created after them and dropped before them.</summary>
    public IReadOnlyList<string> ReferencedOperators { get; init; } = [];

    public string Kind => "Constraint";

    public ConstraintNode? DeclarationNode { get; init; }

    public ExpressionNode? CheckExpressionNode { get; init; }

    public IReadOnlyList<string> NormalizedExpressions { get; init; } = [];

    /// <summary>
    /// The comparison form of a CHECK expression, built from its parsed tree (G-73): PostgreSQL stores and prints the expression fully
    /// parenthesized and with its own casts, so the text is never compared. Null when there is no tree (a hand-built definition).
    /// </summary>
    public string? NormalizedCheckExpression { get; init; }

    /// <summary>
    /// A <c>CHECK</c> expression as PostgreSQL itself renders it (<c>pg_get_expr(conbin, conrelid)</c>), or null when not available (G-172).
    /// A live constraint carries its own rendering; a project constraint carries the rendering of its expression on a temporary copy of
    /// the live table, made during <c>diff</c>/<c>publish</c>. PostgreSQL types a literal by the column it is compared with
    /// (<c>status = 'x'</c> on an enum is <c>status = 'x'::s.st</c>, an <c>IN</c> list is <c>= ANY (ARRAY[...])</c>) and casts a domain
    /// column to its base type, which the parsed comparison cannot know.
    /// </summary>
    public string? ServerRenderedCheck { get; init; }

    /// <summary>Ordered local columns participating in a foreign key constraint.</summary>
    public IReadOnlyList<string> ForeignKeyColumns { get; init; } = [];

    /// <summary>Schema of the foreign key's referenced (target) table, if known.</summary>
    public string? ForeignKeyReferencedSchemaName { get; init; }

    /// <summary>Raw pg_query update action code (e.g. "a", "r", "c", "n", "d").</summary>
    public string? ForeignKeyUpdateAction { get; init; }

    /// <summary>Raw pg_query delete action code (e.g. "a", "r", "c", "n", "d").</summary>
    public string? ForeignKeyDeleteAction { get; init; }

    /// <summary>The columns of <c>ON DELETE SET NULL (cols)</c> or <c>SET DEFAULT (cols)</c>, in the order written; empty when the action applies to every referencing column. Compared as a set (G-182).</summary>
    public IReadOnlyList<string> ForeignKeyDeleteSetColumns { get; init; } = [];

    /// <summary>Raw pg_query match type code (e.g. "s", "f", "p").</summary>
    public string? ForeignKeyMatchType { get; init; }

    /// <summary>Whether the constraint is DEFERRABLE.</summary>
    /// <summary>The constraint's comment (G-161); null when it has none.</summary>
    public string? Comment { get; init; }

    /// <summary>
    /// True when the model states the comment: a project has a <c>COMMENT ON CONSTRAINT</c> for it (<c>IS NULL</c> states that it has none), a live database has one when the constraint has a comment.
    /// A comment is compared only for a constraint whose comment is stated, so a database's comment on a constraint the project does not mention is left alone (the same rule as a schema's, G-160).
    /// </summary>
    public bool CommentStated { get; init; }

    /// <summary>
    /// The comment on the index of a primary key, unique, or exclusion constraint (<c>COMMENT ON INDEX t_pkey</c>; the index has the constraint's name), which is the constraint's and not a separate index
    /// of the model (G-295); null when it has none.
    /// </summary>
    public string? IndexComment { get; init; }

    /// <summary>True when the model states <see cref="IndexComment"/>, compared on the same rule as <see cref="CommentStated"/>: only for a constraint whose index comment the project states.</summary>
    public bool IndexCommentStated { get; init; }

    public bool IsDeferrable { get; init; }

    /// <summary>Whether a DEFERRABLE constraint is INITIALLY DEFERRED (true) or INITIALLY IMMEDIATE (false/null).</summary>
    public bool? IsInitiallyDeferred { get; init; }

    /// <summary>
    /// Whether a CHECK or FOREIGN KEY constraint is enforced; false only for PostgreSQL 18's <c>NOT ENFORCED</c> (G-121). Defaults to true, the
    /// only value before PostgreSQL 18 and for every other constraint kind.
    /// </summary>
    public bool IsEnforced { get; init; } = true;

    /// <summary>
    /// Whether a CHECK or FOREIGN KEY constraint is <c>NOT VALID</c> (G-234): it is enforced for new rows, but the rows already there were not checked.
    /// In the database it is <c>convalidated = false</c>. Compared in one direction only: a project that states the constraint valid while the database has
    /// it not valid is a change (<c>VALIDATE CONSTRAINT</c>), and a project that states it <c>NOT VALID</c> while the database has validated it is not.
    /// </summary>
    public bool IsNotValid { get; init; }

    /// <summary>
    /// True when the project wrote no name and the constraint has the name PostgreSQL would give it (<see cref="PgProj.Parser.ConstraintNode.IsNameGenerated"/>), which
    /// follows its columns: a column rename changes it, and the live constraint keeps the old one (G-289).
    /// </summary>
    public bool IsNameGenerated { get; init; }

    /// <summary>
    /// Whether the last column of a PRIMARY KEY or UNIQUE constraint is <c>WITHOUT OVERLAPS</c> (PostgreSQL 18's temporal key; G-121). That
    /// column is in <see cref="PrimaryKeyColumns"/> or <see cref="UniqueColumns"/>, in its key position.
    /// </summary>
    public bool WithoutOverlaps { get; init; }

    /// <summary>True for <c>UNIQUE NULLS NOT DISTINCT</c> (G-262).</summary>
    public bool NullsNotDistinct { get; init; }

    /// <summary>
    /// Whether the last column of a FOREIGN KEY is <c>PERIOD</c> on both sides (PostgreSQL 18; G-121). That column is in
    /// <see cref="ForeignKeyColumns"/> and <see cref="ForeignKeyReferencedColumns"/>, in its key position.
    /// </summary>
    public bool ForeignKeyUsesPeriod { get; init; }

    /// <summary>Access method for an EXCLUDE constraint (e.g. "gist").</summary>
    public string? ExclusionAccessMethod { get; init; }

    /// <summary>Ordered exclusion elements ("column WITH operator") for an EXCLUDE constraint.</summary>
    public IReadOnlyList<ConstraintExclusionElementDefinition> ExclusionElements { get; init; } = [];

    /// <summary>Raw SQL text of an EXCLUDE constraint's WHERE predicate, if any.</summary>
    public string? RawExclusionPredicateExpression { get; init; }

    /// <summary>True for <c>CHECK (...) NO INHERIT</c> (G-194): a child of the table (<c>INHERITS</c>) does not get the check. Compared, and part of the definition.</summary>
    public bool IsNoInherit { get; init; }

    /// <summary>True when the project states <c>ALTER TABLE t CLUSTER ON</c> this key constraint's index (G-256).</summary>
    public bool IsClustered { get; init; }

    /// <summary>True when the project states <c>ALTER TABLE t REPLICA IDENTITY USING INDEX</c> this key constraint's index (G-255).</summary>
    public bool IsReplicaIdentity { get; init; }

    /// <summary>
    /// The key of the table a <c>LIKE</c> clause copied this constraint from (G-221); null for one the project declares itself. The table's <c>CREATE TABLE</c>
    /// makes it, so it is not created on its own with the table; once the table exists it is changed like any other. Not compared.
    /// </summary>
    public string? CopiedByLike { get; init; }

    /// <summary>
    /// True when the constraint is written in the element list of <c>CREATE TABLE ... PARTITION OF parent (...)</c> (G-266): the partition's <c>CREATE TABLE</c>, which deploys the list as
    /// written, makes it, so it is not created on its own with the table; once the table exists it is changed like any other. Not compared.
    /// </summary>
    public bool IsInPartitionOfList { get; init; }

    /// <summary>
    /// The <c>WITH (...)</c> storage parameters of a primary key's, unique constraint's, or exclusion constraint's index, each as written (G-195); compared
    /// as an index's are, and changed in place with <c>ALTER INDEX</c> on the constraint's index, which has the constraint's name.
    /// </summary>
    public IReadOnlyList<TableStorageOptionNode> IndexStorageOptions { get; init; } = [];

    /// <summary>The <c>INCLUDE (...)</c> columns of a primary key's, unique constraint's, or exclusion constraint's index, in order (G-200).</summary>
    public IReadOnlyList<string> IncludeColumns { get; init; } = [];

    public bool IsRecovered { get; set; }

    public bool HasErrors { get; set; }

    private readonly PgProj.Parser.PlaceSlot<SemanticLocation> _location = PgProj.Parser.PlaceSlot<SemanticLocation>.Empty;

    /// <summary>
    /// Where the definition was written: its file and range (G-309e), for the diagnostics about it; null for one read from a database. Never part of the
    /// definition's equality, never compared, and not in <c>--dump-model</c>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SemanticLocation? Location
    {
        get => _location.Value;
        init => _location = new PgProj.Parser.PlaceSlot<SemanticLocation>(value);
    }

    private readonly PgProj.Parser.PlaceSlot<SemanticLocation> _referencedTableLocation = PgProj.Parser.PlaceSlot<SemanticLocation>.Empty;

    /// <summary>Where a foreign key's referenced table is named (G-309e), for a diagnostic about a missing table; never compared.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SemanticLocation? ReferencedTableLocation
    {
        get => _referencedTableLocation.Value;
        init => _referencedTableLocation = new PgProj.Parser.PlaceSlot<SemanticLocation>(value);
    }

}
