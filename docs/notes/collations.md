# Collations

Collations are modeled:

```sql
CREATE COLLATION name (PROVIDER = ..., LOCALE = ..., ...);
```

A libc collation is written with `LOCALE`, or with `LC_COLLATE` and `LC_CTYPE`; an ICU collation with `LOCALE`, with `DETERMINISTIC = false` and, on
PostgreSQL 16 and later, `RULES`. Write the collation as `schema.name` in a column or index (`COLLATE app.ci`). `init` writes each one to
`Schema/<schema>/Collation/<name>.sql`.

## Order and comparing

A collation is deployed before the columns, index keys, composite type fields, domains, and range types that name it, whatever order the files are in,
and read back from `pg_collation`.

- An ICU locale is compared as a language tag: `en_US` and `en-us` are one locale.
- An ICU collation on the root locale may be written with an empty locale, `LOCALE = ''`, which is how `pg_dump` writes it before PostgreSQL 16 (16 and
  later print `und`). It is deployed as written, since PostgreSQL 14 and 15 refuse an ICU collation that states no locale at all; `init` writes the
  empty locale for a collation read that way; and `''` and `und` are the same locale to the comparison.
- `VERSION` is not modeled: the server records it.

## Changing one

A collation cannot be altered.

- A change to one nothing uses is a drop and a create.
- A change to one that a column, index, or range type uses is not carried out, because PostgreSQL refuses to drop it: `publish --execute` stops and names
  the collation. Create it under a new name, move what uses it, and drop the old one.
- A removed collation is dropped. That is not a destructive change, since a collation holds no data, and PostgreSQL itself refuses while anything uses
  it.
- Its owner (`ALTER COLLATION ... OWNER TO`) and comment (`COMMENT ON COLLATION`) are modeled like any other object's, and written beside it by `init`.
  A comment-only change is a `COMMENT ON`; it does not drop a collation that something uses.

## Not the project's, and not modeled

- The collations PostgreSQL ships, the ones it imports from the operating system, and one an extension owns are left alone. When `schemas` is set, only
  the collations in one of those schemas are managed.
- `CREATE COLLATION ... FROM` and the other `ALTER COLLATION` forms are warned about and ignored.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
