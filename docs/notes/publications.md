# Publications

Logical replication publications are modeled:

```sql
CREATE PUBLICATION name
    [FOR ALL TABLES | FOR TABLE t [(columns)] [WHERE (filter)], ... | FOR TABLES IN SCHEMA s, ...]
    [WITH (publish = ..., publish_via_partition_root = ..., publish_generated_columns = ...)];
```

A publication belongs to no schema; its name is its identity. It is deployed after the tables it names, and `init` writes each one to
`Publication/<name>.sql`.

## Comparing and changing

It is read back from `pg_publication` and compared by whether it is `FOR ALL TABLES`, its options, its schemas, and its tables with their column lists
(a set) and row filters.

- PostgreSQL prints a row filter its own way (`(n > 0)`), so a filter is compared in a form that ignores that.
- An option the project leaves out and PostgreSQL defaults is not a difference.
- A modified publication is changed in place with `ALTER PUBLICATION` (its options, its tables, its schemas), so nothing is dropped and subscribers see
  no gap. Only a change to `FOR ALL TABLES`, which cannot be altered into or out of, is a drop and a create.
- Removing one is a destructive change, since its subscribers stop receiving, so `publish --execute` refuses it without `--allow-destructive`.

PostgreSQL does not allow a column list in a publication that also has `TABLES IN SCHEMA`.

## `ALTER PUBLICATION` in project SQL

`pg_dump` writes a publication as an empty `CREATE PUBLICATION` and one `ADD TABLE` for each table, so these are applied to the publication of that name,
in any file:

- `ALTER PUBLICATION name ADD { TABLE ... | TABLES IN SCHEMA ... }` appends tables and schemas;
- `ALTER PUBLICATION name DROP ...` removes them;
- `ALTER PUBLICATION name SET ...` replaces them;
- `ALTER PUBLICATION name SET (...)` changes the options.

One for a publication the project does not declare is a warning.

## Scope and limits

- A publication an extension owns is left alone. When `schemas` is set, a publication is managed when a table or schema it names is in one of those
  schemas, or when it names nothing.
- `DROP PUBLICATION` and `COMMENT ON PUBLICATION` are warned about and ignored, and a publication's comment is not read. Its owner is: see
  [Owners](owners.md).

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
