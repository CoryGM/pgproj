using PgProj.Parser;

namespace PgProj.Semantics;

public sealed record TableDefinition(
    string Key,
    string Name,
    string? SchemaName,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<string> ConstraintKeys,
    IReadOnlyList<string> ReferencedTables,
    IReadOnlyList<string> ReferencedColumns,
    IReadOnlyList<string> ReferencedTypes,
    IReadOnlyList<string> ReferencedFunctions,
    IReadOnlyList<TableDefinition> ResolvedTables,
    IReadOnlyList<ColumnDefinition> ResolvedColumns,
    IReadOnlyList<TypeDefinition> ResolvedTypes,
    IReadOnlyList<FunctionDefinition> ResolvedFunctions,
    string? Comment = null) : ISemanticNode
{
    public string Kind => "Table";

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>True for <c>CREATE UNLOGGED TABLE</c> (G-24b).</summary>
    public bool IsUnlogged { get; init; }

    /// <summary>The table's <c>TABLESPACE</c> name, or null for the database default.</summary>
    public string? TablespaceName { get; init; }

    /// <summary>
    /// The table's <c>USING</c> access method (G-223), compared only when the project states one, since the default is the server's
    /// <c>default_table_access_method</c>; changed in place with <c>ALTER TABLE ... SET ACCESS METHOD</c>, which rewrites the table.
    /// </summary>
    public string? AccessMethod { get; init; }

    /// <summary>The table's <c>WITH (...)</c> storage parameters, in the order written.</summary>
    public IReadOnlyList<TableStorageOptionNode> StorageOptions { get; init; } = [];

    /// <summary>
    /// <see cref="StorageOptions"/> sorted by name (G-24b): the storage parameter list is a set, and PostgreSQL's own read-back
    /// (<c>pg_class.reloptions</c>) is not in the order the author wrote, so comparing the written order would report a phantom
    /// difference every time. <see cref="StorageOptions"/> itself stays in the order written, for deploy output.
    /// </summary>
    public IReadOnlyList<TableStorageOptionNode> NormalizedStorageOptions { get; init; } = [];

    /// <summary>The strategy of <c>PARTITION BY</c> (<c>"RANGE"</c>, <c>"LIST"</c>, or <c>"HASH"</c>), or null when this table is not partitioned (G-24c).</summary>
    public string? PartitionStrategy { get; init; }

    /// <summary>The <c>PARTITION BY</c> key, in the order written. Empty when <see cref="PartitionStrategy"/> is null.</summary>
    public IReadOnlyList<PartitionKeyElementNode> PartitionKeyElements { get; init; } = [];

    /// <summary>The parent's schema, when this table is a partition (<c>CREATE TABLE ... PARTITION OF parent ...</c>).</summary>
    public string? PartitionOfSchemaName { get; init; }

    /// <summary>The parent's name, when this table is a partition. Null for an ordinary table or a partitioned parent.</summary>
    public string? PartitionOfTableName { get; init; }

    /// <summary>The schema of the composite type this table is of (a typed table, <c>CREATE TABLE t OF s.type</c>, G-136); null for an ordinary table.</summary>
    public string? OfTypeSchemaName { get; init; }

    /// <summary>The composite type this table is of; null for an ordinary table. A typed table's columns are its type's fields: adding, dropping, or retyping one is done on the type, and <c>CASCADE</c> carries it here.</summary>
    public string? OfTypeName { get; init; }

    /// <summary>True for a typed table.</summary>
    public bool IsTypedTable => !string.IsNullOrWhiteSpace(OfTypeName);

    /// <summary>The key of the composite type a typed table is of (<c>schema.type</c>); null for an ordinary table.</summary>
    public string? OfTypeKey => IsTypedTable ? DatabaseModel.CreateKey(OfTypeSchemaName, OfTypeName!) : null;

    /// <summary>
    /// The keys of the tables this table inherits from (<c>INHERITS (...)</c>), in the order written (G-194); empty for none. Compared as a set: a parent added
    /// is <c>ALTER TABLE ... INHERIT</c>, one removed <c>NO INHERIT</c>. The parents' columns are on <see cref="Columns"/>, each with
    /// <see cref="ColumnDefinition.InheritedFromTable"/> set.
    /// </summary>
    public IReadOnlyList<string> InheritsFrom { get; init; } = [];

    /// <summary>
    /// The <c>LIKE</c> clauses of the table's <c>CREATE TABLE</c> (G-221), each deployed as written. What they copy is already in the model, marked
    /// <see cref="ColumnDefinition.CopiedByLike"/> and so on; this is kept only to write the statement. Not compared.
    /// </summary>
    public IReadOnlyList<PgProj.Parser.TableLikeNode> LikeClauses { get; init; } = [];

    /// <summary>
    /// The <c>FOR VALUES ...</c> or <c>DEFAULT</c> clause, exactly as written, when this table is a partition. Null unless
    /// <see cref="PartitionOfTableName"/> is set.
    /// </summary>
    public string? PartitionBoundRaw { get; init; }

    /// <summary>
    /// True when a partition has to be created with its own column list and attached (<c>CREATE TABLE child (...)</c>, then <c>ALTER TABLE parent ATTACH PARTITION child ...</c>), because
    /// a NOT NULL constraint the project names on one of its columns is not the parent's (G-264): a partition made with <c>PARTITION OF</c> takes the parent's constraint name.
    /// Set by <see cref="PartitionColumnInheritancePass"/>; not compared.
    /// </summary>
    public bool PartitionKeepsOwnColumns { get; init; }

    /// <summary>
    /// The replica identity the project states (G-255): <c>DEFAULT</c>, <c>FULL</c>, <c>NOTHING</c>, or <c>USING INDEX</c> (the index carries <see cref="IndexDefinition.IsReplicaIdentity"/>); a live table has
    /// it when it is not the default. Compared only when the project states one.
    /// </summary>
    public string? ReplicaIdentity { get; init; }

    /// <summary>True when the project states the table is clustered on no index (<c>SET WITHOUT CLUSTER</c>, G-260); a table the project says nothing about keeps whatever the database has.</summary>
    public bool WithoutCluster { get; init; }

    /// <summary>
    /// The firing mode of the table's internal (foreign-key) triggers, <c>ENABLE</c> or <c>DISABLE</c> (G-261), stated by <c>ALTER TABLE t { ENABLE | DISABLE } TRIGGER ALL</c>; a live table has it when they are
    /// disabled. Compared one way: only when the project states it (a table the project says nothing about keeps whatever the database has). Deploying it needs a superuser (<c>--allow-superuser-statements</c>).
    /// </summary>
    public string? InternalTriggerState { get; init; }

    /// <summary>The defaults this child gives columns it only inherits (G-237); <see cref="InheritedColumnDefaultPass"/> applies them to the inherited columns.</summary>
    public IReadOnlyList<InheritedColumnDefault> InheritedColumnDefaults { get; init; } = [];

    /// <summary>The NOT NULL constraints this child has of its own on columns it only inherits (G-253); <see cref="InheritedColumnDefaultPass"/> applies them.</summary>
    public IReadOnlyList<InheritedColumnNotNull> InheritedColumnNotNulls { get; init; } = [];

    /// <summary>The element list of <c>PARTITION OF parent (...)</c> as written (G-266), deployed in the partition's <c>CREATE TABLE</c>; null when there is none. Not compared: what it states is in the columns and constraints.</summary>
    public string? PartitionElementsText { get; init; }

    /// <summary>
    /// The bound as PostgreSQL itself renders it (<c>pg_get_expr(relpartbound, oid)</c>), or null when not available
    /// (G-101). A live partition carries its own rendering; a project partition carries the rendering of its bound on
    /// a temporary copy of the live parent, made during <c>diff</c>/<c>publish</c>. PostgreSQL rewrites a bound's
    /// literals in the key's type (<c>'2026-01-01'</c> on a <c>timestamptz</c> key is <c>'2026-01-01 00:00:00+00'</c>).
    /// </summary>
    public string? ServerRenderedPartitionBound { get; init; }

    /// <summary>
    /// The two bounds to compare (G-101): PostgreSQL's renderings when both tables have one, otherwise the bounds as
    /// written.
    /// </summary>
    public static (string? Old, string? New) GetComparablePartitionBounds(TableDefinition oldTable, TableDefinition newTable)
    {
        ArgumentNullException.ThrowIfNull(oldTable);
        ArgumentNullException.ThrowIfNull(newTable);

        return oldTable.ServerRenderedPartitionBound is not null && newTable.ServerRenderedPartitionBound is not null
            ? (oldTable.ServerRenderedPartitionBound, newTable.ServerRenderedPartitionBound)
            : (oldTable.PartitionBoundRaw, newTable.PartitionBoundRaw);
    }

    /// <summary>True once <c>ALTER TABLE ... ENABLE ROW LEVEL SECURITY</c> has been applied (G-76).</summary>
    public bool IsRowSecurityEnabled { get; init; }

    /// <summary>True once <c>ALTER TABLE ... FORCE ROW LEVEL SECURITY</c> has been applied (G-76); meaningless unless <see cref="IsRowSecurityEnabled"/> is also true.</summary>
    public bool IsRowSecurityForced { get; init; }

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
}
