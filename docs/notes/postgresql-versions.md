# PostgreSQL versions

pgproj is tested against PostgreSQL **14, 15, 16, 17, and 18**. It reads SQL with libpg_query, PostgreSQL 18's grammar; one grammar reads every
version's SQL.

## A feature the server is too old for

`publish --execute` checks the project against the connected server before anything runs, and refuses a construct the server does not accept,
naming it. `--dry-run` and `--output` only warn, since the script may be meant for another server.

| Needs | Constructs |
|---|---|
| PostgreSQL 15 | `NULLS NOT DISTINCT`; `ON DELETE SET NULL` or `SET DEFAULT` with a column list; a `security_invoker` view; an `UNLOGGED` sequence; a publication's column lists, row filters, and `TABLES IN SCHEMA` |
| PostgreSQL 16 | `STORAGE` inside a column's definition (`ALTER TABLE ... ALTER COLUMN ... SET STORAGE` works on every version, and is how `init` writes it) |
| PostgreSQL 17 | the `builtin` collation provider |
| PostgreSQL 18 | the four features below |

One change is not caught up front: changing the access method of a table or materialized view that already exists (`ALTER ... SET ACCESS METHOD`)
needs PostgreSQL 15, and on 14 the server refuses it and the publish rolls back.

On PostgreSQL 14, an ICU collation's locale, the `public` schema's default grants, and a generated column's dependencies are read as 14 records them.

## PostgreSQL 18 features

All four PostgreSQL 18 features that add to a schema are supported: each is deployed, read back, and compared, and `init` scaffolds it.

- **`NOT ENFORCED`** on a `CHECK` or `FOREIGN KEY` constraint.
- **`VIRTUAL` generated columns**, PostgreSQL 18's default when a generated column says neither `STORED` nor `VIRTUAL`.
- **Temporal keys:** `WITHOUT OVERLAPS` on a `PRIMARY KEY` or `UNIQUE` constraint, and `PERIOD` on a `FOREIGN KEY`. They need the `btree_gist`
  extension, which the project declares like any other.
- **`NOT NULL ... NOT VALID`**, written `ALTER TABLE t ADD [CONSTRAINT name] NOT NULL col NOT VALID`.

How a change to one is deployed:

- A generated column changed between stored and virtual, or a key changed to or from temporal, is dropped and added.
- A `NOT VALID` NOT NULL that becomes a plain one is validated, by its name in the database; a valid one that becomes `NOT VALID` is dropped and added
  back.

A project that uses one of them can only be executed against a PostgreSQL 18 server: `publish --execute` refuses on an older one, naming each such
constraint or column (`--dry-run` and `--output` print the script with a warning).

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
