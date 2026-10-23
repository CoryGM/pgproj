# Query bodies

Query bodies: what a view (or materialized view) reads, and what a `LANGUAGE sql` function or procedure body reads (a string body or `BEGIN ATOMIC`), is read from PostgreSQL's own parse tree: its tables, views, qualified columns, function calls, and casts, with aliases, CTEs, subqueries, and `UNION` branches scoped as PostgreSQL scopes them. PostgreSQL checks these when it creates the object, so each is created after the tables, views, and functions it reads, including a view over another view and a SQL function calling another. A column is checked only when it is qualified by a table's name or alias. A PL/pgSQL body is not read, since PostgreSQL does not check it until it runs.

A PL/pgSQL body is read too, but only to warn about names the project does not define, and it never affects an order: [PL/pgSQL bodies](plpgsql-bodies.md).

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
