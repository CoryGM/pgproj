using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// A <c>CREATE RULE</c> (G-128): a query rewrite rule on a table or a view, scoped to it like a trigger or a policy. Its body is arbitrary SQL that PostgreSQL
/// rewrites when it stores the rule, so a rule is compared by the server's rendering of it (<c>pg_get_ruledef</c>, <see cref="ServerRenderedDefinition"/>) as a
/// view is (G-96), and by its text in a normal form when there is no rendering to compare. The tables, views, functions, and operators it uses are dependencies.
/// </summary>
public sealed record RuleDefinition(
    string Key,
    string Name,
    string? SchemaName,
    string? TableName) : ISemanticNode
{
    public string Kind => "Rule";

    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    public IReadOnlyList<string> ReferencedOperators { get; init; } = [];

    /// <summary>The views the rule uses, its own view among them when it is a rule on a view (a rule is created after the relation it is on).</summary>
    public IReadOnlyList<string> ReferencedViews { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The command the rule applies to: <c>select</c>, <c>insert</c>, <c>update</c>, or <c>delete</c>.</summary>
    public string Event { get; init; } = "select";

    /// <summary>True for <c>DO INSTEAD</c>, false for <c>DO ALSO</c>.</summary>
    public bool IsInstead { get; init; }

    /// <summary>The statement as written (or, for a live rule, as <c>pg_get_ruledef</c> prints it), for the deployed SQL.</summary>
    public string? RawDefinition { get; init; }

    /// <summary><see cref="RawDefinition"/> in a comparison form (without <c>OR REPLACE</c> and the semicolon, white space collapsed), used when there is no server rendering.</summary>
    public string? NormalizedDefinition { get; init; }

    /// <summary>
    /// <c>pg_get_ruledef</c>'s rendering of the rule, set on a live rule when it is read and on a project rule when a connection renders it (G-128). Two rules that
    /// both have one are compared by it, so a rule written another way from how PostgreSQL prints it is not a change.
    /// </summary>
    public string? ServerRenderedDefinition { get; init; }

    /// <summary>The rule's firing mode (G-257), as <see cref="TriggerDefinition.EnabledState"/> is the trigger's: null means enabled, and it is compared only when the project states one.</summary>
    public string? EnabledState { get; init; }

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

/// <summary>The identity of a rule (G-128).</summary>
public static class RuleKeys
{
    /// <summary>The key <c>rule:schema.relation.name</c>; the prefix keeps a rule apart from a trigger or a policy of the same name on the same relation.</summary>
    public static string Create(string? schemaName, string? tableName, string ruleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);
        return "rule:" + (string.IsNullOrWhiteSpace(tableName)
            ? DatabaseModel.CreateKey(schemaName, ruleName)
            : $"{DatabaseModel.CreateKey(schemaName, tableName)}.{ruleName.Trim()}");
    }
}
