namespace PgProj.Parser;

/// <summary>
/// A <c>CREATE AGGREGATE name (arguments) (SFUNC = ..., STYPE = ..., ...)</c> statement (G-130): a user-defined aggregate function. It lives in a schema and is
/// identified like an overloaded function, by that schema, its name, and the types of its arguments. Its options (the state and final functions and types, the
/// moving-aggregate variants, the initial values, the sort operator, and the flags) are kept by name.
/// </summary>
public sealed record AggregateNode(string? Name, string? SchemaName) : SqlNode("Aggregate", Name, SchemaName)
{
    /// <summary>
    /// The types named in the statement as the author wrote them (G-207), keyed by the normalized name the node keeps
    /// (<c>pg_catalog.bpchar</c> to <c>char(1)</c>, <c>My S.My T</c> to <c>"My S"."My T"</c>): deployment writes these, and the normalized
    /// names stay the identity and comparison form. Never compared.
    /// </summary>
    public IReadOnlyDictionary<string, string> WrittenTypes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The argument types as written, in order (<c>integer</c>, <c>public.mood</c>, <c>integer[]</c>), each with a <c>VARIADIC </c> prefix when it is variadic.
    /// Empty for <c>(*)</c>. For an ordered-set aggregate the direct arguments come first and the ordered ones after them (see <see cref="DirectArgumentCount"/>).
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// The name each argument was given (<c>VARIADIC items anycompatiblearray</c>), parallel to <see cref="Arguments"/>; null for an argument written without one (G-292). Deployed, so the
    /// catalog keeps the names (<c>pg_proc.proargnames</c>), and never part of the aggregate's identity or compared, as a routine's parameter names are not.
    /// </summary>
    public IReadOnlyList<string?> ArgumentNames { get; init; } = [];

    /// <summary>Negative for an ordinary aggregate; for an ordered-set (or hypothetical-set) one, how many of <see cref="Arguments"/> are direct arguments, before <c>ORDER BY</c>.</summary>
    public int DirectArgumentCount { get; init; } = -1;

    /// <summary>
    /// The options by lower-case name: a function or operator as its name parts joined by dots, a type as written (<c>[]</c> for an array), a number or string as its
    /// text, and a flag without a value (<c>finalfunc_extra</c>, <c>hypothetical</c>) as <c>true</c>. The old <c>BASETYPE</c> option is the argument, not an option.
    /// </summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}
