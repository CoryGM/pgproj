using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>A <c>CREATE POLICY</c> (row-level security policy) (G-76). Scoped to its table, like a trigger or constraint (G-36/G-38).</summary>
public sealed record PolicyDefinition(
    string Key,
    string Name,
    string? SchemaName,
    string? TableName) : ISemanticNode
{
    public string Kind => "Policy";

    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    /// <summary>The command the policy applies to: <c>select</c>/<c>insert</c>/<c>update</c>/<c>delete</c>/<c>all</c>. Always present; defaults to <c>all</c>.</summary>
    /// <summary>The policy's comment (G-164); null when it has none.</summary>
    public string? Comment { get; init; }

    /// <summary>
    /// True when the model states the comment: a project has a <c>COMMENT ON POLICY</c> for it (<c>IS NULL</c> states that it has none), a live database has one when the policy has a comment. A comment is
    /// compared only for a policy whose comment is stated, so a database's comment on a policy the project does not mention is left alone (G-160, G-161, G-163).
    /// </summary>
    public bool CommentStated { get; init; }

    /// <summary>
    /// The <c>USING</c> expression as PostgreSQL prints it (<c>pg_get_expr</c>), when it was asked to (G-245): a live policy has it from its read, a project policy that also exists live by
    /// replacing the live one with it in a transaction that is rolled back. Two renderings are compared in place of the canonical forms. Null when there is none.
    /// </summary>
    public string? ServerRenderedUsing { get; init; }

    /// <summary>The <c>WITH CHECK</c> expression as PostgreSQL prints it (G-245), as <see cref="ServerRenderedUsing"/>.</summary>
    public string? ServerRenderedWithCheck { get; init; }

    public string Command { get; init; } = "all";

    /// <summary>True for a <c>PERMISSIVE</c> policy (the default); false for <c>RESTRICTIVE</c>.</summary>
    public bool IsPermissive { get; init; } = true;

    /// <summary>The roles the policy applies to, as written. Defaults to <c>["PUBLIC"]</c> when <c>TO</c> is omitted.</summary>
    public IReadOnlyList<string> Roles { get; init; } = ["PUBLIC"];

    /// <summary>
    /// <see cref="Roles"/> as a set, ignoring order and case (G-76: PostgreSQL stores <c>polroles</c> as an unordered array; the
    /// order a project writes <c>TO a, b</c> in is not meaningful). <see cref="Roles"/> itself stays in the order written, for
    /// deploy output.
    /// </summary>
    public IReadOnlyList<string> NormalizedRoles { get; init; } = ["PUBLIC"];

    /// <summary>The <c>USING ( ... )</c> expression exactly as written, kept beside <see cref="NormalizedUsingExpression"/> because normalization is lossy. Null when there is none.</summary>
    public string? RawUsingExpression { get; init; }

    /// <summary>The comparison form of <see cref="RawUsingExpression"/>, built from its parsed tree; null when there is no tree or no expression.</summary>
    public string? NormalizedUsingExpression { get; init; }

    /// <summary>The <c>WITH CHECK ( ... )</c> expression exactly as written, kept beside <see cref="NormalizedWithCheckExpression"/> because normalization is lossy. Null when there is none.</summary>
    public string? RawWithCheckExpression { get; init; }

    /// <summary>The comparison form of <see cref="RawWithCheckExpression"/>, built from its parsed tree; null when there is no tree or no expression.</summary>
    public string? NormalizedWithCheckExpression { get; init; }

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
