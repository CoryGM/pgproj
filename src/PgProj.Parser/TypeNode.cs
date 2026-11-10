namespace PgProj.Parser;

/// <summary>
/// A <c>CREATE TYPE</c> statement: composite (<c>AS (...)</c>), enum (<c>AS ENUM (...)</c>), range
/// (<c>AS RANGE (...)</c>), or a shell/base type (no <c>AS</c> clause at all). Its structure is read here, from pg_query's tree and, for
/// the text deployed as written, the statement's tokens (G-90 part 4); <see cref="RawStatementSql"/> is the exact text of this one statement.
/// </summary>
public sealed record TypeNode(string? Name, string? SchemaName, string RawStatementSql) : SqlNode("Type", Name, SchemaName)
{
    /// <summary><c>composite</c>, <c>enum</c>, <c>range</c>, or <c>base</c> (G-267); a shell type is not a <see cref="TypeNode"/> (<see cref="BaseTypeNode"/>).</summary>
    public string? Form { get; init; }

    /// <summary>A base type's options (G-267), in the order written, as pg_query read them; empty for any other kind of type.</summary>
    public IReadOnlyList<BaseTypeOptionNode> BaseTypeOptions { get; init; } = [];

    /// <summary>
    /// Each composite field, in declaration order: its type as pg_query parsed it (G-99) and as written; empty for any other kind of type.
    /// The written text is what is deployed; the parsed type is what comparison uses, so <c>varchar(10)</c> and PostgreSQL's
    /// <c>character varying(10)</c> are one type.
    /// </summary>
    public IReadOnlyList<CompositeFieldTypeNode> CompositeFieldTypes { get; init; } = [];

    /// <summary>True when a typed table (<c>CREATE TABLE t OF this_type</c>) is of this composite type (G-136): a change to a field then needs <c>CASCADE</c>, which carries it to the table.</summary>
    public bool HasTypedTables { get; init; }

    /// <summary>An enum's labels, in declaration order, exactly as pg_query read them (G-90 part 4).</summary>
    public IReadOnlyList<string> EnumLabels { get; init; } = [];

    /// <summary>
    /// The labels the author says were renamed (G-191: <c>-- @renamed-label: old -> new</c> on the comment lines above the <c>CREATE TYPE</c>), in the order
    /// written: a hint for <c>publish</c>, never compared.
    /// </summary>
    public IReadOnlyList<EnumLabelRename> EnumLabelRenames { get; init; } = [];

    /// <summary>A range type's <c>name = value</c> options, each value as written; null for any other kind of type (G-90 part 4).</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? RangeOptions { get; init; }

    /// <summary>
    /// A range type's <c>SUBTYPE</c> as pg_query parsed it, read the way a column's type is, so <c>float8</c> and PostgreSQL's
    /// <c>double precision</c> compare as one type (G-115); null for any other kind of type.
    /// </summary>
    public CompositeFieldTypeNode? RangeSubtype { get; init; }
}

/// <summary>A composite type field's type, read from its <c>ColumnDef</c> the way a table column's type is (G-99).</summary>
public sealed record CompositeFieldTypeNode(
    string Name,
    string? BaseType,
    IReadOnlyList<string> Modifiers,
    int ArrayDimensions,
    string? IntervalFields)
{
    /// <summary>The field's type exactly as written (<c>varchar(10)</c>), for deployment; null when the statement's text could not be read.</summary>
    public string? RawType { get; init; }

    /// <summary>The field's <c>COLLATE</c>, its name parts joined with dots (<c>C</c>, <c>app.ci</c>), or null when it states none (G-192); not part of <see cref="RawType"/>.</summary>
    public string? CollationName { get; init; }
}

/// <summary>
/// One option of a base type's definition (G-267): its name in lower case (<c>input</c>, <c>internallength</c>, <c>passedbyvalue</c>, ...), and its value as pg_query
/// gives it: the name parts of a function, a type, or a keyword (<c>app</c>, <c>t_in</c>; <c>double</c>; <c>variable</c>), or the text of a string or number
/// (<c>N</c>, <c>16</c>, <c>true</c>). An option written without a value (<c>PASSEDBYVALUE</c>) has neither.
/// </summary>
public sealed record BaseTypeOptionNode(string Name, IReadOnlyList<string> NameParts, string? Value);

/// <summary>A label of an enum renamed from <see cref="From"/> to <see cref="To"/> (G-191), as the author's annotation says; labels are exact, as PostgreSQL compares them.</summary>
public sealed record EnumLabelRename(string From, string To);
