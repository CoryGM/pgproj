namespace PgProj.Semantics;

/// <summary>
/// A privilege the live database has on an object, kept as the statement that grants it, for a project that does not model grants
/// (<c>excludeGrants</c>, G-87). pgproj never proposes grant changes for such a project, but an object it drops and recreates loses its
/// privileges with it, so they are issued again from here (G-116). <see cref="Name"/> is null for a statement over a whole schema
/// (<c>GRANT ... ON ALL TABLES IN SCHEMA</c>); <see cref="ObjectKind"/> is <c>table</c> (tables, views, and materialized views alike),
/// <c>sequence</c>, <c>function</c>, or <c>procedure</c>.
/// </summary>
public sealed record UnmodeledPrivilege(string ObjectKind, string SchemaName, string? Name, string Statement);
