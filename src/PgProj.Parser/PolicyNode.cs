namespace PgProj.Parser;

/// <summary>
/// A <c>CREATE POLICY</c> statement (G-76). PostgreSQL scopes a policy's name to its table, the same as a trigger or a
/// constraint (G-36/G-38), so two tables may legitimately have a policy of the same name.
/// </summary>
public sealed record PolicyNode(
    string? Name,
    string? SchemaName,
    string? TableName) : SqlNode("Policy", Name, SchemaName)
{
    /// <summary>The command the policy applies to: <c>select</c>/<c>insert</c>/<c>update</c>/<c>delete</c>/<c>all</c>. Always present; defaults to <c>all</c> when <c>FOR</c> is omitted.</summary>
    public string Command { get; init; } = "all";

    /// <summary>True for a <c>PERMISSIVE</c> policy (the default, whether written or omitted); false for <c>RESTRICTIVE</c>.</summary>
    public bool IsPermissive { get; init; } = true;

    /// <summary>The roles the policy applies to, in the order written. Defaults to <c>["PUBLIC"]</c> when <c>TO</c> is omitted.</summary>
    public IReadOnlyList<string> Roles { get; init; } = ["PUBLIC"];

    /// <summary>The <c>USING ( ... )</c> expression exactly as written, without the outer parentheses; null when there is none.</summary>
    public string? RawUsingExpression { get; init; }

    /// <summary>The pg_query JSON of the <c>USING</c> expression, for the canonical comparison; null when there is none.</summary>
    public string? UsingExpressionAst { get; init; }

    /// <summary>The <c>WITH CHECK ( ... )</c> expression exactly as written, without the outer parentheses; null when there is none.</summary>
    public string? RawWithCheckExpression { get; init; }

    /// <summary>The pg_query JSON of the <c>WITH CHECK</c> expression, for the canonical comparison; null when there is none.</summary>
    public string? WithCheckExpressionAst { get; init; }
}
