namespace PgProj.Parser;

public sealed record ViewNode(
    string? Name,
    string? SchemaName,
    bool IsMaterialized = false) : SqlNode("View", Name, SchemaName)
{
    public string? RawBodyExpression { get; init; }

    /// <summary>
    /// True for <c>CREATE OR REPLACE VIEW</c> (G-236). One that follows a <c>CREATE VIEW</c> of the same name replaces it, as it does in PostgreSQL: <c>pg_dump</c> writes a view that other
    /// views depend on as a placeholder (<c>SELECT NULL::type AS col</c>) first and the real body with <c>CREATE OR REPLACE VIEW</c> later.
    /// </summary>
    public bool IsReplace { get; init; }

    /// <summary>
    /// pg_query's tree for the view's query (the <c>SelectStmt</c> under <c>ViewStmt.query</c> or <c>CreateTableAsStmt.query</c>), as
    /// JSON. What the view reads (relations, columns, calls, casts) is bound from this, not from the SQL text (G-90).
    /// </summary>
    public string? QueryAst { get; init; }

    /// <summary>The view's options in their canonical form (<see cref="ViewOptions"/>), sorted: <c>WITH (security_barrier, security_invoker)</c> and the check option (G-180).</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>The column names of <c>CREATE VIEW v (a, b) AS ...</c>, in order; empty when none were written (and for a recursive view, whose list is part of its body; G-189).</summary>
    public IReadOnlyList<string> ColumnAliases { get; init; } = [];

    /// <summary>
    /// A view's <c>WITH (...)</c> options as written (<c>security_barrier = false</c>), which is what is deployed (G-217); <see cref="Options"/> is their
    /// canonical form, which is what is compared, and leaves out an option set to its default.
    /// </summary>
    public IReadOnlyList<TableStorageOptionNode> WrittenOptions { get; init; } = [];

    /// <summary>A materialized view's storage parameters (<c>WITH (fillfactor = 50)</c>), as written (G-215).</summary>
    public IReadOnlyList<TableStorageOptionNode> StorageOptions { get; init; } = [];

    /// <summary>True for a materialized view written <c>WITH NO DATA</c>, which is created unpopulated (G-215).</summary>
    public bool WithNoData { get; init; }

    /// <summary>A materialized view's <c>TABLESPACE</c>, as written; null when none is (G-219).</summary>
    public string? TablespaceName { get; init; }

    /// <summary>A materialized view's <c>USING</c> access method, as written; null when none is (G-219).</summary>
    public string? AccessMethod { get; init; }

    /// <summary>
    /// The defaults of the view's columns, from <c>ALTER VIEW v ALTER COLUMN c SET DEFAULT expr</c> (which <c>pg_dump</c> writes as <c>ALTER TABLE ONLY v ...</c>), by column, as written (G-291).
    /// An insert through an updatable view uses one when the statement does not give the column.
    /// </summary>
    public IReadOnlyList<ViewColumnDefault> ColumnDefaults { get; init; } = [];
}

/// <summary>A default set on a view's column: the column and the expression's text as written (G-291).</summary>
public sealed record ViewColumnDefault(string ColumnName, string Expression);
