namespace PgProj.Parser;

/// <summary>
/// A <c>CREATE TYPE</c> statement: composite (<c>AS (...)</c>), enum (<c>AS ENUM (...)</c>), range
/// (<c>AS RANGE (...)</c>), or a shell/base type (no <c>AS</c> clause at all). Its structure is read here, from pg_query's tree and, for
/// the text deployed as written, the statement's tokens (G-90 part 4); <see cref="RawStatementSql"/> is the exact text of this one statement.
/// </summary>
public sealed record TypeNode(string? Name, string? SchemaName, string RawStatementSql) : SqlNode("Type", Name, SchemaName)
{
    /// <summary><c>composite</c>, <c>enum</c>, or <c>range</c>; null for a shell or base type, which has no structure to capture.</summary>
    public string? Form { get; init; }

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
