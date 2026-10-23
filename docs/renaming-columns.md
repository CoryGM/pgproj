# Renaming Columns

pgproj distinguishes a column rename from a drop-and-add pair only when the
rename is stated explicitly. This document describes the annotation syntax,
how it flows through the pipeline, and the SQL it produces.

## Why explicit annotation?

Comparing the old and new schema definitions for a table cannot reliably
detect renames on its own: a column removed in one place and a similarly
typed column added elsewhere are structurally indistinguishable from an
unrelated drop + add. Rather than guessing based on heuristics (matching
column position, type, or name similarity), pgproj requires the column
rename to be declared directly in the SQL definition. This keeps rename
detection deterministic and avoids accidentally destructive `DROP COLUMN` /
`ADD COLUMN` pairs when the user's intent was actually to rename data in
place.

## Syntax

Place a `-- @renamed-from: <old_name>` comment on the line(s) immediately
above the column definition you want to rename:

```sql
CREATE TABLE public.orders (
	id integer PRIMARY KEY,
	-- @renamed-from: legacy_name
	display_name text
);
```

Rules:

- The annotation must appear on a comment line directly above the column
  definition, ignoring blank lines. If any other non-blank, non-comment
  line appears between the annotation and the column, the annotation is
  not attached to that column.
- `<old_name>` is the column's previous name in the deployed/old schema.
  It is read as PostgreSQL reads a name (folded to lower case unless it is
  in double quotes) and matched exactly against the old table's column names:
  `Email` and `"email"` both name the column `email`, and a column created
  as `"Email"` is named `"Email"`. `diff` and `publish` read it the same way,
  so what `diff` shows as a rename is the `RENAME COLUMN` that
  `publish` writes.
- If no column with `<old_name>` exists in the old schema, or that column
  has already been claimed by another rename in the same diff, the
  annotation is ignored and the column is treated as newly added.
- A comment that starts like the annotation but is not one (`-- @renamed-from email`, without the colon; `-- @renamed_from: email`;
  `-- @renamed-from:` with no name) is not read as a rename, and `build`, `diff`, and `publish` warn about it at the comment, so a
  typo does not quietly turn a rename into a dropped and an added column.
- The annotation only affects diffing and deployment; the column keeps
  its new name (`display_name` above) as its actual name in the schema.

## Constraints and indexes on the column

`RENAME COLUMN` carries everything on the column with it: PostgreSQL tracks a constraint's or index's columns by number. So a rename is only the
`RENAME COLUMN`: a key, unique, or `CHECK` constraint, an index (its keys, expressions, `INCLUDE` columns, and `WHERE`), and a foreign key in
another table that references the column are compared with the old name read as the new one, and a change that is only the rename leaves them alone.
Before, each was dropped and added again, rebuilding the index and re-scanning the referencing table under lock. A real change alongside the rename
(another column in the key, a different condition) is still a change.

A constraint or index you did not name is named after its column by PostgreSQL (`t_email_key`, `t_email_check`, `t_code_idx`), and the database keeps that name
when the column is renamed. A fresh deploy of the renamed project would name it after the new column, so `publish` renames it too, in place, after the
`RENAME COLUMN` (`ALTER TABLE ... RENAME CONSTRAINT t_email_key TO t_email_address_key`, `ALTER INDEX ... RENAME TO t_sku_idx`). Renaming a unique
constraint renames its index with it; nothing is rebuilt. A constraint you named keeps its name.

## Views that output the column

A view that selects the renamed column keeps it under the old name in PostgreSQL, and `CREATE OR REPLACE VIEW` cannot rename a view's column. So when
you rename a column and the views that output it, `publish` renames the view's column first and then replaces the view in place:

```sql
ALTER TABLE "public"."customer" RENAME COLUMN "email" TO "email_address";
ALTER VIEW "public"."v_customer" RENAME COLUMN email TO email_address;
CREATE OR REPLACE VIEW "public"."v_customer" AS
SELECT id, email_address FROM public.customer;
```

The view isn't dropped, so the views built on it, its grants, and its column comments stay; a view built on it that outputs the column is renamed the
same way. To know the view's new columns, `diff` and `publish` render the project's view with the rename applied, in a transaction that is always
rolled back. A view whose columns change in another way as well is dropped and created again (see [Dependent objects](notes/dependent-objects.md)).

## How it flows through the pipeline

1. **Parsing** (`PgProj.Parser.PgQueryParser`): `TryCreateColumnNode`
   inspects the raw SQL immediately preceding the column's `ColumnDef`
   node (using the AST's `location` offset) for a `-- @renamed-from:`
   comment, and if found, stores the old name in `ColumnNode.RenamedFrom`.
2. **Semantic mapping** (`PgProj.Semantics.ColumnPass`): `RenamedFrom` is
   copied onto the resulting `ColumnDefinition`.
3. **Diffing** (`PgProj.Diff.DiffEngine.DiffColumns`): before matching
   columns by identity key, any new column with a non-null `RenamedFrom`
   is paired with the old column of that name (if it exists and hasn't
   already been claimed by another rename). The pair is recorded as a
   `Modified` column change instead of a `Removed` + `Added` pair, with a
   `Name` modification detail describing the old and new names.
4. **Deployment** (`PgProj.Deploy.AlterOperation.GenerateAlterTable`):
   renamed columns are emitted first as
   `ALTER TABLE ... RENAME COLUMN <old> TO <new>;`, ahead of any other
   `ALTER TABLE` statements (type changes, nullability, defaults) for the
   same table, so later statements can safely reference the column by its
   new name.

## Example

Old schema:

```sql
CREATE TABLE public.orders (
	id integer PRIMARY KEY,
	legacy_name text
);
```

New schema:

```sql
CREATE TABLE public.orders (
	id integer PRIMARY KEY,
	-- @renamed-from: legacy_name
	display_name text NOT NULL
);
```

Generated deployment SQL:

```sql
ALTER TABLE "public"."orders" RENAME COLUMN "legacy_name" TO "display_name";
ALTER TABLE "public"."orders" ALTER COLUMN "display_name" SET NOT NULL;
```

## Limitations

- Only column renames are supported by this annotation; table, schema, and
  other object renames are out of scope.
- The annotation is a one-time directive: once the rename has been applied
  and the deployed schema catches up, the annotation comment should be
  removed from the source SQL (leaving it in place is harmless but has no
  further effect once the old column name no longer exists in the
  baseline).
