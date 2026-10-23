# Typed tables

Typed tables: `CREATE TABLE t OF some_type [(col WITH OPTIONS ...)]` takes its columns from the composite type (`CREATE TYPE some_type AS (...)`, which may be in another file): one column per field, in the type's order, with the defaults and `NOT NULL`s the table gives some of them. The table is created after its type, read back from `pg_class.reloftype`, and `init` writes it as `OF` its type. A field added to, dropped from, or retyped on the type reaches the table through `ALTER TYPE ... CASCADE`, which pgproj writes when a typed table is of the type (PostgreSQL refuses the change without it); the table's own change is then only what a table can change about a column (its default, `NOT NULL`), and a table that becomes typed, stops being typed, or changes its type is converted with `ALTER TABLE ... OF` and `NOT OF`. A typed table of a type the project does not declare as a composite type (a table's row type, an extension's type) is an error, and a composite type change that cannot be made in place cannot be deployed while a typed table is of it.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
