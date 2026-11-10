# Enum types

An enum type is compared by its labels, in order. How `publish` makes a change depends on what changed, because PostgreSQL can add a label and rename one
in place, but cannot remove one or change their order.

| Change | What `publish` does |
|---|---|
| A label added, anywhere | `ALTER TYPE ... ADD VALUE`, with `BEFORE` or `AFTER` when it is not last. In place. |
| A label renamed, with an annotation (below) | `ALTER TYPE ... RENAME VALUE 'old' TO 'new'`. In place; every row keeps its value under the new name. |
| A label removed, or the labels reordered, on a type no column uses | The type is dropped and created again. |
| A label removed, or the labels reordered, on a type columns use | The type is rebuilt under its columns (below). A removed label needs `--allow-destructive`; a reorder does not. |

## Renaming a label

pgproj cannot tell a renamed label from one removed and another added, and does not guess. Say so above the `CREATE TYPE`, one line per label:

```sql
-- @renamed-label: glad -> happy
-- @renamed-label: 'on hold' -> 'paused'
CREATE TYPE app.mood AS ENUM ('sad', 'happy', 'paused');
```

A label is written as it is, or in single quotes when it has a space or another special character (a quote inside doubled). Labels are exact, as PostgreSQL
compares them. Once the database has the new label, the annotation is nothing to do, so it can stay; it is never compared.

Without the annotation, `glad` to `happy` is a removal and an addition: the type is rebuilt, and a row that holds `glad` stops the publish.

## Rebuilding a type under its columns

PostgreSQL has no `ALTER TYPE ... DROP VALUE`, and will not drop a type a column uses. So for a label removed or reordered, `publish` does this in its
transaction:

```sql
ALTER TYPE "app"."mood" RENAME TO "mood_pgproj_old";
CREATE TYPE "app"."mood" AS ENUM ('sad', 'happy');
-- for each column that uses the type (its default dropped first and set again after):
ALTER TABLE "app"."visits" ALTER COLUMN "mood" TYPE "app"."mood" USING "mood"::text::"app"."mood";
DROP TYPE "app"."mood_pgproj_old";
```

- An array column moves the same way (`USING col::text[]::app.mood[]`).
- A view on the column, a `CHECK` that names it, and an index with a predicate or an expression on it are dropped first and created again after, because
  they compare the column with the old type's labels. A plain index or key on the column is rebuilt by PostgreSQL as the column changes.
- Changing a column's type rewrites its table under an `ACCESS EXCLUSIVE` lock, which blocks reads and writes for as long as it takes; `publish` warns
  about each table before anything runs ([Locks and large tables](locks.md)). `--concurrently` does not help here.
- The owner the project states for the type is set on the new one.
- A row that holds a removed label cannot be moved. The publish stops and is rolled back, nothing applied:
  `the column app.visits.mood holds a label the enum type app.mood no longer has, so the type was not rebuilt`. Change or delete those rows first (a
  pre-deploy script can: `UPDATE app.visits SET mood = 'sad' WHERE mood = 'ok';`), or keep the label.

The type is not rebuilt, and `publish` refuses the change, naming why, when a domain over it, a composite type with a field of it, a routine with it in its
signature or its SQL body (a PL/pgSQL body is not read for this), a partition key, or a partition or inheritance child's column uses it. Make that change by hand, the same way.
