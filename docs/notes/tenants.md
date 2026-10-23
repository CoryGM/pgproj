# Schema-per-tenant databases

Some applications keep each customer's data in a schema of its own: `tenant_acme`, `tenant_globex`, `tenant_initech`, all holding the same tables, views,
and functions, beside one or more schemas every tenant shares. pgproj can manage such a database from one project: you write the tenant's objects once,
in a **template**, and pgproj applies the template to every tenant's schema.

On this page: [the words used here](#the-words-used-here) · [a project with tenants](#a-project-with-tenants) · [starting from a database: `init`](#starting-from-a-database-init) · [writing the template](#writing-the-template) ·
[where the tenants come from](#where-the-tenants-come-from) · [what `build` prints](#what-build-prints) · [comparing: `diff`](#comparing-diff) ·
[deploying: `publish`](#deploying-publish) · [a tenant's own scripts](#a-tenants-own-scripts) · [troubleshooting](#troubleshooting) · [limits](#limits)

**A complete example** is in the repository: [`samples/SchemaPerTenant`](../../samples/SchemaPerTenant/README.md), an online storefront with a schema
per shop, a shared `platform` schema, tenants listed in a CSV file, and a tenant post-deploy script that seeds each new shop. Its README walks through
publishing it, adding a shop, changing the template, and seeing drift.

This is not the other common way of serving many tenants, where they share tables and a `tenant_id` column with row-level security keeps them apart.
That design needs nothing special from pgproj: it is an ordinary project ([row-level security](row-level-security.md)).

## The words used here

| Word | Meaning | In the example below |
|---|---|---|
| **shared project** | The project's ordinary SQL files: the schemas, tables, and functions every tenant shares. Deployed once. | `sql/shared/` |
| **template** | A folder of SQL that every tenant's schema holds, written **without a schema name**. | `sql/Tenant/` |
| **prefix** | The start of every tenant schema's name. | `tenant` |
| **tenant identifier** | What tells one tenant from another. | `acme` |
| **tenant schema** | A tenant's schema: the prefix, an underscore, and the identifier. | `tenant_acme` |
| **stand-in schema** | The prefix itself, used where the template has to name "this tenant's schema". `build` checks the template as if it were deployed there. | `tenant` |

## A project with tenants

```
shop/
├── pgproj.json
├── tenants.csv                  the list of tenants (one of three ways to give it)
└── sql/
    ├── shared/
    │   └── platform.sql         shared: CREATE SCHEMA platform; currencies, plans, ...
    └── Tenant/                  the template: every tenant's schema holds these
        ├── customers.sql
        ├── orders.sql
        └── grants.sql
```

```json
{
  "name": "shop",
  "version": "1.0",
  "source": "./sql",
  "searchPath": ["platform", "public"],
  "tenants": {
    "template": "./sql/Tenant",
    "prefix": "tenant",
    "file": "./tenants.csv"
  }
}
```

| Property | | What it is |
|---|---|---|
| `tenants.template` | required | The template folder, relative to `pgproj.json`. Every `*.sql` file under it, in any subfolder, is the template. It may be inside `source` (it is then not read as part of the shared project), but it cannot be `source` itself or hold it. |
| `tenants.prefix` | required | The start of every tenant schema's name: the tenant `acme`'s schema is `<prefix>_acme`. Read as PostgreSQL reads a name: folded to lower case unless written in double quotes (`"\"Tenant\""`). It cannot start with `pg_`, be `public`, `pg_catalog`, or `information_schema`, or be listed in `schemas` or `searchPath`; no schema in `schemas` or `searchPath` may be named `<prefix>_...` either. |
| `tenants.file` | optional | A CSV file listing the tenants (see [below](#a-csv-file-of-tenants)). |
| `tenants.query` | optional | A query that returns the tenants' identifiers (see [below](#a-query)). |
| `tenants.predeploy` | optional | A script run in each tenant's transaction before the template's changes (see [a tenant's own scripts](#a-tenants-own-scripts)). |
| `tenants.postdeploy` | optional | A script run in each tenant's transaction after the template's changes. |

Give at most one of `file` and `query`. With neither, the tenants are the schemas the database already has whose names start with the prefix.

## Starting from a database: `init`

If the database already has its tenants, `init --tenant-prefix` writes the project for you:

```
$ pgproj init --source-connection "Host=localhost;Database=shop;Username=postgres" --tenant-prefix tenant --tenant-file --report
Wrote 10 SQL file(s) to: C:\work\shop\sql
Wrote project file: C:\work\shop\pgproj.json
Wrote the tenant file: C:\work\shop\tenants.csv (3 tenant(s))
Tenants: 3 schema(s) named tenant_...; the template (Tenant, 6 file(s)) was scaffolded from tenant_acme.
1 tenant(s) differ from the template; pgproj diff shows how, and pgproj publish would make each match it: tenant_initech (0 added, 1 removed, 1 modified).
```

What it does:

1. **The tenants** are the schemas named `tenant_...`. The shared schemas (every other one) are scaffolded as `init` always does, into
   `sql/Schema/<schema>/`.
2. **One tenant becomes the template:** the first by name, or the one `--tenant-from acme` names. Its objects are written to `sql/Tenant/`, laid out
   like a schema (`Table/`, `View/`, `Function/`, ...), with its schema name taken out, so they read as the template above. The schema's own file is
   `sql/Tenant/tenant.sql`, where statements about the schema (its grants, owner, comment) name the stand-in. Anything of the tenant's that belongs to the
   whole database rather than a schema (an extension, a cast) is left out, and `init` says so.
3. **The other tenants are compared with the template,** as `diff` would, and each that differs is named with how many objects `publish` would add,
   remove, and change in it. Run `pgproj diff` to see what differs (above, someone added an index to `tenant_initech` by hand), and decide whether the
   template or the tenant is right before you `publish`.
4. **`pgproj.json` gets the `tenants` block:** `"template": "./sql/Tenant"`, `"prefix": "tenant"`, and the list of tenants: with `--tenant-file`, a
   `tenants.csv` beside `pgproj.json` listing the tenants found, so the list is a file from now on; with `--tenant-query "SELECT slug FROM
   platform.tenants"`, that query (`init` does not run it); with neither, the prefix alone.

With `--report`, the report has a **Tenants** section: the tenants, the one the template came from, and a table of those that differ.

A function written with `SET search_path FROM CURRENT` is stored by PostgreSQL as the path it found, `SET search_path TO 'tenant_acme', 'platform',
'public'`. `init` writes it with the stand-in, `SET search_path TO 'tenant', 'platform', 'public'`, which each tenant's copy reads as its own schema
(see [below](#functions-and-procedures-set-search_path-from-current)). A function body that still names the tenant's schema is kept as written, and
`init` names the file so you can change it.

## Writing the template

The template is plain SQL that PostgreSQL, `psql`, and your editor all read. The one rule that makes it a template: **write the tenant's own objects
without a schema.** pgproj reads the template once for each tenant, with that tenant's schema as the default schema, so `CREATE TABLE orders` becomes
`tenant_acme.orders` for `acme` and `tenant_globex.orders` for `globex`.

```sql
-- sql/Tenant/orders.sql
CREATE TYPE order_status AS ENUM ('open', 'paid', 'shipped');

CREATE TABLE customers (
    id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name text NOT NULL
);

CREATE TABLE orders (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id bigint NOT NULL REFERENCES customers (id),            -- the tenant's own table
    currency    char(3) NOT NULL REFERENCES platform.currencies (code), -- a shared table
    status      order_status NOT NULL DEFAULT 'open',
    total       numeric(12, 2) NOT NULL
);

CREATE INDEX ON orders (customer_id);

CREATE VIEW open_orders AS
    SELECT o.id, c.name, o.total FROM orders o JOIN customers c ON c.id = o.customer_id WHERE o.status = 'open';

CREATE FUNCTION order_count() RETURNS bigint
    LANGUAGE sql STABLE
    SET search_path FROM CURRENT
    AS $$ SELECT count(*) FROM orders $$;
```

### How a name in the template is found

A name written without a schema is looked for, as PostgreSQL looks for it, in this order: `pg_catalog`, then **the tenant's own schema**, then each schema
of the project's `searchPath`. So `orders` above is the tenant's `orders`, even though `public` might also have one, and with
`"searchPath": ["platform", "public"]` the template can call a shared function `round_money(total)` without writing `platform.`. A name with a schema
(`platform.currencies`) is that schema's object: that is how the template refers to something shared.

### Statements about the tenant's schema itself

Some statements have to name the schema itself: `GRANT USAGE ON SCHEMA`, `ALTER DEFAULT PRIVILEGES IN SCHEMA`, `GRANT ... ON ALL TABLES IN SCHEMA`,
`ALTER SCHEMA ... OWNER TO`, and `COMMENT ON SCHEMA`. In the template, they name the **stand-in schema**, the prefix, which stands for each tenant's
schema:

```sql
-- sql/Tenant/grants.sql
GRANT USAGE ON SCHEMA tenant TO app_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA tenant GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_user;
COMMENT ON SCHEMA tenant IS 'One customer''s data.';
```

The stand-in is used only there. Anywhere else, a name written with the stand-in (`tenant.orders`) is an error: write `orders`.

### Functions and procedures: `SET search_path FROM CURRENT`

PostgreSQL finds the names in a function's or procedure's body when it **runs**, not when it is created (a body written as a SQL-standard `BEGIN ATOMIC`
or `RETURN` is the exception: it is bound when it is created). It looks along the `search_path` of whoever calls it, and that path may not start with the
tenant's schema, so `SELECT count(*) FROM orders` could count another tenant's orders, or fail. Add `SET search_path FROM CURRENT` to each template
routine: pgproj creates a tenant's routines in a session whose path starts with that tenant's schema, and `FROM CURRENT` keeps that path for the routine.
`build` warns about each template routine that sets no `search_path`. If your application always sets `search_path` to the tenant's schema first, you may
leave it out and ignore the warning.

You can also write the path out, naming the stand-in where the tenant's schema goes: `SET search_path TO tenant, platform, public` (or
`'tenant', 'platform', 'public'`) is, for each tenant, its own schema, then `platform` and `public`. That is how `init` writes a function it scaffolds.

### What the template cannot hold

The template is applied once per tenant, so it holds only what belongs in a tenant's schema. `build` reports, as an error:

| In the template | Why | Instead |
|---|---|---|
| An object created or changed in a shared schema (`CREATE TABLE platform.audit`, `GRANT ... ON platform.currencies`) | It would be created once per tenant | Put it in the shared project |
| The tenant's own object written with a schema (`CREATE TABLE tenant.extra`, `tenant_acme.extra`) | What is deployed is the text you wrote, so the schema would not follow the tenant | Write it without a schema |
| A reference to one tenant's schema (`SELECT ... FROM tenant_acme.orders`) | The template is the same for every tenant | Refer to the tenant's own objects without a schema |
| Something that belongs to the whole database: an extension, an event trigger, a cast, a publication, a role membership (`GRANT role TO role`), `ALTER DEFAULT PRIVILEGES` without `IN SCHEMA` | It is not in a schema | Put it in the shared project |
| A statement about another schema (`GRANT USAGE ON SCHEMA platform`, `CREATE SCHEMA other`) | It is not the tenant's schema | Put it in the shared project |

And the shared project cannot reach into the tenants: an object in, or a reference to, the stand-in schema or a schema named `<prefix>_...` is an error
there, since the tenants' objects are the template's and differ by tenant.

## Where the tenants come from

| | The prefix alone | A query | A CSV file |
|---|---|---|---|
| In `pgproj.json` | `"prefix": "tenant"` | `"prefix"` and `"query"` | `"prefix"` and `"file"` |
| The list of tenants | the database's schemas named `tenant_...` | the identifiers the query returns | the identifiers the file lists |
| A new tenant's schema is created by (once `publish` handles tenants) | your application | pgproj | pgproj |
| Checked by `build` | the prefix | the prefix (the query needs the database) | the prefix and the whole file |

### The prefix alone

Every schema in the database whose name starts with `tenant_` is a tenant: `tenant_acme` is the tenant `acme`. pgproj will not create tenant schemas in
this mode: your application runs `CREATE SCHEMA tenant_newco` when a customer signs up, and `publish`, once it handles tenants, fills it with the template.

The prefix is matched exactly, with no wildcards: `tenant_%` in `LIKE` would also match `tenantx`, and the prefix does not.

### A CSV file of tenants

```csv
tenant,plan
acme,gold
globex,silver
"initech, inc",bronze
```

- The **first line is a header**, and the column named `tenant` (in any case) holds each tenant's identifier. Other columns are ignored, so the file can
  carry what your team keeps about each tenant.
- An identifier is taken **exactly as written**, case included: `Acme` is the schema `tenant_Acme`, which PostgreSQL keeps distinct from `tenant_acme`.
- Fields follow the usual CSV rules: a field in double quotes may hold commas, line breaks, and doubled quotes (`"say ""hi"""`). The file is UTF-8; a
  byte-order mark and blank lines are fine.
- The path is relative to `pgproj.json`. The file can live outside the source folder, or outside the repository, if you don't want each new tenant to be a commit.

`build` reads the file and reports every problem with its line: a missing `tenant` column, an empty identifier, one listed twice, one that starts or ends
with a space (it would be part of the schema's name), and one whose schema name would pass PostgreSQL's limit of 63 bytes (counted in UTF-8, so `é` is
two), which PostgreSQL would cut short, putting two tenants in one schema.

### A query

```json
"tenants": {
  "template": "./sql/Tenant",
  "prefix": "tenant",
  "query": "SELECT slug FROM platform.tenants WHERE active"
}
```

The query returns one column of identifiers; `slug = 'acme'` is the schema `tenant_acme`. Use it when your application already keeps a table of tenants.
`build` does not run it (it needs the database).

## What `build` prints

```
Loaded project: shop
Source path: C:\work\shop\sql
SQL files: 1
Tenant template: C:\work\shop\sql\Tenant (3 SQL file(s)), checked in the stand-in schema tenant
Tenants: 3 listed in C:\work\shop\tenants.csv, each in schema tenant_<identifier>
```

`--erd` and `pgproj document` draw and describe the template once, in the stand-in schema (`tenant.orders`), with the shared project.

## Comparing: `diff`

`diff` compares a project with tenants in parts, so each part reads like a `diff` of its own:

1. **The shared schemas, once.** Every schema of the database except the tenants' (`<prefix>_...`), one named as the stand-in, and PostgreSQL's own;
   or only the project's `schemas`, when it sets them. A tenant's objects are never compared, or proposed for removal, as shared ones.
2. **Each tenant, on its own.** The template is read for the tenant's schema (as `build` reads it for the stand-in) and compared with that schema
   alone, PostgreSQL rendering views, defaults, and the rest with the tenant's schema first on the search path, as it will when the tenant is deployed.

```
$ pgproj diff
Shared schemas:
Added (3):
  + Table platform.plans
  + Column platform.plans.code
  + Constraint platform.plans.plans_pkey

Tenants: 4, their objects shown in the template's stand-in schema, tenant.
2 tenant(s): tenant_acme, tenant_globex
Added (1):
  + Column tenant.customers.email
Modified (1):
  ~ Table tenant.customers
      Columns: 'id, name' -> 'email, id, name'
1 tenant(s), changed differently from the tenants above: tenant_initech
Added (1):
  + Column tenant.customers.email
Removed (1):
  - Index tenant.orders_status_idx
Modified (2):
  ~ Table tenant.customers
      Columns: 'id, name' -> 'email, id, name'
  ~ Table tenant.orders
      Indexes: 'orders_customer_id_idx, orders_status_idx' -> 'orders_customer_id_idx'
1 new tenant(s), with no schema yet; publish creates each with the template's 20 object(s): tenant_newco
Added: 26
Removed: 1
Modified: 4
tenant_old is no longer a tenant: the tenant list does not include it. pgproj leaves the schema and everything in it as they are, and never drops
a tenant's schema. If it is no longer needed, drop it by other means (for example DROP SCHEMA tenant_old CASCADE).
```

Here the project added a table, `platform.plans`, to the shared schema and an `email` column to the template's `customers`; the tenants listed are
`acme`, `globex`, `initech`, and `newco`, and the database also has `tenant_old`.

How to read it:

- **Tenants with the same changes are listed once,** the largest group first. Their objects are shown in the **stand-in schema** (`tenant.orders`), so
  the changes read the way the template is written, and they are the same for every tenant in the group. A group after the first is a set of tenants
  whose schemas have drifted from the others: above, someone added an index to `tenant_initech.orders` by hand, so `tenant_initech` needs the column
  and to lose the index.
- **A new tenant** is one the query or the file lists whose schema the database does not have yet. It is counted, not listed object by object.
- **Up to date** names the tenants with nothing to change.
- **A former tenant** is a schema named as a tenant's (`tenant_old`) that the query or the file no longer lists. pgproj never drops it, or anything in
  it, and says so in a warning. With the prefix alone there are no former tenants: a tenant is gone when its schema is.
- **The totals** at the end (`Added`, `Removed`, `Modified`) count every tenant: three tenants that each need the `email` column count as three
  added columns.
- With `--exit-code`, any change, in the shared schemas or in any tenant, new tenants included, exits with `2`.

**`--tenants acme,globex`** compares only those tenants, by identifier (not `tenant_acme`), as a comma-separated list or the option repeated; each must
be one the project already has. The shared schemas are compared either way. Use it to look at a few tenants of many.

**A query that cannot run yet.** The tenant query usually reads a table of the shared project, which a new database does not have. `diff` then
compares the shared schemas, says `Tenants: not read`, and warns that the query could not run; the tenants are read once the shared project is published.

**`--format json`** writes one document for CI, the same envelope as for any project (`schemaVersion`, `status`, `project`, `documents`, `diagnostics`, `counts`): `added`, `removed`, and `modified` (the shared changes; before the engine they were under `shared`), `standInSchema`,
`tenantGroups` (each with `tenants` and the group's changes, in the stand-in schema), `newTenants` (each `schema`, `identifier`, and `objects`), `upToDate`,
`formerTenants`, `tenantsNotRead` (when the query could not run), `bodyNotes`, `undroppedSchemas`, and `summary` (the totals). It names every tenant; the text form names
ten in a line and says how many more.

## Deploying: `publish`

`publish` deploys a project with tenants in parts, each **a transaction of its own**:

1. **The shared changes**, with the project's `predeploy` and `postdeploy` scripts, which run once, here.
2. **Each tenant**, one after another, in the order of their schemas' names. A tenant's script starts with `SET search_path TO` the tenant's schema and
   then `searchPath`, so the template's unqualified names are deployed exactly as `build` resolved them. Where the template names the stand-in schema
   (`GRANT USAGE ON SCHEMA tenant`), the tenant's schema is used. The tenant's own scripts, if the project has them, run in the same transaction (see
   [a tenant's own scripts](#a-tenants-own-scripts)).

```
$ pgproj publish --execute
Published the shared schemas.
Published tenant_acme.
Published tenant_globex.
Published tenant_initech.
Published tenant_newco.
Published: the shared schemas and 4 tenant(s), 4 of them with changes.
```

**A new tenant.** With a query or a file, a tenant listed whose schema does not exist yet gets it: its transaction starts with `CREATE SCHEMA IF NOT EXISTS`
and builds the whole template in it. With the prefix alone, your application creates the schema (`CREATE SCHEMA tenant_newco`), and the next `publish`
fills it. The new schema is owned by the role that runs `publish`, unless the template states an owner (`ALTER SCHEMA tenant OWNER TO app_owner`).

**Everything is checked before anything runs.** The checks a `publish` makes on one script are made on every part first: what can discard data (without
`--allow-destructive`), a placeholder pgproj cannot run, what the server is too old for, a missing tablespace or role, statements only a superuser can run,
and whether each part's transaction fits the server's lock table. If any part fails a check, nothing runs, not even the shared part, and the message names
the part:

```
Publish failed: 2 operation(s) can discard data, in 2 part(s); nothing was run.
  tenant_acme: [Table] tenant_acme.customers: column "name" removed
  tenant_globex: [Table] tenant_globex.customers: column "name" removed
Use --output/--dry-run to review the generated script, and pass --allow-destructive to run it anyway.
```

**When a tenant fails.** The run stops there. What was published stays published; the failing tenant's transaction is rolled back; the tenants after it are
not started. The output says which is which:

```
Published tenant_acme.
Publish failed in tenant_globex: 23502: column "phone" of relation "customers" contains null values
  The statement that failed: ALTER TABLE "tenant_globex"."customers" ADD COLUMN "phone" text NOT NULL
  Nothing was applied: the script was rolled back.
Published before tenant_globex, and kept: the shared schemas, tenant_acme.
Not started: tenant_initech, tenant_newco.
Fix the problem and run publish again: what was published has nothing left to do.
```

Until you publish again, the tenants are on different versions of the template. Fix what failed (above, a row with no `phone`, or give the column a default)
and run `publish` again: the published tenants have nothing to do, and the rest are published.

**A tenant query that reads a shared table.** On a new database, the table the query reads does not exist until the shared changes create it. `publish
--execute` then publishes the shared changes first (a `postdeploy` script can fill the table), reads the tenants, checks them, and publishes them:

```
Published the shared schemas.
Read the tenants after publishing the shared changes: 2.
Published tenant_acme.
Published tenant_globex.
```

In that case alone the tenants are checked after the shared changes are published, so a tenant that fails a check leaves the shared changes published.
`--dry-run` and `--output` show the shared part and warn that the tenants are read once it is published.

**`--tenants acme,globex`** publishes only those tenants (identifiers, not schema names); the shared changes are published either way. Use it to try a
change on a few tenants first, or to publish one new tenant.

**`--concurrently`** works per tenant: after a tenant's transaction commits, its new indexes are built concurrently and its new constraints validated, then
the next tenant starts.

**A printed or written script** (`--dry-run`, `--output`) has the shared part, then each tenant's part after a line naming it:

```sql
SET search_path TO platform, public;

CREATE TABLE "platform"."plans" ( ... );

-- pgproj: tenant tenant_acme (a transaction of its own)
SET search_path TO tenant_acme, platform, public;

CREATE SCHEMA IF NOT EXISTS "tenant_acme";

ALTER TABLE "tenant_acme"."customers" ADD COLUMN "email" text;
```

It is not wrapped in transactions, and `publish` warns so: `--execute` runs each part in its own; if you run the script with `psql`, wrap each part in
`BEGIN; ... COMMIT;`.

**A former tenant** (no longer listed by the query or the file) is never dropped, nor anything in it; `publish` warns, as `diff` does, that it must be
dropped by other means.

**How long it takes.** Each tenant is read, compared, and deployed on its own, so a run's time grows with the number of tenants. With the template on this
page, on a local PostgreSQL 18, 100 tenants took about 14 seconds to compare, to publish a new column to, or to find up to date: about 0.14 seconds a tenant.

## A tenant's own scripts

The template says what a tenant's schema holds. Some work is data, not schema: seeding a new tenant, recording that it was updated, fixing a row.
`tenants.predeploy` and `tenants.postdeploy` are scripts for that, run once per tenant, in the tenant's own transaction:

```json
"tenants": {
  "template": "./sql/Tenant",
  "prefix": "tenant",
  "file": "./tenants.csv",
  "postdeploy": "./scripts/tenant/post.sql"
}
```

The paths are relative to `pgproj.json`, and a script may include others with `:r` lines, as the project's scripts can. The files are never read as
schema, wherever they are.

### What runs, in what order

Each tenant's transaction runs, in this order:

1. `SET search_path TO` the tenant's schema, then `searchPath`.
2. For a new tenant, `CREATE SCHEMA` for its schema, so the scripts can use it.
3. `SET LOCAL pgproj.tenant_schema` and `pgproj.tenant_id` to the tenant's values (see below).
4. The tenant's `predeploy` script.
5. The template's changes.
6. The tenant's `postdeploy` script.

So for a new tenant, `predeploy` runs once its schema exists but before any of the template's tables, types, or functions are in it; a script that seeds a
new tenant's data (rows in the template's tables) belongs in `postdeploy`, marked `-- pgproj: run when=tenant-created`. If anything in the transaction
fails, a new tenant's schema and objects are rolled back with it, so a tenant is never left half created.

With `--concurrently`, the tenant's indexes are built after that transaction commits. The project's own `predeploy` and `postdeploy` run once, with
the shared changes, before any tenant; they do not run for each tenant.

A tenant's scripts run on **every publish**, for **every tenant**, even one with nothing else to change, unless a file says otherwise with a marker
(below). So write them to be safe to run again (`INSERT ... ON CONFLICT DO NOTHING`, `UPDATE ... WHERE`). If a script fails, that tenant's whole
transaction is rolled back, its changes included, and the run stops there, as for any failure. `--comments-only` runs no script.

### Naming the tenant: variables

A script names the tenant's schema and identifier with psql's variable syntax, so the same file runs in psql too:

| Write | Is | For `acme` |
|---|---|---|
| `:"tenant_schema"` | the tenant's schema, as a quoted name | `"tenant_acme"` |
| `:'tenant_schema'` | the tenant's schema, as a string | `'tenant_acme'` |
| `:'tenant_id'` | the tenant's identifier, as a string | `'acme'` |

```sql
-- scripts/tenant/seed.sql
INSERT INTO :"tenant_schema".customer_groups (name) VALUES ('default') ON CONFLICT DO NOTHING;
INSERT INTO platform.audit (tenant, note) VALUES (:'tenant_id', 'seeded');
```

To try a script on one tenant by hand: `psql -v tenant_schema=tenant_acme -v tenant_id=acme -f scripts/tenant/seed.sql`.

The variables are filled in only where SQL has them: not inside a string (`':"tenant_schema"'`), a comment, or a dollar-quoted body (`$$ ... $$`), as
psql does. Inside a function or a `DO` block, use the settings pgproj sets for the transaction instead:

```sql
DO $$
BEGIN
    EXECUTE format('ANALYZE %I.orders', current_setting('pgproj.tenant_schema'));
END $$;
```

`current_setting('pgproj.tenant_id')` is the identifier, and `current_schema()` is the tenant's schema too, since it is first on the path.

### Names written without a schema

Because the tenant's schema is first on the transaction's `search_path`, a name written without a schema (`INSERT INTO orders ...`) is the tenant's
`orders` when it has one, and otherwise the first schema of `searchPath` that has one. This is PostgreSQL's own rule, the same one the template relies
on, so it works. But a name that both the tenant and a shared schema have is the tenant's, without a word, and a reader cannot tell which you meant; so
write `:"tenant_schema".orders` for the tenant's objects, and the schema for shared ones (`platform.audit`), when a script could be read either way.

### Markers: when a file runs

A file can say when it runs, with a comment among the comment lines at its top, before its first statement:

```sql
-- Seeds a new tenant.
-- pgproj: run when=tenant-created
INSERT INTO :"tenant_schema".customer_groups (name) VALUES ('default');
```

| Marker | The file runs |
|---|---|
| none | on every publish, for every tenant |
| `-- pgproj: run when=tenant-changed` | for a tenant that has changes in this publish |
| `-- pgproj: run when=tenant-created` | for a tenant whose schema pgproj creates in this publish, or, if the schema exists, that has no tables, views, sequences, types, or routines yet: the first time pgproj fills it. With the prefix alone, where your application creates the schema, this is the publish that first fills it. |

The project's own `predeploy` and `postdeploy` files take one marker: `-- pgproj: run when=changed` runs the file only when the shared schemas (or, in a
project without tenants, the database) have changes in this publish. A file without a marker runs on every publish, as before.

A marker belongs to its file. A script made of `:r` includes can give each file its own:

```sql
-- scripts/tenant/post.sql
:r seed.sql        -- marked when=tenant-created
:r audit.sql       -- marked when=tenant-changed
:r touch.sql       -- no marker: every publish
```

A file that does not run is left out with everything it includes, and the script says so where it would have been:
`-- pgproj: skipped seed.sql (it runs when=tenant-created)`. `--dry-run` and `--output` show each tenant's scripts that way, with its values filled in.
A tenant with nothing left to run in its scripts and nothing to change runs nothing.

Whether a tenant has changes, or is new, is decided by the tenant's own state in each run, and its scripts commit or roll back with it, so a tenant that a
run did not reach gets its `tenant-changed` and `tenant-created` files the next time. There is no marker for "the shared schemas changed": a run that
stopped partway would publish the shared changes, and the next run, having none, would not run such a file for the tenants it had not reached. A script
that must follow a shared change should run on every publish and do nothing when there is nothing to do.

### What `build` reports

`build` checks a tenant's scripts with the stand-in's values (`:"tenant_schema"` as `"tenant"`), syntax included, and reports, naming the file:

- a marker a script does not take: an unknown value, a tenant marker in the project's own script, or `when=changed` in a tenant's;
- two markers in one file;
- a variable other than `tenant_schema` and `tenant_id`;
- a variable in the project's own scripts, which run once and have no tenant.

## Troubleshooting

| `build` says | What to do |
|---|---|
| `Tenant template: Table platform.audit is in the shared schema 'platform'` | Move the statement to the shared project. |
| `Tenant template: Table tenant.extra is written with the schema 'tenant'` | Remove the schema: `CREATE TABLE extra`. |
| `... refers to an object written with the schema 'tenant', the stand-in` | Refer to the tenant's object without a schema: `orders`, not `tenant.orders`. |
| `... refers to an object in 'tenant_acme', which is one tenant's schema` | The template cannot single out a tenant. Refer to the tenant's own object without a schema. |
| `A grant on schema platform: the tenant template can name only its own schema` | A statement about a shared schema belongs in the shared project; one about the tenant's schema names the prefix. |
| `... routine(s) ... do not set search_path` (a warning) | Add `SET search_path FROM CURRENT` to each, or make sure every session sets `search_path` to the tenant's schema first. |
| `View 'public.counts' references the relation 'order_labels' without a schema, and it is not on the search path` | A shared object cannot use a tenant's object: there is one per tenant. |
| `tenants.csv:4: the tenant 'acme' is listed again (first on line 2)` | Remove the repeated line. |
| `'schemas' lists 'tenant_acme', which is named as a tenant's schema is` | Leave tenant schemas out of `schemas`; pgproj finds them from the prefix. |
| `diff`: `Tenants: not read`, and `The tenant query could not run (42P01: ...)` | The table the query reads is not in the database yet. Publish the shared project first. |
| `diff`: `--tenants names 'tenant_acme', which the tenant file ... does not list` | `--tenants` takes identifiers: `--tenants acme`. |
| `diff`: `The tenant query returns 2 columns; it must return one column` | Select only the identifier: `SELECT slug FROM platform.tenants WHERE active`. |
| `diff`: `tenant_old is no longer a tenant` (a warning) | The query or the file stopped listing it. Drop the schema yourself if it is no longer needed, or list it again. |
| `diff`: a tenant in a group of its own, `changed differently from the tenants above` | Its schema was changed by hand. `publish` brings it back to the template (see the changes listed for it). |
| `init`: `The database has no schema named customer_<identifier>` | Check `--tenant-prefix`: the tenants' schemas must be named `<prefix>_<identifier>`. |
| `init`: `These template files have a function body that still names tenant_acme` (a warning) | Change the body to refer to the tenant's own objects without a schema. |
| `init`: `N tenant(s) differ from the template` | `pgproj diff` shows how. Change the template, or let `publish` bring those tenants to it. |
| `build`: `'-- pgproj: run when=changed' is not a marker a tenant's postdeploy script takes` | A tenant's file takes `when=tenant-changed` or `when=tenant-created`. |
| `build`: `A tenant's script uses :'tenant_name', which pgproj does not set` | The variables are `tenant_schema` and `tenant_id`. |
| `build`: `The project's deploy script uses :'tenant_id'` | Move that statement to `tenants.predeploy` or `tenants.postdeploy`. |
| `publish`: every tenant `ran only their scripts` | A tenant script without a marker runs on every publish; mark it `when=tenant-changed` if it should run only with changes. |
| `publish`: `Publish failed in tenant_globex: ...` and `Not started: ...` | Fix what the error names, then run `publish` again: it finishes the rest. |
| `publish`: `... can discard data, in 2 part(s); nothing was run.` | Review with `--dry-run`; add `--allow-destructive` if dropping it is what you want. Nothing was published, not even the shared part. |
| `publish`: `The tenant query could not run ... Nothing in the shared schemas creates it, so no tenant was published.` | The query reads a table that neither the database nor the shared project has. Add the table to the shared project, or fix the query. |

## Limits

- `init` scaffolds the template from one tenant: what only other tenants have is not in it (it shows as their differences). It does not run a
  `--tenant-query`, and a tenant's schema name with characters a file name cannot hold is not supported.
- `diff` and `publish` read, compare, and deploy each tenant on its own, one after another (not in parallel), so their time grows with the number of tenants.
- `publish` stops at the first tenant that fails; there is no option to go on with the others.
- A tenant's scripts run in each tenant's transaction, one tenant after another, so a script that runs on every publish adds to every run's time.
- A marker is read only among the comment lines at the top of its file.
- A lock warning is printed for each tenant it applies to, so with many tenants the same warning repeats.
- With the prefix alone, any schema named `<prefix>_...` is a tenant, even one made for something else: choose a prefix nothing else uses.
- `--tenants` cannot name an identifier that contains a comma.
- A table that a `LIKE` clause copies and the project does not declare is not read from the database for a project with tenants.
- Only names the parse tree shows qualified are checked: a schema written inside a PL/pgSQL or other string body is not read, as elsewhere in pgproj.
- In the diagram, a type in the stand-in schema is written `tenant_order_status` (the diagram joins schema and name with `_`), which reads like a tenant's
  schema; it is the template's `order_status`.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
