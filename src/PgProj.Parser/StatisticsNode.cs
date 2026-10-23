namespace PgProj.Parser;

/// <summary>One column or expression of a <c>CREATE STATISTICS</c> (G-150): a column by name, or an expression (PostgreSQL 14 and later) as text and as its tree.</summary>
public sealed record StatisticsElementNode(string? Name)
{
    /// <summary>The expression as text (rendered from its tree); null for a column.</summary>
    public string? RawExpression { get; init; }

    /// <summary>The pg_query JSON of the expression, for the comparison; null for a column.</summary>
    public string? ExpressionAst { get; init; }
}

/// <summary>
/// A <c>CREATE STATISTICS name [(kind, ...)] ON column-or-expression, ... FROM table</c> statement (G-150): extended statistics the planner uses for correlated
/// columns. The object lives in a schema (not necessarily its table's) and is about one table. Only this statement is read: <c>ALTER STATISTICS</c>, an owner, and a
/// comment are not.
/// </summary>
public sealed record StatisticsNode(string? Name, string? SchemaName) : SqlNode("Statistics", Name, SchemaName)
{
    /// <summary>The schema of the table the statistics are about; the default schema when it is not written.</summary>
    public string? TableSchemaName { get; init; }

    public string? TableName { get; init; }

    /// <summary>The kinds written (<c>ndistinct</c>, <c>dependencies</c>, <c>mcv</c>) in lower case; empty when none is, which means all three.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>The columns and expressions, in the order written.</summary>
    public IReadOnlyList<StatisticsElementNode> Elements { get; init; } = [];

    /// <summary>
    /// The key of the table a <c>LIKE</c> clause copied this statistics object from (G-221); null for one the project declares itself. The table's <c>CREATE TABLE</c>
    /// makes it, so it is not created on its own with the table; once the table exists it is changed like any other. Not compared.
    /// </summary>
    public string? CopiedByLike { get; init; }
}
