namespace PgProj.Parser;

/// <summary>
/// A <c>CREATE OPERATOR name (LEFTARG = ..., RIGHTARG = ..., FUNCTION = ..., ...)</c> statement (G-129): a user-defined operator. It lives in a schema,
/// and its identity is that schema, its name (a run of symbols such as <c>===</c>), and the types of its two operands (one is absent for a prefix or
/// postfix operator).
/// </summary>
public sealed record OperatorNode(string? Name, string? SchemaName) : SqlNode("Operator", Name, SchemaName)
{
    /// <summary>
    /// The types named in the statement as the author wrote them (G-207), keyed by the normalized name the node keeps
    /// (<c>pg_catalog.bpchar</c> to <c>char(1)</c>, <c>My S.My T</c> to <c>"My S"."My T"</c>): deployment writes these, and the normalized
    /// names stay the identity and comparison form. Never compared.
    /// </summary>
    public IReadOnlyDictionary<string, string> WrittenTypes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The left operand's type as written (<c>integer</c>, <c>public.mood</c>); null for a prefix operator.</summary>
    public string? LeftType { get; init; }

    /// <summary>The right operand's type as written; null for a postfix operator.</summary>
    public string? RightType { get; init; }

    /// <summary>The function the operator runs, as written (<c>FUNCTION</c> or its older spelling <c>PROCEDURE</c>).</summary>
    public string? FunctionName { get; init; }

    /// <summary>The operator that does the same with its operands swapped (<c>COMMUTATOR</c>), as <c>schema.name</c> or <c>name</c>; null when none is given.</summary>
    public string? Commutator { get; init; }

    /// <summary>The operator that gives the opposite truth value (<c>NEGATOR</c>), as <c>schema.name</c> or <c>name</c>; null when none is given.</summary>
    public string? Negator { get; init; }

    /// <summary>The selectivity estimator for a <c>WHERE</c> use (<c>RESTRICT</c>), as written; null when none is given.</summary>
    public string? RestrictFunction { get; init; }

    /// <summary>The selectivity estimator for a join (<c>JOIN</c>), as written; null when none is given.</summary>
    public string? JoinFunction { get; init; }

    /// <summary>True when the operator was declared <c>HASHES</c> (usable in a hash join).</summary>
    public bool IsHashes { get; init; }

    /// <summary>True when the operator was declared <c>MERGES</c> (usable in a merge join).</summary>
    public bool IsMerges { get; init; }
}
