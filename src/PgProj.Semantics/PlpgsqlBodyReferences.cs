namespace PgProj.Semantics;

/// <summary>A relation (table or view) a PL/pgSQL body names, as written: <see cref="Schema"/> is null when it is unqualified.</summary>
public sealed record BodyRelation(string? Schema, string Name);

/// <summary>A column a PL/pgSQL body names through a qualifier (<c>o.total</c>, <c>public.orders.total</c>), with the relation the qualifier stands for.</summary>
public sealed record BodyColumn(string? Schema, string Table, string Column);

/// <summary>A function or procedure a PL/pgSQL body calls, as written.</summary>
public sealed record BodyFunction(string? Schema, string Name);

/// <summary>
/// What a PL/pgSQL routine's body names (G-303a, G-303b): the relations, qualified columns, and functions its SQL reads, as written, leaving out what the body
/// creates itself, a trigger's transition tables, system relations, and sequences. Never a dependency and never compared: PostgreSQL does not bind a PL/pgSQL
/// body when the routine is created. <c>publish</c> and <c>diff</c> use it to say when a change drops, renames, or retypes something a body names.
/// </summary>
public sealed record PlpgsqlBodyReferences(
    IReadOnlyList<BodyRelation> Relations,
    IReadOnlyList<BodyColumn> Columns,
    IReadOnlyList<BodyFunction> Functions);
