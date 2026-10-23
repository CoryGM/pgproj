# Destructive changes

`publish --execute` refuses to run a script that can discard data or access unless `--allow-destructive` is also given. It lists each affected operation with the reason, and runs nothing. These are the changes it counts:

- a dropped table;
- a dropped column (a renamed one is not dropped), or a changed column type that can lose a value. A widening keeps every value and is not destructive:
  `varchar` or `char` made longer or unbounded, or turned into `varchar` or `text` at least as long; `numeric` with at least as many digits
  before and after the point, or unconstrained; `smallint` to `int` or `bigint`, `int` to `bigint`; `real` to `double precision`; `varbit` made longer;
  `timestamp`, `timestamptz`, `time`, or `timetz` given more fractional digits; the same for arrays of them. A widening can still rewrite the table
  (`int` to `bigint` does), which `publish` names before it runs ([Locks and large tables](locks.md));
- a dropped materialized view (its computed data is lost until it is refreshed), including one dropped to be rebuilt around a change to what it uses;
- a dropped extension (every object it owns is dropped with it);
- a dropped field of a composite type (`ALTER TYPE ... DROP ATTRIBUTE`; the values it holds in stored rows are lost);
- a dropped cast, operator, aggregate, rule, publication, or event trigger that the script does not create again (one that is changed is dropped and created again, which loses nothing);
- a revoked grant or role membership (a genuine `REVOKE`, not a narrower re-grant), unless `excludeGrants`/`excludeRoleMemberships` leaves it out of the model;
- a changed owner of an existing object, since the old owner loses the privileges it had as the owner (setting the owner of a new object is not destructive).

This is deliberately conservative: any column type change is treated as destructive, even a widening one PostgreSQL could apply losslessly (`integer` to `bigint`), because telling a safe widening from a truncating narrowing in general needs per-type knowledge pgproj does not attempt. Rebuilding an index, a constraint, a plain view, a type, or a function, procedure, or trigger from source is not destructive, even when it is deployed as drop and create, since nothing is lost. A changed partition strategy or key is not covered by `--allow-destructive` at all: `publish` refuses it with no way to proceed.

`--output`/`--dry-run` always show the full script regardless of this check. `--execute` runs the script in a single transaction, so a failure partway leaves nothing applied; a script that is only printed or written is not wrapped in one (`publish` warns so), and should be run inside a transaction (`psql --single-transaction -f deploy.sql`) to get the same guarantee (deferred).

`publish --comments-only` deploys nothing but `COMMENT ON` statements, so this check does not apply to it ([Publishing only the comments](comments-only.md)).

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
