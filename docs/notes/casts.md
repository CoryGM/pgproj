# Casts

Casts: `CREATE CAST (source AS target) WITH FUNCTION f(args) | WITHOUT FUNCTION | WITH INOUT [AS ASSIGNMENT | AS IMPLICIT]` is deployed after the function and the types it uses, read back from `pg_cast`, and compared by its method, its function and that function's argument types, and when it applies. A cast is identified by its pair of types (`integer` and `pg_catalog.int4` are one), has no schema, and cannot be altered, so any change is a drop and a create. `init` writes each one to `Cast/<source>_to_<target>.sql`. Removing one is a destructive change, so `publish --execute` refuses it without `--allow-destructive`. The casts PostgreSQL ships, one an extension owns, and the ones it makes along with a type (a range type's cast to its multirange type) are not the project's and are left alone; when `schemas` is set, only the casts whose function or types are in one of those schemas are managed. `DROP CAST` and `COMMENT ON CAST` in a project are warned about and ignored.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
