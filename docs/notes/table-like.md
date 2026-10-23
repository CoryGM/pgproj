# Tables written with LIKE

`CREATE TABLE t (..., LIKE source [INCLUDING ... | EXCLUDING ...], ...)` is deployed as written: the `LIKE` clause is in the `CREATE TABLE`, where it was written among the table's own columns. PostgreSQL copies what it copies when the table is created, so pgproj gives the table the same in its model, and `diff` compares the table with what PostgreSQL made:

- **Always:** the source's columns, in order, at the clause's place, with their types, collations, and `NOT NULL` (a primary key's and an identity's included).
- **`INCLUDING DEFAULTS`:** column defaults. A `serial` column's default names the source's sequence, which the copy then shares.
- **`INCLUDING GENERATED`:** generated columns' expressions. Without it a generated column is copied as an ordinary column.
- **`INCLUDING IDENTITY`:** identities, with their options; the table gets a sequence of its own.
- **`INCLUDING CONSTRAINTS`:** `CHECK` constraints, under the source's names (a `NOT VALID` one is copied valid). Foreign keys are never copied.
- **`INCLUDING INDEXES`:** the primary key, unique and exclusion constraints, and indexes, named as PostgreSQL names an index created without a name: `t_pkey`, `t_<columns>_key`, `t_<columns>_excl`, `t_<columns>_idx`. An expression is named as PostgreSQL names it (`lower(n)` is `lower`; one it names nothing is `expr`), a repeated name gets a number (`t_u_u1_idx`), and a name already taken in the schema gets a number after its label (`t_pkey1`).
- **`INCLUDING STATISTICS`:** extended statistics, named `t_<columns>_stat`, with the columns before the expressions as PostgreSQL lists them.
- **`INCLUDING COMMENTS`:** the comments of the copied columns, `CHECK` constraints, and indexes.
- **`INCLUDING STORAGE`, `INCLUDING COMPRESSION`:** the columns' `STORAGE` and `COMPRESSION` as the source states them.
- `INCLUDING ALL` is all of these, and an `EXCLUDING` after it takes one away.

The table is created after its source and after each constraint, index, and statistics object the clause copies, since those are statements of their own. What the clause copies is not created again; a copied comment is set after the table. A table can copy from a table that itself copies (it is read once that one has its copies), or from a composite type (its fields become columns). A table that copies from itself, directly or through others, is an error, and so is a column the table has twice.

Once the table exists, it follows its source: a column, `CHECK`, or index the source gains is added to the table too, with `ALTER TABLE ... ADD` and `CREATE INDEX`, as a fresh deploy would have made it. A column the source loses is dropped from the table, which `publish --execute` treats as destructive. An `ALTER TABLE ... ADD COLUMN` on the table comes after the copied columns.

**A source the project does not declare** is a reference like any other: `build` reports a missing table, an error in one of the project's `schemas` and a warning outside them ([unresolved references](unresolved-references.md)). `diff` and `publish` read such a source from the database (its columns, constraints, indexes, statistics, and comments) and build the project again with it, so the table has the columns PostgreSQL gave it. Without a connection (`build`, `document`), the table has only its own columns.

`init` writes a table made with `LIKE` as the database has it: every column and constraint, without the clause.

Not handled: a view or materialized view as the source (its columns are not known without the database; the table keeps only its own columns, with a warning). A source's inherited `CHECK` constraints are not copied.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
