# Tablespaces

Tablespaces: a table's or materialized view's `TABLESPACE` clause is deployed and compared by name, but pgproj never creates a tablespace (it is
cluster-wide and needs a superuser and a directory on the database server; `CREATE TABLESPACE` in a project is reported as not
modeled). `publish --execute` first asks the server for its tablespaces and refuses, before running anything, when a table or materialized view names one
it does not have, listing each tablespace and the tables and materialized views that name it; `--dry-run` and `--output` print the script with a warning.
Create the tablespace on the server first, or remove the clause. A table written with the database's default tablespace (`TABLESPACE pg_default` in
a default database) is the same as one written with no clause, because PostgreSQL stores it that way; in a database created with another default
tablespace, `pg_default` is a different, real one. An index's `TABLESPACE` clause is not read.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
