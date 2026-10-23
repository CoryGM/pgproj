namespace PgProj.Parser;

public sealed record ColumnNode(
    string? Name,
    string? TableName,
    string? SchemaName,
    string? DataType,
    string? RawType,
    bool? IsNullable,
    string? RawDefaultExpression,
    bool IsGenerated,
    string? GeneratedExpression,
    string? CollationName,
    IDictionary<string, string>? StorageParameters,
    string? IdentityGeneration,
    bool IsSerial = false,
    string? RenamedFrom = null,
    string? Comment = null) : SqlNode("Column", Name, SchemaName)
{
    /// <summary>
    /// Type modifiers exactly as declared, such as <c>10</c> and <c>2</c> for <c>numeric(10,2)</c> or <c>20</c> for
    /// <c>varchar(20)</c>. For an <c>interval</c> this is only the precision; the field restriction is
    /// <see cref="IntervalFields"/>.
    /// </summary>
    /// <summary>
    /// The column's type exactly as written (<c>VARCHAR(10)</c>), which is what is deployed (G-207); null for a serial column (pgproj writes
    /// its underlying type and sequence) or when the text could not be read. <see cref="DataType"/> is the normalized spelling.
    /// </summary>
    public string? WrittenType { get; init; }

    /// <summary>An identity's sequence options (<c>start</c>, <c>increment</c>, ...), as <c>CREATE SEQUENCE</c>'s are read; null when it states none (G-214).</summary>
    public IReadOnlyDictionary<string, string>? IdentityOptions { get; init; }

    /// <summary>The text inside an identity's parentheses as written, which is what is deployed (G-214); null when it has none.</summary>
    public string? IdentityOptionsText { get; init; }

    /// <summary>The column's <c>STORAGE</c> as written (<c>main</c>, <c>default</c>, ...), lower case; null when none is (G-222).</summary>
    public string? Storage { get; init; }

    /// <summary>
    /// True when the storage was set by <c>ALTER TABLE ... ALTER COLUMN ... SET STORAGE</c> rather than written in the column's definition (G-269): it is deployed that way,
    /// which every supported version accepts, while <c>STORAGE</c> inside <c>CREATE TABLE</c> needs PostgreSQL 16.
    /// </summary>
    public bool IsStorageSetByAlter { get; init; }

    /// <summary>The column's <c>COMPRESSION</c> as written (<c>lz4</c>, <c>pglz</c>, <c>default</c>), lower case; null when none is (G-222).</summary>
    public string? Compression { get; init; }

    public IReadOnlyList<string> TypeModifiers { get; init; } = [];

    /// <summary>Number of array dimensions declared (<c>text[]</c> is 1, <c>integer[][]</c> is 2); 0 for a scalar.</summary>
    public int ArrayDimensions { get; init; }

    /// <summary>The field restriction of an <c>interval</c> column (for example <c>day to second</c>), or null.</summary>
    public string? IntervalFields { get; init; }

    /// <summary>
    /// The pg_query JSON of a generated column's expression, which the semantic layer turns into a form that is equal for spellings
    /// PostgreSQL treats as the same expression (G-71); null for a column that is not generated.
    /// </summary>
    public string? GeneratedExpressionAst { get; init; }

    /// <summary>
    /// Whether a generated column is <c>VIRTUAL</c> (PostgreSQL 18), computed when it is read, rather than <c>STORED</c>; false for a stored
    /// column and for one that is not generated (G-121). On PostgreSQL 18 a generated column that says neither is virtual.
    /// </summary>
    public bool IsVirtual { get; init; }

    /// <summary>
    /// Whether the column's <c>NOT NULL</c> was added <c>NOT VALID</c> (PostgreSQL 18; G-121): the constraint is enforced for new rows, the
    /// existing ones not checked, until <c>VALIDATE CONSTRAINT</c>. False for an ordinary NOT NULL and for a nullable column.
    /// </summary>
    public bool NotNullIsNotValid { get; init; }

    /// <summary>
    /// The name of a <c>NOT VALID</c> NOT NULL constraint, when the SQL gave one: <c>VALIDATE CONSTRAINT</c> needs it. Kept only for a
    /// NOT VALID one; the name of an ordinary NOT NULL is never kept (it does not exist before PostgreSQL 18; G-126).
    /// </summary>
    public string? NotNullConstraintName { get; init; }

    /// <summary>
    /// The name the SQL gave the column's <c>NOT NULL</c> (<c>a int CONSTRAINT nn NOT NULL</c>, <c>CONSTRAINT nn NOT NULL a</c>,
    /// <c>ADD CONSTRAINT nn NOT NULL a</c>), or null when it gave none (G-235). PostgreSQL 18 stores it; an older server accepts and forgets it.
    /// </summary>
    public string? NotNullName { get; init; }

    /// <summary>True for <c>NOT NULL NO INHERIT</c> (PostgreSQL 18; G-253): the constraint is the column's own and a child does not get it.</summary>
    public bool NotNullNoInherit { get; init; }

    /// <summary>
    /// The pg_query JSON of the column's <c>DEFAULT</c> expression, so the semantic layer can read what it refers to from the tree
    /// rather than from the text (G-102: the text parser has no cast, so <c>'{}'::text[]</c> was "unparsable"); null when the
    /// column has no written default.
    /// </summary>
    public string? DefaultExpressionAst { get; init; }

    /// <summary>
    /// The key of the table a <c>LIKE</c> clause copied this column from (G-221); null for one the project declares itself. The table's <c>CREATE TABLE</c>
    /// makes it, so it is not created on its own with the table; once the table exists it is changed like any other. Not compared.
    /// </summary>
    public string? CopiedByLike { get; init; }

    private readonly PlaceSlot<SourceRange> _typeRange = PlaceSlot<SourceRange>.Empty;

    /// <summary>Where the column's type is named (G-309e); never part of the node's equality.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SourceRange? TypeRange
    {
        get => _typeRange.Value;
        init => _typeRange = new PlaceSlot<SourceRange>(value);
    }
}
