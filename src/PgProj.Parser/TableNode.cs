namespace PgProj.Parser;

public sealed record TableNode(
    string? Name,
    string? SchemaName,
    IReadOnlyList<ColumnNode> Columns,
    string? Comment = null) : SqlNode("Table", Name, SchemaName)
{
    /// <summary>True for <c>CREATE UNLOGGED TABLE</c> (G-24b).</summary>
    public bool IsUnlogged { get; init; }

    /// <summary>The table's <c>TABLESPACE</c> name, or null when none was written (the database default applies).</summary>
    public string? TablespaceName { get; init; }

    /// <summary>The table's <c>USING</c> access method, as written; null when none is (G-223).</summary>
    public string? AccessMethod { get; init; }

    /// <summary>The table's <c>WITH (...)</c> storage parameters, in the order written.</summary>
    public IReadOnlyList<TableStorageOptionNode> StorageOptions { get; init; } = [];

    /// <summary>The strategy of <c>PARTITION BY</c> (<c>"RANGE"</c>, <c>"LIST"</c>, or <c>"HASH"</c>), or null when this table is not partitioned (G-24c).</summary>
    public string? PartitionStrategy { get; init; }

    /// <summary>The <c>PARTITION BY</c> key, in the order written. Empty when <see cref="PartitionStrategy"/> is null.</summary>
    public IReadOnlyList<PartitionKeyElementNode> PartitionKeyElements { get; init; } = [];

    /// <summary>The parent's schema, when this table is a partition (<c>CREATE TABLE ... PARTITION OF parent ...</c>).</summary>
    public string? PartitionOfSchemaName { get; init; }

    /// <summary>True when <see cref="PartitionOfSchemaName"/> was not written but defaulted (G-93); see <see cref="SqlNode.IsSchemaDefaulted"/>.</summary>
    public bool IsPartitionOfSchemaDefaulted { get; init; }

    /// <summary>The parent's name, when this table is a partition. Null for an ordinary table or a partitioned parent.</summary>
    public string? PartitionOfTableName { get; init; }

    /// <summary>
    /// The <c>FOR VALUES ...</c> or <c>DEFAULT</c> clause, exactly as written, when this table is a partition. Null unless
    /// <see cref="PartitionOfTableName"/> is set.
    /// </summary>
    public string? PartitionBoundRaw { get; init; }

    /// <summary>The replica identity the project states with <c>ALTER TABLE t REPLICA IDENTITY</c>: <c>DEFAULT</c>, <c>FULL</c>, <c>NOTHING</c>, or <c>USING INDEX</c> (G-255); null when it states none.</summary>
    public string? ReplicaIdentity { get; init; }

    /// <summary>True when the project says <c>ALTER TABLE t SET WITHOUT CLUSTER</c> and no index of the table is <c>CLUSTER ON</c> (G-260).</summary>
    public bool WithoutCluster { get; init; }

    /// <summary>The firing mode the project states for the table's internal (foreign-key) triggers with <c>ALTER TABLE t { ENABLE | DISABLE } TRIGGER ALL</c> (G-261): <c>ENABLE</c> or <c>DISABLE</c>; null when it states none.</summary>
    public string? InternalTriggerState { get; init; }

    /// <summary>
    /// The defaults this table gives columns it only inherits, from <c>ALTER TABLE ONLY child ALTER COLUMN c SET|DROP DEFAULT</c> (G-237). A child lists only its own columns, so the applier cannot set the
    /// default on a column node; the semantic layer applies it once the child has its parents' columns.
    /// </summary>
    public IReadOnlyList<InheritedColumnDefault> InheritedColumnDefaults { get; init; } = [];

    /// <summary>
    /// The NOT NULL constraints this table gives columns it only inherits, from <c>CONSTRAINT foo NOT NULL a</c> among the child's elements (PostgreSQL 18; G-253): the child's own constraint,
    /// with its own name. Applied by the semantic layer once the child has its parents' columns.
    /// </summary>
    public IReadOnlyList<InheritedColumnNotNull> InheritedColumnNotNulls { get; init; } = [];

    /// <summary>
    /// The element list of <c>CREATE TABLE child PARTITION OF parent (...) FOR VALUES ...</c> as written, parentheses included (G-266): column options
    /// (<c>col WITH OPTIONS DEFAULT x</c>, <c>col CONSTRAINT n NOT NULL</c>) and table constraints. Deployed as written; its defaults and NOT NULL constraints are also recorded
    /// in <see cref="InheritedColumnDefaults"/> and <see cref="InheritedColumnNotNulls"/>, and its other constraints are constraint nodes, for the model. Null when there is none.
    /// </summary>
    public string? PartitionElementsText { get; init; }

    /// <summary>True once <c>ALTER TABLE ... ENABLE ROW LEVEL SECURITY</c> has been applied (G-76). PostgreSQL has no way to set this from <c>CREATE TABLE</c> itself; it is always false until a matching <c>ALTER TABLE</c> statement exists, folded on by <see cref="AlterTableColumnApplier"/>.</summary>
    public bool IsRowSecurityEnabled { get; init; }

    /// <summary>True once <c>ALTER TABLE ... FORCE ROW LEVEL SECURITY</c> has been applied (G-76); meaningless unless <see cref="IsRowSecurityEnabled"/> is also true.</summary>
    public bool IsRowSecurityForced { get; init; }

    /// <summary>The schema of the composite type this table is of (<c>CREATE TABLE t OF s.type</c>, a typed table, G-136); null when the table is not typed.</summary>
    public string? OfTypeSchemaName { get; init; }

    /// <summary>True when <see cref="OfTypeSchemaName"/> was not written but defaulted (G-93).</summary>
    public bool IsOfTypeSchemaDefaulted { get; init; }

    /// <summary>The composite type this table is of; null for an ordinary table. A typed table's columns are the type's fields, each optionally given a default or NOT NULL with <c>WITH OPTIONS</c>.</summary>
    public string? OfTypeName { get; init; }

    /// <summary>
    /// The tables of <c>CREATE TABLE ... INHERITS (...)</c>, in the order written (G-194); empty for a table that inherits from none. A partition's parent is
    /// <see cref="PartitionOfTableName"/>, not this.
    /// </summary>
    public IReadOnlyList<TableParentNode> InheritsFrom { get; init; } = [];

    /// <summary>The <c>LIKE source [INCLUDING ...]</c> clauses of the column list, in the order written (G-221); empty for none.</summary>
    public IReadOnlyList<TableLikeNode> LikeClauses { get; init; } = [];
}

/// <summary>
/// A <c>LIKE source [INCLUDING | EXCLUDING ...]</c> clause of <c>CREATE TABLE</c> (G-221): the relation whose columns are copied, what is copied with them
/// (<see cref="Options"/>, PostgreSQL's <c>CREATE_TABLE_LIKE_*</c> bits), the clause as written, which is what is deployed, and how many of the table's own
/// columns are written before it, which is where the copied columns go.
/// </summary>
public sealed record TableLikeNode(string? SchemaName, string Name, int Options, string RawText, int OwnColumnsBefore)
{
    public const int Comments = 1 << 0;
    public const int Compression = 1 << 1;
    public const int Constraints = 1 << 2;
    public const int Defaults = 1 << 3;
    public const int Generated = 1 << 4;
    public const int Identity = 1 << 5;
    public const int Indexes = 1 << 6;
    public const int Statistics = 1 << 7;
    public const int Storage = 1 << 8;

    /// <summary>The value of <see cref="SourceKind"/> for a composite type.</summary>
    public const string TypeSource = "type";

    /// <summary>True when <see cref="SchemaName"/> was not written but defaulted (G-93); the search path then decides it.</summary>
    public bool IsSchemaDefaulted { get; init; }

    /// <summary>What the source turned out to be once the project was read: <see cref="TypeSource"/> for a composite type, null for a table or one not found.</summary>
    public string? SourceKind { get; init; }

    /// <summary>Whether the clause copies <paramref name="option"/> (one of the constants of this record).</summary>
    public bool Includes(int option) => (Options & option) != 0;
}

/// <summary>A table named in <c>INHERITS (...)</c> (G-194).</summary>
public sealed record TableParentNode(string? SchemaName, string Name)
{
    /// <summary>True when <see cref="SchemaName"/> was not written but defaulted (G-93); the search path then decides it.</summary>
    public bool IsSchemaDefaulted { get; init; }
}

/// <summary>A child table's own default (or, with a null <see cref="RawExpression"/>, <c>DROP DEFAULT</c>) for a column it inherits (G-237).</summary>
public sealed record InheritedColumnDefault(string ColumnName, string? RawExpression, string? ExpressionAst)
{
    /// <summary>True when the default is written in the element list of <c>PARTITION OF parent (...)</c>, which deploys it (G-266); false for an <c>ALTER TABLE</c>.</summary>
    public bool IsInPartitionOfList { get; init; }
}

/// <summary>A child table's own NOT NULL constraint (its name, and whether it is <c>NO INHERIT</c>) for a column it inherits (G-253).</summary>
public sealed record InheritedColumnNotNull(string ColumnName, string? Name, bool NoInherit)
{
    /// <summary>
    /// True for <c>CONSTRAINT n NOT NULL col</c> written as a table constraint in the element list of <c>PARTITION OF parent (...)</c> (G-266): deployed as written, a form
    /// only PostgreSQL 18 accepts.
    /// </summary>
    public bool IsTableConstraintInPartitionOfList { get; init; }
}
