# Composite types

A change to a composite type's fields is deployed in place where PostgreSQL allows it, and otherwise the type is rebuilt under the columns that use it:

| Change | Deployed as |
|---|---|
| A new field | `ALTER TYPE ... ADD ATTRIBUTE`, which PostgreSQL appends at the end |
| A removed field | `ALTER TYPE ... DROP ATTRIBUTE`, which `publish --execute` treats as destructive, since every stored value of that field is lost |
| A retyped field, on a type no table column uses | `ALTER TYPE ... ALTER ATTRIBUTE ... TYPE` |
| A field's changed collation, on a type no table column uses | `ALTER ATTRIBUTE ... TYPE ... COLLATE` |
| A retyped field, or a changed collation, on a type table columns use | The type is rebuilt under its columns (below). A narrowing needs `--allow-destructive`; a widening does not. |
| A renamed field | a dropped field and an added one |
| A different kind of type | the type is dropped and created again |

A field's collation (`label text COLLATE "C"`) is deployed, read back, and compared as a column's is: the database's `pg_catalog."C"` and the project's
`"C"` are one collation, and a field without one has the default.

A composite type with no fields, `CREATE TYPE t AS ()`, is deployed, read back, scaffolded by `init`, and takes its first fields in place with
`ADD ATTRIBUTE`. An empty composite and an empty enum (`AS ENUM ()`) are two different types.

## Rebuilding a type under its columns

PostgreSQL refuses to retype a field while a table column uses the type (`cannot alter type ... because column ... uses it`), even for a widening such as
`varchar(10)` to `varchar(20)`. So `publish` does this in its transaction:

```sql
ALTER TYPE "app"."addr" RENAME TO "addr_pgproj_old";
CREATE TYPE "app"."addr" AS (street text, zip varchar(10), n int);
-- for each column that uses the type (its default dropped first and set again after):
ALTER TABLE "app"."homes" ALTER COLUMN "a" TYPE "app"."addr"
    USING CASE WHEN "a"::text IS NULL THEN NULL ELSE ROW(("a")."street", ("a")."zip", ("a")."n")::text::"app"."addr" END;
DROP TYPE "app"."addr_pgproj_old";
```

- Each value is built field by field, each field taken from the old field of its name (a field added in the same change is null), and read back through
  its text, so every field is parsed by its new type. A value that does not fit fails loudly rather than being cut: the publish stops and is rolled back,
  nothing applied, naming the column: `the column app.homes.a holds a value the composite type app.addr cannot take after the change, so the type was
  not rebuilt: value too long for type character varying(5)`. Change those rows first (a pre-deploy script can), or keep the field's type.
- A narrowing (`varchar(10)` to `varchar(5)`, `numeric(10,2)` to `numeric(5,1)`, any change not known to keep every value) needs `--allow-destructive`,
  and so does a field dropped in the same change; a widening (a longer `varchar`, `int` to `bigint`, more digits) does not. A number or a time given fewer
  fractional digits is **rounded**, not refused, as PostgreSQL rounds it in a cast: `12.345` in `numeric(10,3)` becomes `12.35` in `numeric(10,2)`.
- A null column stays null; a value whose fields are all null stays one.
- An array column moves the same way. When the old and new types have the same fields in the same order it moves through its text
  (`USING col::text::app.addr[]`). When they do not, it moves through a function made for the change (`app.addr_pgproj_move`), dropped before the old
  type, which keeps each element's order and nulls; a multi-dimensional array is refused by name there, and an array whose first index is not 1 comes back
  starting at 1.
- A view on the column, a `CHECK` that names it, and an index with a predicate or an expression on it are dropped first and created again after. A plain
  index or key on the column is rebuilt by PostgreSQL as the column changes.
- Changing a column's type rewrites its table under an `ACCESS EXCLUSIVE` lock, which blocks reads and writes for as long as it takes; `publish` warns
  about each table before anything runs ([Locks and large tables](locks.md)). `--concurrently` does not help here.
- The owner the project states for the type is set on the new one.

Adding and dropping fields are done in place, as is a retype on a type no table column uses (a typed table of it takes the change with `CASCADE`).

The type is not rebuilt, and `publish` refuses the change, naming why, when a domain over it, another composite type with a field of it, a routine
with it in its signature or its SQL body, a typed table of it, a partition key, or a partition or inheritance child's column uses it. A type no column uses
directly, used only inside another composite type a column uses, is changed in place, which PostgreSQL refuses; the publish rolls back. Make those changes by hand: create
the new type under another name, move each column with `ALTER TABLE ... ALTER COLUMN ... TYPE <new> USING ROW(...)::text::<new>`, drop the old type,
and rename the new one.

Removing a field that a view reads fails on the server.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
