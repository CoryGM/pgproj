# Row-level security policies

Row-level security policies (`CREATE POLICY`) are compared by their command (`SELECT`, `INSERT`, `UPDATE`, `DELETE`, or `ALL`), their `PERMISSIVE` or
`RESTRICTIVE` kind, their role list, and their `USING` and `WITH CHECK` expressions. A policy belongs to its table, as a trigger or a constraint does:
the same policy name can exist on two tables.

## Comparing the expressions

A policy's `USING` and `WITH CHECK` are compared as PostgreSQL prints them: a project policy that also exists in the database is put in place of the live
one inside a transaction that is always rolled back, and `pg_get_expr` is read back. So a subquery written `(SELECT t.c <= m.c1 FROM public.m)` is the
same policy the server prints as `( SELECT (t.c <= m.c1) FROM m)`.

A role that does not own the table, or a policy that uses something the publish has yet to create, is compared by its canonical text instead.

## Changing a policy

- Any change but its comment is deployed by dropping and recreating the policy, since `ALTER POLICY` cannot change the command or the
  `PERMISSIVE`/`RESTRICTIVE` kind.
- A change to only the comment is `COMMENT ON POLICY`, in place, and a recreated policy gets its comment back.
- A `USING` or `WITH CHECK` that is a subquery, `WITH CHECK ((SELECT ...))`, is deployed with its own parentheses inside the clause's.
- `ALTER TABLE ... ENABLE | DISABLE | FORCE | NO FORCE ROW LEVEL SECURITY` are applied as in-place `ALTER TABLE` statements.

## Limits

- `ALTER POLICY ... RENAME TO` and a `DROP POLICY` in the project are not modeled: a renamed or removed policy is one whose `CREATE POLICY` no longer
  appears, as for every other kind of object except a column ([Renaming columns](../renaming-columns.md)).
- A policy on a table an extension owns belongs to the extension and is left alone.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
