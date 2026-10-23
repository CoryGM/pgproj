# Dependent objects

PostgreSQL won't change a column's type, or drop a column, while a view, a policy, a trigger (its `WHEN` or `UPDATE OF`), or a `BEGIN ATOMIC` routine
uses it. `publish` reads from the database which of these use each column, drops the ones in the way of a change (and the views built on them) before
it, and recreates them from the project after it, with a comment in the script saying why.

- **Grants and owners are kept.** Each recreated object keeps its grants and, when another role owned it, its owner. A dropped and recreated object of
  any kind keeps its grants (with `excludeGrants`, the privileges the database had).
- **What the project does not have is refused.** A view in the way that the project doesn't define (another project's, outside `schemas`) makes
  `publish` refuse before anything runs, naming it.
- **A materialized view is recomputed** when it is recreated this way, so the change needs `--allow-destructive`.
- **A generated column in the way is refused**, since PostgreSQL 16 can't change one in place: remove it in one publish, make the change, and add it back
  in the next.

## A function or procedure that has to be dropped

A function or procedure whose return type or arguments change can only be changed by dropping it. Everything the database records as using it is taken
out of the way first and put back after:

- a view, index, trigger, policy, or `CHECK` is dropped and recreated (a `CHECK` is re-added `NOT VALID` and then validated);
- a column default or a domain's `CHECK` is detached and reattached;
- an operator or an aggregate built on it is dropped and recreated ([aggregates](aggregates.md)).

The routine keeps its grants and owner. Something else that uses it and that pgproj cannot recreate, such as a cast, makes `publish` refuse, naming it.

## A view whose columns change

- A view whose output columns change in a way `CREATE OR REPLACE VIEW` can't make (a column removed, reordered, or retyped, or two columns trading names)
  is dropped and created again, with the views built on it.
- A view that only gains columns at the end is replaced in place.
- A view whose columns are only renamed, as when it selects a table column the project renames, keeps its place too: `publish` renames them with
  `ALTER VIEW ... RENAME COLUMN` and then replaces it, so what is built on it, and its grants and comments, stay.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
