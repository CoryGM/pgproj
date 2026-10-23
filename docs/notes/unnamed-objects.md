# Unnamed constraints and indexes

A constraint or index written without a name is given the name PostgreSQL gives it, so the project compares with the database it deploys. The names are chosen across all of the project's files, in the order the files and statements are read, as a deploy from scratch would choose them:

- **Primary key:** `<table>_pkey`.
- **Unique:** `<table>_<columns>_key`, the `INCLUDE` columns among the columns (`UNIQUE (a) INCLUDE (b)` is `t_a_b_key`).
- **Exclusion:** `<table>_<columns>_excl`, the `INCLUDE` columns among them too. An expression is named as PostgreSQL names it: a function call by the function's name (`lower(b)` is `lower`), a cast by what it casts, and one PostgreSQL names nothing `expr`.
- **Foreign key:** `<table>_<columns>_fkey`.
- **Check:** `<table>_<column>_check` when it uses exactly one column, `<table>_check` otherwise.
- **Index** (`CREATE INDEX ON t (...)`, unique or not, on a table or a materialized view): `<table>_<columns>_idx`, the `INCLUDE` columns among them and an expression named as above (`CREATE INDEX ON t (lower(b))` is `t_lower_idx`). A column named twice gets a number (`t_a_a1_idx`). The index is deployed without a name, as written, and PostgreSQL gives it this name; a second one with the same columns is `t_a_b_idx1`.

A name longer than 63 bytes is shortened as PostgreSQL shortens it. A name already taken gets a number after its label (`t_a_check1`). When the number depends on something other than an earlier unnamed constraint in the same file, `build` warns: PostgreSQL picks the number from the order the statements actually run, which pgproj cannot know across files or against a deployed database, so give the constraint or index a name.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
