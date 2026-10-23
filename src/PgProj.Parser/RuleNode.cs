namespace PgProj.Parser;

/// <summary>
/// A <c>CREATE [OR REPLACE] RULE name AS ON event TO table [WHERE condition] DO [ALSO | INSTEAD] { NOTHING | command | (command; ...) }</c> statement
/// (G-128): a query rewrite rule on a table or a view. A rule is scoped to its table (or view), like a trigger or a policy, and is identified by
/// its schema, that relation, and its name. Its body is arbitrary SQL that PostgreSQL rewrites when it stores it, so it is kept as written for
/// deployment and compared through the server's own rendering (<c>pg_get_ruledef</c>).
/// </summary>
public sealed record RuleNode(string? Name, string? SchemaName, string? TableName) : SqlNode("Rule", Name, SchemaName)
{
    /// <summary>The command the rule applies to: <c>select</c>, <c>insert</c>, <c>update</c>, or <c>delete</c>.</summary>
    public string Event { get; init; } = "select";

    /// <summary>The rule's firing mode when the project states one with <c>ALTER TABLE ... { ENABLE | DISABLE | ENABLE REPLICA | ENABLE ALWAYS } RULE</c> (G-257); null when it states none.</summary>
    public string? EnabledState { get; init; }

    /// <summary>True for <c>DO INSTEAD</c>; false for <c>DO ALSO</c>, which is what omitting it means.</summary>
    public bool IsInstead { get; init; }

    /// <summary>The statement exactly as written, for the deployed SQL and the fallback comparison.</summary>
    public string? RawDefinition { get; init; }

    /// <summary>The pg_query JSON of the rule (its condition and its actions), for reading the tables, views, functions, and operators it uses.</summary>
    public string? QueryAst { get; init; }
}
