# Promoting between environments

The normal way to use pgproj is a **project in source control**: the SQL files are the desired state, and `publish` brings each environment up to date from them. This
page is for the other case, the edge case: there is no project, or the environments are not built from one, and one **database is the reference** for another (dev for
QA, QA for production). `diff` and `publish` can take a live database as the desired state, which also gives a parity check between two environments.

## Direction

A connection has a direction.

| | Is | Option | Variable | `pgproj.json` |
|---|---|---|---|---|
| **Source** | the desired state: the database that is right | `--source-connection` | `PGPROJ_SOURCE_CONNECTION` | `sourceConnectionString` (not used by `diff` and `publish`) |
| **Target** | the database that is compared and, for `publish`, changed | `--target-connection` | `PGPROJ_TARGET_CONNECTION` | `targetConnectionString` |

- **Only the target is ever changed.** The source is read in a read-only transaction.
- **Only the option on the command line turns source mode on.** A bare `--source-connection` takes the variable. The variable alone, and `sourceConnectionString` (which
  `init` writes), never switch `diff` or `publish` away from the project's SQL files, so an exported variable cannot change what an everyday run does.
- **The first line of output says which way it goes**: `Source shop_dev (localhost:5432) -> Target shop_qa (localhost:5432)`. `diff` adds what its marks mean: `+` is in
  the source only, `-` only in the target, `~` in both with a difference. `--format json` names both databases in `source` and `target`.
- **A database cannot be its own source.** The same host, port, and database is refused before anything is read; the same server and database reached under another name
  is refused once both are open (when the role can read the server's `system_identifier`).

## Walk-through: dev, QA, production

Three databases on one server (`shop_dev`, `shop_qa`, `shop_prod`), `shop_dev` with some tables and a view. `$C` is `Host=localhost;Username=postgres;Password=...`.

**1. Read what would change.** `--dry-run` prints the script and runs nothing; `--quiet` leaves only the script, so it can be saved.

```
$ pgproj publish --source-connection "$C;Database=shop_dev" --target-connection "$C;Database=shop_qa" --dry-run --exclude-owners
No diagnostics.
Source shop_dev (localhost:5432) -> Target shop_qa (localhost:5432)
CREATE TABLE "public"."customer" ( ...
```

**2. Run it.** `--execute` runs the script in one transaction, behind the same checks as any publish, all made against the target.

```
$ pgproj publish --source-connection "$C;Database=shop_dev" --target-connection "$C;Database=shop_qa" --execute --exclude-owners
Source shop_dev (localhost:5432) -> Target shop_qa (localhost:5432)
Executed deployment script.
```

**3. Check parity.** `--exit-code` makes the answer the exit code: 0 when the databases match, 2 when they differ, 1 on an error.

```
$ pgproj diff --source-connection "$C;Database=shop_dev" --target-connection "$C;Database=shop_qa" --exit-code --exclude-owners
Added: 0
Removed: 0
Modified: 0
$ echo $?
0
```

**4. Promote QA to production** the same way (`--source-connection "$C;Database=shop_qa" --target-connection "$C;Database=shop_prod"`). A second `publish` between a pair that
matches proposes nothing.

**5. When production has drifted.** Someone added a column to dev, and production has a table of its own that dev does not:

```
$ pgproj diff --source-connection "$C;Database=shop_dev" --target-connection "$C;Database=shop_prod" --exit-code --exclude-owners
Source shop_dev (localhost:5432) -> Target shop_prod (localhost:5432)
In what follows, an object marked + exists only in the source, one marked - only in the target, and ~ in both with a difference.
Added (1):
  + Column public.customer.phone
Removed (3):
  - Table public.hotfix_notes
  - Column public.hotfix_notes.id
  - Constraint public.hotfix_notes.hotfix_notes_pkey
Modified (1):
  ~ Table public.customer
      Columns: 'email, id' -> 'email, id, phone'
$ echo $?
2
```

**What only the target has is removed**, because the target is made to match the source. A `publish --execute` is therefore refused until you say it may discard data:

```
$ pgproj publish --source-connection "$C;Database=shop_dev" --target-connection "$C;Database=shop_prod" --execute --exclude-owners
Publish failed: 1 operation(s) can discard data and were not executed.
  [Table] public.hotfix_notes: table dropped
Use --output/--dry-run to review the generated script, and pass --allow-destructive to run it anyway.
```

Read the list under `Removed` before adding `--allow-destructive`. If production's own objects should stay, bring them into the source first, or leave them out of the
comparison with `schemas` in a `pgproj.json` (below). pgproj never drops a schema, and warns about one it leaves.

## Options environments usually need

Environments differ in who owns things and who may use them, so a promotion run usually adds:

- **`--exclude-owners`**: the source's owners (`ALTER ... OWNER TO`) are the source's roles, which production may not have. Without it, an owner that differs is a change, and a
  changed owner needs `--allow-destructive`.
- **`--exclude-grants`** and **`--exclude-role-memberships`**: the same for privileges and memberships. A role that a grant names must exist on the target
  (`publish --execute` checks, and pgproj never creates roles).
- **`--lock-timeout`**, **`--concurrently`**, **`--allow-lock-limit`**: for a production target with large tables ([Locks and large tables](locks.md)).

They apply to **both** databases, so a difference in what is excluded is never reported. See [Excluding grants, role memberships, and owners](excluding-grants-and-owners.md).

## The project file in this mode

`pgproj.json` is optional. When it is there, it still supplies what is not in the databases:

| Setting | Effect in source mode |
|---|---|
| `schemas` | Only these schemas are read from the source **and** the target, so a database shared with other projects is not touched. |
| `searchPath` | How unqualified names resolve; otherwise the one `init` would write for the source. |
| `excludeGrants`, `excludeRoleMemberships`, `excludeOwners` | As the options above; the options override. |
| `predeploy`, `postdeploy` | Run against the **target**, in the publish's transaction. Data and one-off scripts are not in the source database, so keep them here. |
| `targetConnectionString` | The default target, when `--target-connection` and `PGPROJ_TARGET_CONNECTION` are not given. |
| `source` and the SQL files | **Not read.** |
| `tenants` | Refused: a source database has no template. |

## In CI

Keep the passwords out of the command line with the variables, and let the exit code fail the job when two environments drift:

```yaml
- name: QA matches production
  env:
    PGPROJ_SOURCE_CONNECTION: ${{ secrets.QA_DATABASE }}
    PGPROJ_TARGET_CONNECTION: ${{ secrets.PROD_DATABASE }}
  run: pgproj diff --source-connection --exit-code --exclude-owners --exclude-grants --format json > parity.json
```

The bare `--source-connection` is what turns the mode on; its value comes from the variable. Exit code 2 fails the step; 1 is an error.

## Limits

- The source is read as [`init`](../commands.md#init) reads a database, so what `init` cannot read is not promoted: roles, tablespaces, foreign tables, subscriptions, and
  the other kinds `init` lists.
- Source and target may be different PostgreSQL versions; what the target is too old for is refused by name before `publish --execute` runs.
- The whole source is read before the target is opened.
- There is no plan file: `publish` plans from the target as it is when it runs. Review with `--dry-run` in the same run, or save the script with `--output`.
- `build --source-connection` models the source without a target, to see that what `init` would write builds; `document --source-connection` documents it.
