namespace PgProj.Parser;

/// <summary>
/// A column added by <c>ALTER TABLE ... ADD COLUMN</c>. It is a transient marker: <see cref="AlterTableColumnApplier"/> moves the
/// column into its table's <see cref="TableNode.Columns"/> before the semantic passes run, so the model sees it as a column of the
/// table (G-47).
/// </summary>
public sealed record AlterTableAddColumnNode(
    string? TableName,
    string? SchemaName,
    ColumnNode Column,
    bool IfNotExists) : SqlNode("AlterTableAddColumn", TableName, SchemaName);

/// <summary>
/// An <c>ALTER TABLE</c> action the model does not represent (<c>DROP COLUMN</c>, <c>ALTER COLUMN ... TYPE</c>, <c>OWNER TO</c>, and
/// so on). <see cref="AlterTableColumnApplier"/> turns it into a warning so it is reported instead of silently ignored (G-47).
/// </summary>
public sealed record UnsupportedAlterTableNode(
    string? TableName,
    string? SchemaName,
    string Action) : SqlNode("UnsupportedAlterTable", TableName, SchemaName);

/// <summary>
/// <c>ALTER TABLE ... ENABLE/DISABLE ROW LEVEL SECURITY</c> and <c>FORCE/NO FORCE ROW LEVEL SECURITY</c> (G-76). A transient
/// marker, like <see cref="AlterTableAddColumnNode"/>: <see cref="AlterTableColumnApplier"/> folds it onto its table's
/// <see cref="TableNode.IsRowSecurityEnabled"/>/<see cref="TableNode.IsRowSecurityForced"/> before the semantic passes run, so
/// the model sees a table whose row security was ever toggled exactly as if <c>CREATE TABLE</c> itself had set it (which
/// PostgreSQL has no syntax for: row security can only be turned on via a separate <c>ALTER TABLE</c>). Null on either flag
/// means "this statement did not toggle that one", so folding two separate statements (one for ENABLE, one for FORCE) does
/// not clobber the other's state.
/// </summary>
public sealed record AlterTableRowSecurityNode(
    string? TableName,
    string? SchemaName,
    bool? EnableRowSecurity,
    bool? ForceRowSecurity) : SqlNode("AlterTableRowSecurity", TableName, SchemaName);

/// <summary>
/// A <c>NOT NULL</c> written as a table constraint that names its column: <c>CONSTRAINT nn NOT NULL a</c> in <c>CREATE TABLE</c>, or
/// <c>ALTER TABLE ... ADD CONSTRAINT nn NOT NULL a</c> (PostgreSQL 18; G-126). A transient marker, like
/// <see cref="AlterTableAddColumnNode"/>: <see cref="AlterTableColumnApplier"/> makes its column not nullable before the semantic passes
/// run. A NOT NULL is never a constraint of the model, so it has no <see cref="ConstraintNode"/>, and its name is not kept: it does not
/// exist before PostgreSQL 18 and would make phantom diffs.
/// </summary>
public sealed record NotNullConstraintNode(
    string? TableName,
    string? SchemaName,
    string? ColumnName,
    string? ConstraintName) : SqlNode("NotNullConstraint", TableName, SchemaName)
{
    /// <summary>
    /// Whether it was written <c>NOT VALID</c> (PostgreSQL 18; G-121): enforced for new rows, the existing ones not checked. Only
    /// <c>ALTER TABLE ... ADD CONSTRAINT</c> accepts it.
    /// </summary>
    public bool IsNotValid { get; init; }

    /// <summary>True when it is written in the element list of <c>CREATE TABLE ... PARTITION OF parent (...)</c> (G-266), which deploys it as written.</summary>
    public bool IsInPartitionOfList { get; init; }

    /// <summary>Whether it was written <c>NO INHERIT</c> (PostgreSQL 18; G-253).</summary>
    public bool NoInherit { get; init; }
}

/// <summary>
/// <c>ALTER TABLE ... ALTER COLUMN c SET DEFAULT expr</c> or <c>DROP DEFAULT</c> (G-236), the way <c>pg_dump</c> writes a serial column's default. A transient marker, like
/// <see cref="AlterTableAddColumnNode"/>: <see cref="AlterTableColumnApplier"/> sets (or clears) the column's default before the semantic passes run. <see cref="RawExpression"/>
/// is null for <c>DROP DEFAULT</c>.
/// </summary>
public sealed record AlterTableColumnDefaultNode(
    string? TableName,
    string? SchemaName,
    string ColumnName,
    string? RawExpression,
    string? ExpressionAst) : SqlNode("AlterTableColumnDefault", TableName, SchemaName);

/// <summary>
/// <c>ALTER TABLE ... ALTER COLUMN c ADD GENERATED { ALWAYS | BY DEFAULT } AS IDENTITY [(options)]</c> (G-236), the way <c>pg_dump</c> writes every identity column. A transient marker:
/// the applier makes the column an identity, as if <c>CREATE TABLE</c> had said so.
/// </summary>
public sealed record AlterTableAddIdentityNode(
    string? TableName,
    string? SchemaName,
    string ColumnName,
    string? Generation,
    IReadOnlyDictionary<string, string>? Options,
    string? OptionsText) : SqlNode("AlterTableAddIdentity", TableName, SchemaName);

/// <summary>
/// <c>ALTER TABLE ... ALTER COLUMN c SET STORAGE s</c> or <c>SET COMPRESSION m</c> (G-236). A transient marker: the applier sets the column's storage or compression.
/// </summary>
public sealed record AlterTableColumnStorageNode(
    string? TableName,
    string? SchemaName,
    string ColumnName,
    string? Storage,
    string? Compression) : SqlNode("AlterTableColumnStorage", TableName, SchemaName);

/// <summary>
/// <c>ALTER TABLE [ONLY] parent ATTACH PARTITION child FOR VALUES ... | DEFAULT</c> (G-236), the way <c>pg_dump</c> writes every partition. A transient marker: the applier makes the
/// child a partition of the parent, with the bound, before the semantic passes run. <see cref="TableName"/> is the parent.
/// </summary>
public sealed record AlterTableAttachPartitionNode(
    string? TableName,
    string? SchemaName,
    string ChildName,
    string? ChildSchemaName,
    string? BoundRaw) : SqlNode("AlterTableAttachPartition", TableName, SchemaName)
{
    /// <summary>True when <see cref="ChildSchemaName"/> was not written but given by <see cref="DefaultSchema"/>.</summary>
    public bool IsChildSchemaDefaulted { get; init; }
}

/// <summary>
/// <c>ALTER SEQUENCE s OWNED BY table.column</c> (G-236), the way <c>pg_dump</c> hands every serial's sequence to its column. A transient marker: the applier sets the sequence's
/// <c>owned_by</c> option, as if <c>CREATE SEQUENCE</c> had said so.
/// </summary>
public sealed record AlterSequenceOwnedByNode(
    string? Name,
    string? SchemaName,
    string OwnedBy) : SqlNode("AlterSequenceOwnedBy", Name, SchemaName);

/// <summary>
/// <c>ALTER INDEX parent_idx ATTACH PARTITION child_idx</c> (G-236), the way <c>pg_dump</c> ties a partition's own index (or primary key or unique constraint) to the parent's. A
/// transient marker: PostgreSQL makes a partition's index from the parent's, so the applier drops the partition's separately written one from the model, and the parent's
/// index (written <c>ON ONLY</c> by <c>pg_dump</c>) is created on every partition. <see cref="IndexName"/> is the parent's index.
/// </summary>
public sealed record AlterIndexAttachPartitionNode(
    string? IndexName,
    string? SchemaName,
    string ChildIndexName,
    string? ChildSchemaName) : SqlNode("AlterIndexAttachPartition", IndexName, SchemaName)
{
    /// <summary>True when <see cref="ChildSchemaName"/> was not written but given by <see cref="DefaultSchema"/>.</summary>
    public bool IsChildSchemaDefaulted { get; init; }
}

/// <summary>
/// <c>ALTER TABLE t { ENABLE | DISABLE | ENABLE REPLICA | ENABLE ALWAYS } { TRIGGER | RULE } name</c> (G-257). A transient marker: the applier sets the trigger's or rule's
/// <c>EnabledState</c> before the semantic passes run. <see cref="State"/> is <c>ENABLE</c>, <c>DISABLE</c>, <c>ENABLE REPLICA</c>, or <c>ENABLE ALWAYS</c>.
/// </summary>
public sealed record AlterTableObjectStateNode(
    string? TableName,
    string? SchemaName,
    string ObjectKind,
    string ObjectName,
    string State) : SqlNode("AlterTableObjectState", TableName, SchemaName)
{
    /// <summary>
    /// The <see cref="ObjectKind"/> of <c>ALTER TABLE t { ENABLE | DISABLE } TRIGGER USER</c> (G-259), which names no trigger: it sets the state of every trigger the project declares on the table, and a
    /// statement that names a trigger overrides it, wherever each is written.
    /// </summary>
    public const string UserTriggersKind = "user-triggers";

    /// <summary>
    /// The <see cref="ObjectKind"/> of <c>ALTER TABLE t { ENABLE | DISABLE } TRIGGER ALL</c> (G-261): the declared triggers take the state as with <c>USER</c>, and the table's internal
    /// (foreign-key) triggers, which the project does not declare, are a setting of the table (<c>TableNode.InternalTriggerState</c>).
    /// </summary>
    public const string AllTriggersKind = "all-triggers";
}

/// <summary>
/// <c>ALTER TABLE t CLUSTER ON index</c> (G-256). A transient marker: the applier marks the index (or primary key or unique constraint) of that name on the table as the one the table is
/// clustered on, before the semantic passes run. A null <see cref="IndexName"/> is <c>SET WITHOUT CLUSTER</c> (G-260): the table is stated to be clustered on no index.
/// </summary>
public sealed record AlterTableClusterOnNode(
    string? TableName,
    string? SchemaName,
    string? IndexName) : SqlNode("AlterTableClusterOn", TableName, SchemaName);

/// <summary>
/// <c>ALTER TABLE t REPLICA IDENTITY { DEFAULT | FULL | NOTHING | USING INDEX index }</c> (G-255). A transient marker: the applier sets the table's replica identity and, for
/// <c>USING INDEX</c>, marks that index (or key constraint). <see cref="Mode"/> is <c>DEFAULT</c>, <c>FULL</c>, <c>NOTHING</c>, or <c>USING INDEX</c>.
/// </summary>
public sealed record AlterTableReplicaIdentityNode(
    string? TableName,
    string? SchemaName,
    string Mode,
    string? IndexName) : SqlNode("AlterTableReplicaIdentity", TableName, SchemaName);
