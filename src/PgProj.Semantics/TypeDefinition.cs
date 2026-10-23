using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>A single <c>CHECK</c> constraint of a domain (G-77).</summary>
public sealed record DomainCheckConstraintDefinition(string? Name, string RawExpression)
{
    /// <summary>The pg_query tree of the expression (JSON), kept for the canonical comparison (G-73).</summary>
    public string? ExpressionAst { get; init; }

    public string? NormalizedExpression { get; init; }

    /// <summary>
    /// The check as PostgreSQL stores it, <c>pg_get_expr</c> of the constraint, read on the server for the project's check and the live one alike (G-288); null when it
    /// could not be read. Comparison only: what is deployed is <see cref="RawExpression"/>.
    /// </summary>
    public string? ServerRenderedCheck { get; init; }

    /// <summary>
    /// True for a check added <c>NOT VALID</c> (G-233): enforced for new rows, but the rows already in a column of the domain were not checked.
    /// In the database it is <c>convalidated = false</c>. Compared in one direction only, as a table constraint's is (<see cref="ConstraintDefinition.IsNotValid"/>).
    /// </summary>
    public bool IsNotValid { get; init; }

    /// <summary>
    /// True when <see cref="Name"/> was not written in the SQL and was given PostgreSQL's own default name
    /// (<see cref="DeepTypeStructurePass"/>, mirroring <see cref="PgProj.Parser.ConstraintNode.IsNameGenerated"/>).
    /// The generated name is a position artifact (a domain's second unnamed CHECK is numbered <c>_check1</c> only
    /// because of the first), so it must not make reordering two unnamed constraints look like a change.
    /// </summary>
    public bool IsNameGenerated { get; init; }

    /// <summary>
    /// True when the name is a position artifact (G-98): generated here (<see cref="IsNameGenerated"/>), or written in
    /// PostgreSQL's own default form for <paramref name="domainName"/> (<c>&lt;domain&gt;_check</c>,
    /// <c>&lt;domain&gt;_check1</c>, ...). A live database doesn't record whether a name was written or generated, so an
    /// unnamed CHECK reads back from it with the default name as if it had been written; applying the same rule to both
    /// sides lets the two meet.
    /// </summary>
    public bool HasPositionalName(string domainName)
    {
        ArgumentNullException.ThrowIfNull(domainName);

        if (IsNameGenerated || string.IsNullOrWhiteSpace(Name))
        {
            return true;
        }

        var prefix = $"{domainName}_check";
        return Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && Name.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

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

public sealed record TypeDefinition(
    string Key,
    string Name,
    string? SchemaName,
    string? TypeKind,
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

    public string Kind => "Type";

    public SqlNode? DeclarationNode { get; init; }

    public IReadOnlyList<CompositeTypeFieldDefinition> CompositeFields { get; init; } = [];

    /// <summary>The comments on a composite type's fields (<c>COMMENT ON COLUMN type.field</c>), by field name; a field without one is not in it (G-216).</summary>
    public IReadOnlyDictionary<string, string> ColumnComments { get; init; } = ColumnCommentMap.Empty;

    public RangeTypeOptionsDefinition? RangeOptions { get; init; }

    /// <summary>
    /// Ordered enum labels for <c>CREATE TYPE ... AS ENUM (...)</c> types, in declaration/
    /// <c>enumsortorder</c> order. Empty for non-enum types.
    /// </summary>
    public IReadOnlyList<string> EnumLabels { get; init; } = [];

    /// <summary>True for an enum type, which may have no labels yet (<c>CREATE TYPE t AS ENUM ()</c>, G-249): <see cref="EnumLabels"/> alone cannot tell an empty enum from a type that is not one.</summary>
    public bool IsEnum { get; init; }

    /// <summary>True for a composite type, which may have no fields (<c>CREATE TYPE t AS ()</c>, G-268): <see cref="CompositeFields"/> alone cannot tell an empty composite from a type that is not one.</summary>
    public bool IsComposite { get; init; }

    public string? Comment { get; init; }

    /// <summary>
    /// True when the project states this object's comment (G-213, for an extension): <c>COMMENT ON EXTENSION</c>, <c>IS NULL</c> included. An extension
    /// comes with its own comment, so one the project does not state is not compared, as a schema's is not (G-160).
    /// </summary>
    public bool CommentStated { get; init; }

    /// <summary>An extension's <c>CREATE EXTENSION</c> as the project wrote it, which is what is deployed (G-213); null for any other kind, or a live one.</summary>
    public string? ExtensionWrittenText { get; init; }

    /// <summary>An extension's <c>VERSION</c> (G-213): as written in a project, where it is compared only when stated, or the installed one in a live database.</summary>
    public string? ExtensionVersion { get; init; }

    /// <summary>True when an extension's <c>SCHEMA</c> is stated (G-213); otherwise PostgreSQL chooses it, and it is not compared.</summary>
    public bool ExtensionSchemaStated { get; init; }

    /// <summary>
    /// The keys of the extensions an extension is created after (G-213): those declared before it in a project, or the ones it requires in a live database
    /// (and those read before it). An extension is dropped before them.
    /// </summary>
    public IReadOnlyList<string> ExtensionRequires { get; init; } = [];

    public bool IsRecovered { get; set; }

    public bool HasErrors { get; set; }

    /// <summary>Base type of a domain (<c>TypeKind == "domain"</c>), the dotted name pg_query reports (may carry a <c>pg_catalog.</c> prefix, G-88). Null for a non-domain type.</summary>
    public string? DomainBaseType { get; init; }

    /// <summary>A domain's base type exactly as written, which is what is deployed (G-207); null when it was not read. Never compared.</summary>
    public string? DomainWrittenBaseType { get; init; }

    /// <summary>Modifiers of <see cref="DomainBaseType"/> exactly as declared, such as <c>10</c> and <c>2</c> for <c>numeric(10,2)</c>.</summary>
    public IReadOnlyList<string> DomainBaseTypeModifiers { get; init; } = [];

    /// <summary>Array dimensions of <see cref="DomainBaseType"/> (<c>text[]</c> is 1); 0 for a scalar.</summary>
    public int DomainBaseTypeArrayDimensions { get; init; }

    /// <summary>The field restriction of an <c>interval</c> <see cref="DomainBaseType"/> (for example <c>day to second</c>), or null.</summary>
    public string? DomainIntervalFields { get; init; }

    /// <summary>Raw SQL text of a domain's <c>DEFAULT</c> expression, or null when it has none. What is deployed (G-56).</summary>
    public string? DomainRawDefaultExpression { get; init; }

    /// <summary>True when a domain was declared <c>NOT NULL</c>.</summary>
    public bool DomainIsNotNull { get; init; }

    /// <summary>
    /// A domain's <c>COLLATE</c> as written (<c>C</c>, <c>app.ci</c>), or null when it states none (G-196). Compared as a column's collation is
    /// (<see cref="CollationNames"/>); PostgreSQL cannot change it in place, so a change drops and creates the domain.
    /// </summary>
    public string? DomainCollationName { get; init; }

    /// <summary>A domain's <c>CHECK</c> constraints, in declaration order.</summary>
    public IReadOnlyList<DomainCheckConstraintDefinition> DomainCheckConstraints { get; init; } = [];

    /// <summary>True when a typed table (<c>CREATE TABLE t OF this_type</c>) is of this composite type (G-136): changing one of its fields is written with <c>CASCADE</c>, which carries the change to the table.</summary>
    public bool HasTypedTables { get; init; }

    /// <summary>
    /// The comparison form of a domain's <c>DEFAULT</c> (null when it has none); equal for two spellings of the
    /// same default and different for a genuine change, mirroring <see cref="ColumnDefinition.GetComparableDefaultExpression"/> (G-77).
    /// </summary>
    public string? GetComparableDomainDefaultExpression()
        => DefaultExpressionCanonicalizer.Canonicalize(DomainRawDefaultExpression, DomainBaseType, DomainBaseTypeArrayDimensions);

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
