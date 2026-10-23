namespace PgProj.Parser;

/// <summary>
/// A <c>CREATE CAST (source AS target)</c> statement (G-131): a user-defined conversion between two types, done by a function, by
/// binary coercion (<c>WITHOUT FUNCTION</c>), or by the types' text I/O (<c>WITH INOUT</c>). A cast belongs to no schema; the pair of types
/// is its identity.
/// </summary>
public sealed record CastNode(string? SourceType, string? TargetType) : SqlNode("Cast", $"{SourceType} AS {TargetType}", null)
{
    /// <summary>
    /// The types named in the statement as the author wrote them (G-207), keyed by the normalized name the node keeps
    /// (<c>pg_catalog.bpchar</c> to <c>char(1)</c>, <c>My S.My T</c> to <c>"My S"."My T"</c>): deployment writes these, and the normalized
    /// names stay the identity and comparison form. Never compared.
    /// </summary>
    public IReadOnlyDictionary<string, string> WrittenTypes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The function that converts, as written (<c>public.text_to_mytype</c>), without its arguments; null for <c>WITHOUT FUNCTION</c> and <c>WITH INOUT</c>.</summary>
    public string? FunctionName { get; init; }

    /// <summary>The argument types of <see cref="FunctionName"/> as written, in order (it names the function, which may be overloaded).</summary>
    public IReadOnlyList<string> FunctionArguments { get; init; } = [];

    /// <summary>When the cast is applied: <c>explicit</c> (the default, only for <c>CAST(x AS t)</c>), <c>assignment</c> (<c>AS ASSIGNMENT</c>), or <c>implicit</c> (<c>AS IMPLICIT</c>).</summary>
    public string Context { get; init; } = "explicit";

    /// <summary>True for <c>WITH INOUT</c>: the conversion goes through the types' text input and output functions.</summary>
    public bool IsInOut { get; init; }
}
