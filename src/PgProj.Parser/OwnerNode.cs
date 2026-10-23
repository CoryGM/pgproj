namespace PgProj.Parser;

/// <summary>
/// The owner a project states for one object (G-146): <c>ALTER TABLE|VIEW|MATERIALIZED VIEW|SEQUENCE|FUNCTION|PROCEDURE|AGGREGATE|OPERATOR|TYPE|DOMAIN|
/// SCHEMA|EVENT TRIGGER|PUBLICATION name OWNER TO role</c>, or <c>CREATE SCHEMA name AUTHORIZATION role</c>. A transient marker: the semantic
/// layer finds the object it names, in whichever file declares it, and turns it into an owner definition.
/// </summary>
/// <param name="ObjectKind">
/// <c>Schema</c>, <c>Table</c>, <c>View</c>, <c>MaterializedView</c>, <c>Sequence</c>, <c>Function</c>, <c>Procedure</c>, <c>Aggregate</c>,
/// <c>Operator</c>, <c>Type</c>, <c>Domain</c>, <c>EventTrigger</c>, or <c>Publication</c>.
/// </param>
/// <param name="SchemaName">The object's schema as written; null for a schema, an event trigger, a publication, and a name with no schema.</param>
/// <param name="ObjectName">The object's name (for a schema, the schema; for an operator, its symbols).</param>
/// <param name="OwnerRole">
/// The role as written, or null for <c>CURRENT_USER</c> and <c>SESSION_USER</c>, which name whoever runs the script and cannot be compared.
/// </param>
public sealed record OwnerNode(string ObjectKind, string? SchemaName, string? ObjectName, string? OwnerRole) : SqlNode("Owner", ObjectName, SchemaName)
{
    /// <summary>
    /// For a function, procedure, or aggregate, the argument types as written (<c>pg_catalog.int4</c>, <c>text[]</c>), in order; empty for
    /// <c>f()</c> and <c>agg(*)</c>. Null when no argument list was written (<c>ALTER FUNCTION f OWNER TO r</c>), which names the only overload.
    /// </summary>
    public IReadOnlyList<string>? ArgumentTypes { get; init; }

    /// <summary>For an operator, the left operand's type as written; null for a prefix operator.</summary>
    public string? LeftType { get; init; }

    /// <summary>For an operator, the right operand's type as written; null for a postfix operator.</summary>
    public string? RightType { get; init; }
}
