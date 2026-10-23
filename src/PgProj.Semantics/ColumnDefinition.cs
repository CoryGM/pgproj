using PgProj.Parser;

namespace PgProj.Semantics;

public sealed record ColumnDefinition(
    string Key,
    string Name,
    string? SchemaName,
    string? TableName,
    string? DataType,
    string? RawType,
    bool? IsNullable,
    string? RawDefaultExpression,
    bool IsGenerated,
    string? GeneratedExpression,
    string? CollationName,
    IDictionary<string, string>? StorageParameters,
    IReadOnlyList<string> ReferencedTables,
    IReadOnlyList<string> ReferencedColumns,
    IReadOnlyList<string> ReferencedTypes,
    IReadOnlyList<string> ReferencedFunctions,
    IReadOnlyList<TableDefinition> ResolvedTables,
    IReadOnlyList<ColumnDefinition> ResolvedColumns,
    IReadOnlyList<TypeDefinition> ResolvedTypes,
    IReadOnlyList<FunctionDefinition> ResolvedFunctions,
    string? IdentityGeneration,
    bool IsSerial = false,
    string? RenamedFrom = null,
    string? Comment = null) : ISemanticNode
{
    /// <summary>The keys of the project operators (G-129) this object expressions use (G-138): it is created after them and dropped before them.</summary>
    public IReadOnlyList<string> ReferencedOperators { get; init; } = [];

    public string Kind => "Column";

    public ColumnNode? DeclarationNode { get; init; }

    /// <summary>True when this child gives a column it only inherits a default of its own (G-237): <c>ALTER TABLE ONLY child ALTER COLUMN c SET|DROP DEFAULT</c>.</summary>
    public bool OverridesInheritedDefault { get; init; }

    /// <summary>The parsed default expression, or, for a generated column (which has no default), its generated expression (G-71).</summary>
    public ExpressionNode? DefaultExpressionNode { get; init; }

    public string? NormalizedDefaultExpression { get; init; }

    /// <summary>
    /// A generated column's expression in the form the diff compares (a structural form built from the parsed expression, equal for
    /// spellings PostgreSQL treats as the same expression), kept beside <see cref="GeneratedExpression"/>, which stays as written (G-71).
    /// </summary>
    public string? NormalizedGeneratedExpression { get; init; }

    /// <summary>
    /// Whether a generated column is <c>VIRTUAL</c> (PostgreSQL 18): computed when it is read, and taking no space, rather than
    /// <c>STORED</c> (G-121). False for a stored column and for one that is not generated. A change between the two is a drop and add of
    /// the column, since neither can become the other in place.
    /// </summary>
    public bool IsVirtual { get; init; }

    /// <summary>
    /// True when the column is generated only because the table inherits it from a parent and lists the column without a generation expression of its own (<c>CREATE TABLE child (a int, b int)
    /// INHERITS (parent)</c>, as <c>pg_dump</c> writes it) (G-299). The expression is the parent's, kept so the column compares equal to the database's; it is not written for the child, which
    /// PostgreSQL 14 and 15 would refuse (<c>42611</c>).
    /// </summary>
    public bool GenerationIsInherited { get; init; }

    /// <summary>
    /// The key of the parent table this column comes from when the table inherits it (<c>INHERITS</c>) and does not declare it itself (G-194); null for a
    /// column of the table's own. Such a column is not written in the table's <c>CREATE TABLE</c>, and a change to it is made on the parent, which
    /// PostgreSQL carries to the child. Not compared.
    /// </summary>
    public string? InheritedFromTable { get; init; }

    /// <summary>
    /// The column's <c>STORAGE</c> (<c>plain</c>, <c>external</c>, <c>extended</c>, <c>main</c>, or <c>default</c>), lower case (G-222). A project's is what it states,
    /// null when it states none; a database's is what it has. Compared only when the project states one other than <c>default</c>, whose meaning (the type's
    /// storage) pgproj does not know; changed in place with <c>SET STORAGE</c>.
    /// </summary>
    public string? Storage { get; init; }

    /// <summary>
    /// True when the storage was set by <c>ALTER TABLE ... ALTER COLUMN ... SET STORAGE</c> rather than written in the column's definition (G-269): it is deployed that way,
    /// which every supported version accepts, while <c>STORAGE</c> inside <c>CREATE TABLE</c> needs PostgreSQL 16.
    /// </summary>
    public bool IsStorageSetByAlter { get; init; }

    /// <summary>
    /// The column's <c>COMPRESSION</c> (<c>pglz</c>, <c>lz4</c>, or <c>default</c>), lower case (G-222); a database's is null when it has none of its own. Compared only
    /// when the project states one (<c>default</c> is none of its own); changed in place with <c>SET COMPRESSION</c>.
    /// </summary>
    public string? Compression { get; init; }

    /// <summary>Whether the project's stated <c>STORAGE</c> differs from the database's (G-222).</summary>
    public static bool IsStorageChange(ColumnDefinition oldColumn, ColumnDefinition newColumn)
    {
        ArgumentNullException.ThrowIfNull(oldColumn);
        ArgumentNullException.ThrowIfNull(newColumn);
        return newColumn.Storage is { } stated && !string.Equals(stated, "default", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stated, oldColumn.Storage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the project's stated <c>COMPRESSION</c> differs from the database's, <c>default</c> being none of its own (G-222).</summary>
    public static bool IsCompressionChange(ColumnDefinition oldColumn, ColumnDefinition newColumn)
    {
        ArgumentNullException.ThrowIfNull(oldColumn);
        ArgumentNullException.ThrowIfNull(newColumn);
        static string? OwnOf(string? value) => string.Equals(value, "default", StringComparison.OrdinalIgnoreCase) ? null : value;
        return newColumn.Compression is not null && !string.Equals(OwnOf(newColumn.Compression), OwnOf(oldColumn.Compression), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The key of the table a <c>LIKE</c> clause copied this column from (G-221); null for one the project declares itself. The table's <c>CREATE TABLE</c>
    /// makes it, so it is not created on its own with the table; once the table exists it is changed like any other. Not compared.
    /// </summary>
    public string? CopiedByLike { get; init; }

    /// <summary>
    /// Whether the column's <c>NOT NULL</c> is <c>NOT VALID</c> (PostgreSQL 18; G-121): enforced for new rows, the existing ones not checked,
    /// until it is validated. False for an ordinary NOT NULL and for a nullable column.
    /// </summary>
    public bool NotNullIsNotValid { get; init; }

    /// <summary>
    /// The name of a <c>NOT VALID</c> NOT NULL constraint, which <c>VALIDATE CONSTRAINT</c> needs: the live database's, or the one the project
    /// gave (null when it gave none). Never compared, since an unnamed one is named by the server; null for every other column (G-126).
    /// </summary>
    public string? NotNullConstraintName { get; init; }

    /// <summary>
    /// The name of the column's <c>NOT NULL</c> constraint (PostgreSQL 18 stores one; G-235): the name the project gave it, or the live database's own,
    /// which is <c>&lt;table&gt;_&lt;column&gt;_not_null</c> unless it was named. A column that inherits its NOT NULL has its parent's. Null when the project gave none, and
    /// for every live column on an older server. Compared only when the project states one and the database has one: a different name is a rename in
    /// place.
    /// </summary>
    public string? NotNullName { get; init; }

    /// <summary>True for <c>NOT NULL NO INHERIT</c> (PostgreSQL 18; G-253): a child does not get the constraint. Compared as written.</summary>
    public bool NotNullNoInherit { get; init; }

    /// <summary>True when a child has its own NOT NULL constraint, with its own name, on a column it only inherits: <c>CONSTRAINT foo NOT NULL a</c> among the child's elements (PostgreSQL 18; G-253).</summary>
    public bool OwnsInheritedNotNull { get; init; }

    /// <summary>True when a child declares the column without NOT NULL and gets it from a parent (G-253): the child's declaration does not repeat it.</summary>
    public bool NotNullIsInherited { get; init; }

    /// <summary>The comment on the column's <c>NOT NULL</c> constraint, <c>COMMENT ON CONSTRAINT name ON table</c> (G-235). Compared only when stated (<see cref="NotNullCommentStated"/>).</summary>
    public string? NotNullComment { get; init; }

    /// <summary>True when the project has a <c>COMMENT ON</c> statement for the column's NOT NULL constraint (<c>IS NULL</c> states none), as <see cref="ConstraintDefinition.CommentStated"/> does for a constraint.</summary>
    public bool NotNullCommentStated { get; init; }

    /// <summary>
    /// Type modifiers as declared, for example <c>10</c>, <c>2</c> for <c>numeric(10,2)</c> (an <c>interval</c>'s
    /// precision only; see <see cref="IntervalFields"/>). Deployed as written; <see cref="NormalizedTypeModifiers"/>
    /// is the form used for comparison (G-45).
    /// </summary>
    public IReadOnlyList<string> TypeModifiers { get; init; } = [];

    /// <summary>
    /// The column's type exactly as the project (or PostgreSQL's <c>format_type</c>) wrote it, which is what is deployed (G-207); null when
    /// it was not read (a serial column, a column made from a composite type's field). Never compared: <see cref="DataType"/> and its
    /// normalized forms are.
    /// </summary>
    public string? WrittenType { get; init; }

    /// <summary>The text inside an identity's parentheses as written (<c>START WITH 10 INCREMENT BY 5</c>), which is what is deployed (G-214); null when none.</summary>
    public string? IdentityOptionsText { get; init; }

    /// <summary>Canonical <see cref="TypeModifiers"/> for comparison (for example <c>numeric(5)</c> becomes <c>5</c>, <c>0</c>).</summary>
    public IReadOnlyList<string> NormalizedTypeModifiers { get; init; } = [];

    /// <summary>Declared array dimensions (0 for a scalar). PostgreSQL does not distinguish dimension counts, only array or not.</summary>
    public int ArrayDimensions { get; init; }

    /// <summary>The field restriction of an <c>interval</c> column (for example <c>day to second</c>), or null.</summary>
    public string? IntervalFields { get; init; }

    /// <summary>
    /// A canonical text of the full column type (base type, modifiers, interval fields, array-ness) that is equal
    /// for two spellings of the same type and different for a genuine change such as <c>varchar(10)</c> to
    /// <c>varchar(20)</c>. Used to decide whether a column's type must be altered.
    /// </summary>
    public string GetComparableTypeSignature()
        => ColumnTypeNormalizer.BuildComparableTypeSignature(DataType, TypeModifiers, ArrayDimensions, IntervalFields);

    /// <summary>
    /// The comparison form of the column's <c>DEFAULT</c> (null when it has none or is generated). Equal for two
    /// spellings of the same default (whitespace, keyword case, redundant parentheses, PostgreSQL's own
    /// <c>'x'::text</c> rendering) and different for a genuine change; <see cref="RawDefaultExpression"/> is what is
    /// deployed (G-56).
    /// </summary>
    public string? GetComparableDefaultExpression()
        => IsGenerated ? null : DefaultExpressionCanonicalizer.Canonicalize(RawDefaultExpression, DataType, ArrayDimensions);

    /// <summary>
    /// The default as PostgreSQL itself renders it (<c>pg_get_expr(adbin, adrelid)</c>), or null when not available
    /// (G-102). A live column carries its own rendering; a project column carries the rendering of its default on a
    /// temporary table of the live column's type, made during <c>diff</c>/<c>publish</c>. PostgreSQL rewrites a default
    /// in ways no text rule keeps up with (<c>0::numeric</c> becomes <c>(0)::numeric</c>, <c>1.5::numeric</c> becomes
    /// <c>1.5</c>, <c>'true'::boolean</c> becomes <c>true</c>, a <c>timestamptz</c> literal gains its offset).
    /// </summary>
    public string? ServerRenderedDefault { get; init; }

    /// <summary>
    /// A generated column's expression as PostgreSQL itself renders it (<c>pg_get_expr(adbin, adrelid)</c>), or null when
    /// not available (G-97). A live column carries its own rendering; a project column carries the rendering of its
    /// expression on a temporary copy of the live table, made during <c>diff</c>/<c>publish</c>. PostgreSQL stores the
    /// implicit casts it inserts (<c>qty * price</c> over <c>integer</c> and <c>numeric</c> is <c>((qty)::numeric * price)</c>),
    /// which the parsed comparison cannot know.
    /// </summary>
    public string? ServerRenderedGeneratedExpression { get; init; }

    /// <summary>
    /// The two generated expressions to compare (G-97): PostgreSQL's renderings when both columns have one, otherwise
    /// <see cref="NormalizedGeneratedExpression"/> of each.
    /// </summary>
    public static (string? Old, string? New) GetComparableGeneratedExpressions(ColumnDefinition oldColumn, ColumnDefinition newColumn)
    {
        ArgumentNullException.ThrowIfNull(oldColumn);
        ArgumentNullException.ThrowIfNull(newColumn);

        return oldColumn.ServerRenderedGeneratedExpression is not null && newColumn.ServerRenderedGeneratedExpression is not null
            ? (oldColumn.ServerRenderedGeneratedExpression, newColumn.ServerRenderedGeneratedExpression)
            : (oldColumn.NormalizedGeneratedExpression, newColumn.NormalizedGeneratedExpression);
    }

    /// <summary>
    /// The two defaults to compare (G-102): PostgreSQL's renderings when both columns have one, otherwise
    /// <see cref="GetComparableDefaultExpression"/> of each.
    /// </summary>
    public static (string? Old, string? New) GetComparableDefaultExpressions(ColumnDefinition oldColumn, ColumnDefinition newColumn)
    {
        ArgumentNullException.ThrowIfNull(oldColumn);
        ArgumentNullException.ThrowIfNull(newColumn);

        return !oldColumn.IsGenerated && !newColumn.IsGenerated
            && oldColumn.ServerRenderedDefault is not null && newColumn.ServerRenderedDefault is not null
            ? (oldColumn.ServerRenderedDefault, newColumn.ServerRenderedDefault)
            : (oldColumn.GetComparableDefaultExpression(), newColumn.GetComparableDefaultExpression());
    }

    /// <summary>
    /// Whether the column accepts null. <see cref="IsNullable"/> is three-valued (an explicit <c>NULL</c>, an explicit
    /// <c>NOT NULL</c>, or unspecified), but PostgreSQL has two states: a column allows null unless it is <c>NOT NULL</c>. A
    /// column written <c>NULL</c> and one read back from the catalog without <c>NOT NULL</c> are the same column, and comparing
    /// <see cref="IsNullable"/> reported a change for every explicit <c>NULL</c> after publish (G-62). Used to decide whether
    /// two columns differ; <see cref="IsNullable"/> keeps what was written.
    /// </summary>
    public bool AllowsNull => IsNullable != false;

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

    private readonly PgProj.Parser.PlaceSlot<SemanticLocation> _typeLocation = PgProj.Parser.PlaceSlot<SemanticLocation>.Empty;

    /// <summary>Where the column's type is named (G-309e), for a diagnostic about a missing type; never compared.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SemanticLocation? TypeLocation
    {
        get => _typeLocation.Value;
        init => _typeLocation = new PgProj.Parser.PlaceSlot<SemanticLocation>(value);
    }

}
