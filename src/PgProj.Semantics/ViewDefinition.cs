using PgProj.Parser;

namespace PgProj.Semantics;

public sealed record ViewDefinition(
    string Key,
    string Name,
    string? SchemaName,
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

    public string Kind => "View";

    public bool IsMaterialized { get; init; }

    /// <summary>
    /// The first definition of this view, when a later <c>CREATE OR REPLACE VIEW</c> in the project replaced it (G-254): a <c>pg_dump</c> placeholder (<c>SELECT NULL::bigint AS a</c>) or the
    /// view's earlier body. Two views that refer to each other can only be created by making one of them with this first definition and replacing it once the other exists.
    /// </summary>
    public ViewDefinition? InitialDefinition { get; init; }

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The keys of the project views (and materialized views) this one's query or body reads, so it is created after them (G-90, G-91).</summary>
    public IReadOnlyList<string> ReferencedViews { get; init; } = [];

    public string? RawBodyExpression { get; init; }

    /// <summary>pg_query's tree for the view's query as JSON (G-306), kept so a view's output columns can be worked out without a server; null for a view built in code. Not part of the comparison or of the model dump.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? QueryAst { get; init; }

    public ExpressionNode? SelectExpressionNode { get; init; }

    public IReadOnlyList<string> OutputColumnNames { get; init; } = [];

    public IReadOnlyList<string> OutputColumnTypes { get; init; } = [];

    public IReadOnlyList<string> NormalizedExpressions { get; init; } = [];

    /// <summary>
    /// The body as PostgreSQL itself renders it (<c>pg_get_viewdef(oid, true)</c>), or null when not available (G-96).
    /// A live view carries its own rendering. A project view carries the rendering of its body created as a temporary
    /// view on the target server during <c>diff</c>/<c>publish</c>. When both sides have one, the diff compares these
    /// instead of the parsed body, since PostgreSQL rewrites a view (drops a <c>public.</c> qualifier, adds aliases
    /// and casts, expands <c>*</c>) in ways a text comparison cannot reconcile.
    /// </summary>
    public string? ServerRenderedBody { get; init; }

    /// <summary>
    /// The view's output columns as PostgreSQL has them, each <c>name type</c> in order (G-104): a live view's from the catalog, a project
    /// view's from the temporary view it was rendered with. <c>CREATE OR REPLACE VIEW</c> can only append columns, so a change that
    /// removes, reorders, renames, or retypes one needs the view dropped and created instead. Empty when not known.
    /// </summary>
    public IReadOnlyList<string> ServerOutputColumns { get; init; } = [];

    /// <summary>
    /// The view's options in the canonical form of <see cref="ViewOptions"/> (<c>security_barrier=true</c>, <c>security_invoker=true</c>, <c>check_option=local</c>),
    /// sorted; an option that is off is left out. A project view's come from its <c>WITH (...)</c> and <c>WITH CHECK OPTION</c>, a live view's from
    /// <c>pg_class.reloptions</c> (G-180). Always empty for a materialized view, whose storage options are not modeled.
    /// </summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>The column names the project gave the view (<c>CREATE VIEW v (a, b) AS ...</c>), in order; empty for a live view, which carries them in its query (G-189).</summary>
    public IReadOnlyList<string> ColumnAliases { get; init; } = [];

    public string? Comment { get; init; }

    /// <summary>The comments on the view's columns (<c>COMMENT ON COLUMN view.column</c>), by column name; a column without one is not in it (G-216).</summary>
    public IReadOnlyDictionary<string, string> ColumnComments { get; init; } = ColumnCommentMap.Empty;

    /// <summary>The defaults of the view's columns (<c>ALTER VIEW v ALTER COLUMN c SET DEFAULT expr</c>), by column name, as written, which is what is deployed (G-291).</summary>
    public IReadOnlyDictionary<string, string> ColumnDefaults { get; init; } = ColumnCommentMap.Empty;

    /// <summary>
    /// <see cref="ColumnDefaults"/> in the comparison form of a column's default (<c>DefaultExpressionCanonicalizer</c>); a <c>DEFAULT NULL</c> is none, as PostgreSQL stores it (G-291).
    /// </summary>
    public IReadOnlyDictionary<string, string> NormalizedColumnDefaults { get; init; } = ColumnCommentMap.Empty;

    /// <summary>A view's <c>WITH (...)</c> options as written, which is what is deployed (G-217); <see cref="Options"/> is what is compared.</summary>
    public IReadOnlyList<PgProj.Parser.TableStorageOptionNode> WrittenOptions { get; init; } = [];

    /// <summary>A materialized view's storage parameters, as written; compared as a set and changed in place (G-215).</summary>
    public IReadOnlyList<PgProj.Parser.TableStorageOptionNode> StorageOptions { get; init; } = [];

    /// <summary>True for a materialized view written <c>WITH NO DATA</c>: it is created unpopulated. A choice made when it is created, so not compared (G-215).</summary>
    public bool WithNoData { get; init; }

    /// <summary>
    /// A materialized view's <c>TABLESPACE</c> (G-219), compared as a table's is (the database's default tablespace counts as none, G-153) and changed in place with
    /// <c>SET TABLESPACE</c>. A tablespace is cluster-wide, so it is checked for, never created.
    /// </summary>
    public string? TablespaceName { get; init; }

    /// <summary>
    /// A materialized view's <c>USING</c> access method (G-219), compared only when the project states one, since the default is the server's
    /// <c>default_table_access_method</c>; changed in place with <c>SET ACCESS METHOD</c>.
    /// </summary>
    public string? AccessMethod { get; init; }

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
