# Locks and large tables

`publish --execute` runs its script in one transaction. Most of what it does takes a moment, but some changes to a table that already exists hold a heavy lock for as long as they take, which on a large table can be minutes, while the application waits.

## The warning

Before anything runs, in `--dry-run` and `--output` and before `--execute`, `publish` names each such change. Nothing is refused for it:

```
Warning: these changes rewrite, scan, or block writes to a table that already exists, for as long as they take on a large table:
  public.orders: ALTER COLUMN total TYPE rewrites the table and its indexes under an ACCESS EXCLUSIVE lock
  public.orders: CREATE INDEX orders_customer blocks writes to the table while the index is built
```

What is named:

- **Rewrites the table and its indexes** (`ACCESS EXCLUSIVE`, nothing else can read or write the table): a column type change, except a binary-coercible widening (`varchar(10)` to `varchar(20)` or to `varchar` or `text`, a `varbit` made longer, a `numeric` given more precision with the same scale); `SET ACCESS METHOD`; `SET LOGGED` or `SET UNLOGGED`; and an added column that is an identity, a serial, a stored generated column, or has a default that calls a volatile function (`random()`, `clock_timestamp()`, `gen_random_uuid()`, `nextval()`, or a project function not declared `IMMUTABLE` or `STABLE`). An added column with a constant or a `now()` default does not rewrite the table.
- **Scans the whole table under its lock:** `SET NOT NULL`; an added `CHECK` (`ACCESS EXCLUSIVE`); an added foreign key (`SHARE ROW EXCLUSIVE` on the table and the one it references). A constraint written `NOT VALID` does not scan; `VALIDATE CONSTRAINT` does it later under a lighter lock.
- **Blocks writes while an index is built:** a new index (`SHARE`), and a new primary key, unique, or exclusion constraint (which build one).

A table the same script creates has no rows yet, so nothing on it is named.

## `--lock-timeout`

Before a statement can change a table, it waits for its lock. If another session is in the middle of a long transaction on that table, the publish waits behind it, and every query on the table that comes after waits behind the publish: the application stalls until the long transaction ends.

`publish --execute --lock-timeout 5s` waits at most that long for each lock. Past it, the statement fails, the whole script is rolled back (nothing is applied), and the error names the statement that was waiting:

```
Publish failed: a lock was not granted within --lock-timeout 5s, so nothing was applied (the script was rolled back). The statement waiting for it: ALTER TABLE "public"."orders" ALTER COLUMN ...
```

Run it again later. The value is any duration PostgreSQL's `lock_timeout` takes: `500ms`, `5s`, `2min`, or a number of milliseconds. It is off unless given. With `--dry-run` or `--output`, the script starts with `SET lock_timeout = '5s';`, so it waits the same way when someone else runs it.

The timeout bounds the wait for a lock, not how long a statement runs once it has the lock: a rewrite named above still takes as long as it takes. For indexes and constraints, `--concurrently` (below) avoids the long lock.

## `--concurrently`

`publish --execute --concurrently` takes the slow part of new indexes and constraints on tables that already exist out of the transaction. The transaction runs first and commits; then each of these statements runs on its own, one at a time:

- **A new index:** `CREATE INDEX CONCURRENTLY`, which lets the table be written while the index is built (`SHARE UPDATE EXCLUSIVE`). On a partitioned table, where PostgreSQL refuses that, the index is created `ON ONLY` the parent in the transaction, then built concurrently on each partition and attached to the parent's, one partition at a time. A partition the same publish creates is empty; its index is made in the transaction.
- **A changed index:** the new definition is built concurrently under a temporary name (`<name>_pgproj_new`), the old index dropped with `DROP INDEX CONCURRENTLY`, and the new one renamed. Not when a column the index uses is dropped or changes type in the same publish, and not for the index the table's replica identity uses: those are rebuilt in the transaction.
- **A new foreign key or `CHECK`:** added `NOT VALID` in the transaction, which does not scan the table, then `VALIDATE CONSTRAINT` after it, which scans under a lock that lets the table be read and written. A foreign key on a partitioned table stays in the transaction before PostgreSQL 18, which is the first to accept `NOT VALID` for one.
- **A new unique constraint, or a primary key whose columns are already `NOT NULL`:** `CREATE UNIQUE INDEX CONCURRENTLY`, then `ADD CONSTRAINT ... USING INDEX`, which takes a brief lock. A primary key on a column that allows nulls stays in the transaction: `USING INDEX` would make the column `NOT NULL`, scanning the table under `ACCESS EXCLUSIVE`.
- Each index's comment, `CLUSTER ON`, and `REPLICA IDENTITY USING INDEX` follow it.

These stay in the transaction: everything on a table the same publish creates (it has no rows), an exclusion constraint (it has no `USING INDEX` form), a changed key or exclusion constraint (its index cannot be dropped concurrently), a key a new foreign key in the same publish references (it must exist first), a unique index that an existing foreign key may rely on when it changes, an index on a table with a sub-partitioned partition, and an expression index on a partitioned table written in a way pgproj cannot name per partition.

`--lock-timeout` applies to each of the statements after the commit too. The warning above names only what is left in the transaction, and the lock-table estimate counts only the transaction.

**When a statement after the commit fails** (a duplicate key in a new unique index, a row a new constraint rejects, a lock not granted in time), what was committed stays. `publish` drops the index that statement was building, so no half-built index is left behind, and exits with `1`:

```
Publish failed: step 1 of 2 after the commit failed (build the index of unique constraint t_email_key on public.t concurrently): 23505: could not create unique index "t_email_key"
  The statement that failed: CREATE UNIQUE INDEX CONCURRENTLY "t_email_key" ON "public"."t" ("email");
  Dropped what it was building: DROP INDEX CONCURRENTLY IF EXISTS "public"."t_email_key";
  The transaction and the steps before this one were committed. Not done:
    build the index of unique constraint t_email_key on public.t concurrently
    add unique constraint t_email_key using its index
  Run pgproj publish --execute --concurrently again to do the rest.
```

Fix the cause and run it again: the next `publish --concurrently` compares the database as it now is and does what is left.

**A printed or written script** (`--dry-run`, `--output`) has the statements for after the commit below this line:

```
-- pgproj --concurrently: the statements below run outside a transaction, one at a time
```

Run the part above the line in one transaction and the part below it without one, one statement at a time: `psql --single-transaction` would make `CREATE INDEX CONCURRENTLY` fail, and `publish` warns so.

### An `INVALID` index

A `CREATE INDEX CONCURRENTLY` that fails, whoever ran it, can leave an index PostgreSQL marks `INVALID`: queries do not use it, but its name is taken. `pgproj` reads that state:

- `diff` reports it (`Valid: 'False' -> 'True'`), so it is never taken for finished.
- `publish` rebuilds it: concurrently with `--concurrently`, in the transaction without it (with the warning above).
- `init` leaves it out, as `pg_dump` does, and warns with its name; add it to the project if you want it.

## The lock table and `--allow-lock-limit`

Every relation the script creates, alters, or drops stays locked until the transaction commits, and PostgreSQL keeps those locks in one table shared by every
session, of `max_locks_per_transaction * (max_connections + max_prepared_transactions)` locks: 6,400 with the defaults. A script that needs more fails partway
with `out of shared memory` and is rolled back. A schema of thousands of tables or partitions, or a schema per customer, reaches it.

Before anything runs, `publish` estimates the locks the script holds, on the generous side: one for each table and partition, index, sequence, view, and
materialized view it creates, alters, or drops, two more for a new table's TOAST table and index, a copy of an index on each partition of a partitioned table, and
the existing table a new foreign key references. Then:

- **Over the limit,** `--execute` refuses the script before anything runs, unless `--allow-lock-limit` is given:

  ```
  Publish failed: the script holds about 7,200 locks until it commits, more than the server's lock table holds (max_locks_per_transaction 64 * (max_connections 100 + max_prepared_transactions 0) = 6,400), so it would fail partway with "out of shared memory" and roll back. Raise max_locks_per_transaction to at least 90 (it takes a server restart), or publish in smaller steps.
  ```

- **From 80% of it,** `publish` warns, since other sessions use the same table.
- `--dry-run` and `--output` only warn: the script may be meant for another server.

Raising `max_locks_per_transaction` is the usual fix (it takes a restart). `--allow-lock-limit` is for when you know the estimate is too generous for your server.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
