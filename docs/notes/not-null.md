# `NOT NULL`

`NOT NULL`: a column's `NOT NULL` is its nullability. `CONSTRAINT nn NOT NULL` on a column, and the table forms PostgreSQL 18 adds (`CONSTRAINT nn NOT NULL a` in `CREATE TABLE`, `ALTER TABLE ... ADD CONSTRAINT nn NOT NULL a`), all make the column `NOT NULL` and give the constraint the name `nn`.

**The name and the comment (PostgreSQL 18).** From 18 on, the server stores a column's `NOT NULL` as a constraint, named `<table>_<column>_not_null` unless it was named. pgproj deploys the name as written (`a int CONSTRAINT nn NOT NULL`; an older server accepts the name and forgets it), reads it back, scaffolds it with `init` when it is not the default, and compares it:

- Only a name the project **states** is compared, and only against a name the database **has**. A `NOT NULL` with no name in the project is not compared, whatever the database calls it, and on a server before 18, which has no name, nothing is compared. A project that never names a `NOT NULL` is as before.
- A different name is a **rename in place** (`ALTER TABLE ... RENAME CONSTRAINT`), keeping the constraint. A column that becomes `NOT NULL` under a name is named right after (`SET NOT NULL`, then a rename in a `DO` block that runs only on 18 or later).
- `COMMENT ON CONSTRAINT nn ON table` is the constraint's comment. It is deployed, changed in place, and scaffolded, and compared when the project states one, on a server that stores the constraint (on an older one the `COMMENT ON` statement is skipped).
- A child of `INHERITS`, or a partition, has its parent's constraint, under the parent's name, unless it names its own; it has no comment of its own.

Not modeled: the name of a `NOT NULL` that `LIKE` copies, and a partition's own name for a column it declares.

## `NO INHERIT` and a child's own constraints (PostgreSQL 18)

- `NOT NULL NO INHERIT` (`f1 integer NOT NULL NO INHERIT`, `CONSTRAINT nn NOT NULL a NO INHERIT`) is kept. It is the parent's own constraint, so a child does not get it: the child's column is nullable. It is compared as written; turning it on or off for a column that is already NOT NULL is `ALTER TABLE ... ALTER CONSTRAINT name [NO] INHERIT` by the constraint's live name, and making a nullable column `NOT NULL NO INHERIT` is `ADD [CONSTRAINT name] NOT NULL column NO INHERIT`.
- A child that names its own constraint on a column it only inherits (`CREATE TABLE c (CONSTRAINT foo NOT NULL a) INHERITS (p)`) keeps the name; it is a constraint among the child's elements, so it can only be set when the table is created (PostgreSQL refuses a second named constraint on a column that already has an inherited one).
- A child that declares `NOT NULL` on a column its parent also has keeps its own constraint, under its own name, not the parent's; one that declares the column without `NOT NULL` gets the parent's and does not repeat it.
- A partition made first with its own NOT NULL constraint names and attached later keeps them. `CREATE TABLE child PARTITION OF parent` gives the partition the parent's names, so when a project names a partition's NOT NULL otherwise, `publish` creates the partition with its columns and then attaches it (`CREATE TABLE child (partid integer CONSTRAINT child_partid_not_null NOT NULL, ...); ALTER TABLE parent ATTACH PARTITION child FOR VALUES ...`), and `init` writes a live partition that way, naming every NOT NULL of it (even the one PostgreSQL named by itself). A partition with the parent's names stays `PARTITION OF`. A partition's column is never an identity of its own, so the identity clause is left out of the column list.
- A partition's element list can name a NOT NULL: `PARTITION OF parent (a CONSTRAINT child_an NOT NULL)`, or as a table constraint, `(CONSTRAINT child_an NOT NULL a)`, which only PostgreSQL 18 accepts. It is deployed as written, and the name is the partition's own. A separate `ALTER TABLE child ADD CONSTRAINT n NOT NULL a` on a partition is still reported and ignored. See [partitioning](storage-partitioning-and-comments.md).
- Not handled: changing the name of an existing partition's NOT NULL constraint (PostgreSQL refuses to rename an inherited constraint); the partition has to be re-created.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
