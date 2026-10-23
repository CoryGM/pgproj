# Composite types

A change to a composite type's fields is deployed in place, so it works while columns use the type:

| Change | Deployed as |
|---|---|
| A new field | `ALTER TYPE ... ADD ATTRIBUTE`, which PostgreSQL appends at the end |
| A removed field | `ALTER TYPE ... DROP ATTRIBUTE`, which `publish --execute` treats as destructive, since every stored value of that field is lost |
| A retyped field | `ALTER TYPE ... ALTER ATTRIBUTE ... TYPE` (see the limitation below) |
| A field's changed collation | `ALTER ATTRIBUTE ... TYPE ... COLLATE`, under the same limitation |
| A renamed field | a dropped field and an added one |
| A different kind of type | the type is dropped and created again |

A field's collation (`label text COLLATE "C"`) is deployed, read back, and compared as a column's is: the database's `pg_catalog."C"` and the project's
`"C"` are one collation, and a field without one has the default.

A composite type with no fields, `CREATE TYPE t AS ()`, is deployed, read back, scaffolded by `init`, and takes its first fields in place with
`ADD ATTRIBUTE`. An empty composite and an empty enum (`AS ENUM ()`) are two different types.

## Limitation: retyping a field a column uses

PostgreSQL refuses to retype a field while a table column uses the type, directly, as an array, or nested in another composite type
(`cannot alter type ... because column ... uses it`), even for a widening such as `varchar(10)` to `varchar(20)`. pgproj does not work around it: the
publish fails and rolls back, so nothing is lost.

To make such a change, create the new type under another name, move each column with
`ALTER TABLE ... ALTER COLUMN ... TYPE <new> USING ROW(...)::<new>`, drop the old type, and rename the new one (a pre- or post-deployment script can run
that), then update the project to match. Adding, dropping, and renaming fields is not affected.

Removing a field that a view reads fails on the server.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
