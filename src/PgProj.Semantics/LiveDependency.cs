namespace PgProj.Semantics;

/// <summary>
/// An object in the live database that PostgreSQL records (<c>pg_depend</c>) as depending on a relation, or on one of its columns
/// (G-103). PostgreSQL refuses to change a column's type, or to drop it, while such an object exists, so a publish that does either
/// drops the dependent first and recreates it afterwards. Read for the whole database, whatever the project's <c>schemas</c>, so a
/// dependent the project does not own is seen too (and refused, since pgproj cannot recreate it).
/// </summary>
/// <param name="DependentKind">
/// <c>view</c>, <c>materialized view</c>, <c>policy</c>, <c>trigger</c>, <c>function</c>, <c>procedure</c> (a <c>BEGIN ATOMIC</c> body),
/// <c>generated column</c>; and, for a routine (G-105), also <c>default</c> (a column's), <c>check</c> (a table's), <c>domain check</c>,
/// <c>index</c>, and <c>other</c> (an object pgproj doesn't model: a cast, operator, aggregate, or type).
/// </param>
/// <param name="DependentKey">The dependent's model key: <c>schema.name</c>, <c>schema.table.name</c> for a policy, trigger, or column, and
/// <c>schema.name(argument types)</c> for a routine.</param>
/// <param name="ReferencedKey">The relation it depends on (<c>schema.name</c>).</param>
/// <param name="ReferencedColumn">The column it depends on, or null for the relation as a whole.</param>
/// <param name="ReferencedKind"><c>relation</c>, or <c>routine</c> for an object that uses a function or procedure (G-105): PostgreSQL won't drop the
/// routine while it exists. <see cref="ReferencedKey"/> is then the routine's model key.</param>
public sealed record LiveDependency(string DependentKind, string DependentKey, string ReferencedKey, string? ReferencedColumn, string ReferencedKind = "relation");
