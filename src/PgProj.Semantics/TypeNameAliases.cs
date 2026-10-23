namespace PgProj.Semantics;

/// <summary>
/// The spellings of PostgreSQL's built-in types that name another type's catalog name (<c>integer</c> is <c>int4</c>, <c>character varying</c> is
/// <c>varchar</c>, <c>timestamp with time zone</c> is <c>timestamptz</c>): the one table every comparison form of a type name uses (G-207). Only
/// for comparison: what is deployed is the type as written.
/// </summary>
/// <remarks>
/// A bare <c>char</c> is not here. In SQL text it means <c>bpchar</c> (<see cref="DefaultExpressionCanonicalizer"/> adds that), but a name from
/// the parse tree is <c>char</c> only when it was the quoted internal type <c>"char"</c>, since pg_query writes an unquoted <c>char(n)</c> as
/// <c>pg_catalog.bpchar</c>.
/// </remarks>
internal static class TypeNameAliases
{
    /// <summary>Each alias, lower case with single spaces, to its catalog name.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["integer"] = "int4",
        ["int"] = "int4",
        ["bigint"] = "int8",
        ["smallint"] = "int2",
        ["character varying"] = "varchar",
        ["character"] = "bpchar",
        ["boolean"] = "bool",
        ["timestamp with time zone"] = "timestamptz",
        ["timestamp without time zone"] = "timestamp",
        ["time with time zone"] = "timetz",
        ["time without time zone"] = "time",
        ["double precision"] = "float8",
        ["real"] = "float4",
        ["decimal"] = "numeric",
        ["bit varying"] = "varbit",
    };

    /// <summary>
    /// The catalog name of <paramref name="name"/> (without modifiers) when it is an alias, which is a keyword and so matched without regard to
    /// case; any other name is returned as it is, since PostgreSQL compares a type's name exactly (<c>"Mood"</c> is not <c>mood</c>, G-206).
    /// </summary>
    public static string Canonical(string name)
        => All.TryGetValue(name.ToLowerInvariant(), out var catalogName) ? catalogName : name;
}
