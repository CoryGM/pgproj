# samples/SchemaPerTenant: an online storefront, one schema per shop

A complete pgproj project for a database that keeps each customer's data in a schema of its own. It is a storefront platform: many shops, each
with its own catalog, customers, and orders in a schema named `shop_<slug>` (`shop_acme`, `shop_globex`, ...), and one shared schema, `platform`, that
every shop uses (currencies, countries, plans, the list of shops).

Read it beside [Schema-per-tenant databases](../../docs/notes/tenants.md), which explains each feature this sample uses.

## What is where

```
samples/SchemaPerTenant/
├── pgproj.json                       the project: source, searchPath, the scripts, and the "tenants" block
├── tenants.csv                       the shops: one line each (the "tenant" column is the slug)
├── sql/
│   ├── Schema/platform/              the shared schema, deployed once
│   │   ├── platform.sql              CREATE SCHEMA and its comment
│   │   ├── Table/                    currencies, countries, plans, shops, shop_events
│   │   └── Function/format_money.sql a shared function the template's view calls
│   └── Tenant/                       the template: what every shop's schema holds, written without a schema
│       ├── shop.sql                  statements about the shop's schema itself, naming the stand-in "shop"
│       ├── Type/                     order_status (an enum), money_amount (a domain)
│       ├── Table/                    customer_groups, customers, products, orders, order_lines
│       ├── Function/                 touch_updated_at (a trigger function), open_order_count
│       └── View/order_totals.sql     joins the shop's tables and calls platform.format_money
├── scripts/
│   ├── post.sql                      the project's post-deploy script: once per publish
│   ├── seed-platform.sql             no marker: every publish; idempotent seed of the shared lookups
│   ├── record-shared-change.sql      "run when=changed": only when the shared schemas changed
│   └── tenant/
│       ├── post.sql                  each shop's post-deploy script, in the shop's own transaction
│       ├── seed-new-shop.sql         "run when=tenant-created": customer groups and a counter customer for a new shop
│       └── record-shop-change.sql    "run when=tenant-changed": a row in platform.shop_events when a shop's schema changed
└── docs/                             what pgproj document and build --erd write for this project
```

What each part shows:

| Feature | Where |
|---|---|
| The template's own objects written without a schema, so each shop gets its own | every file in `sql/Tenant` |
| A foreign key from a shop's table to a shared one | `customers.country`, `products.currency`, `orders.currency` reference `platform` |
| A shared function called from the template, found through `searchPath` | `View/order_totals.sql` calls `platform.format_money` |
| A trigger and functions that keep the shop's schema first with `SET search_path FROM CURRENT` | `Function/touch_updated_at.sql`, `Function/open_order_count.sql` |
| Statements about the shop's schema naming the stand-in `shop` | `shop.sql`: its comment, `GRANT USAGE ON SCHEMA shop`, `ALTER DEFAULT PRIVILEGES IN SCHEMA shop` |
| The tenant list as a file, with columns of your own beside `tenant` | `tenants.csv` |
| psql's variables in a tenant script | `:"tenant_schema"` and `:'tenant_id'` in `scripts/tenant/*.sql` |
| Every kind of marker | `when=tenant-created`, `when=tenant-changed`, `when=changed`, and none |

## Running it

You need a PostgreSQL server (14 to 18) and a role that can create schemas. The commands run from this folder; pass your connection with
`--target-connection`, or put it in `pgproj.json` in place of `<password>`.

```sh
createdb storefront
C="Host=localhost;Database=storefront;Username=postgres;Password=..."
```

**1. Check the project.** No database needed.

```sh
pgproj build
```

It says the template has 11 files, checked in the stand-in schema `shop`, and that 3 tenants are listed in `tenants.csv`.

**2. Look at what would run.**

```sh
pgproj publish --dry-run --target-connection "$C"
```

The script has the shared part, then a part per shop under a line `-- pgproj: tenant shop_acme (a transaction of its own)`. Each shop's part starts
with `SET search_path TO shop_acme, platform, public;`, creates the schema, sets `pgproj.tenant_schema` and `pgproj.tenant_id`, builds the template, and
ends with the shop's scripts, `:"tenant_schema"` already filled in.

**3. Publish.**

```sh
pgproj publish --execute --target-connection "$C"
```

```
Published the shared schemas.
Published shop_acme.
Published shop_globex.
Published shop_initech.
Published: the shared schemas and 3 tenant(s), 3 of them with changes.
```

Each shop now has its tables, two customer groups, and a "Counter sale" customer, and `platform.shop_events` records what happened:

```sql
SELECT shop, event FROM platform.shop_events ORDER BY id;
--  *        | shared schemas changed
--  acme     | created
--  acme     | schema updated in shop_acme
--  ...
```

**4. Publish again.** Nothing has changed, so the marked scripts are skipped. Only `seed-platform.sql`, which has no marker, runs again, in the
shared part, and its `ON CONFLICT DO NOTHING` makes it change nothing:

```
Published the shared schemas.
Published: the shared schemas (no changes) and 3 tenant(s), 0 of them with changes.
```

`pgproj diff --target-connection "$C"` lists the shared schemas and the three shops as up to date.

**5. Add a shop.** Add a line to `tenants.csv`, `umbrella,Umbrella Pharmacy,pro`, and publish. Only the new shop changes: pgproj creates `shop_umbrella`,
builds the template in it, and its `tenant-created` script seeds it.

**6. Change the template.** Add a column, say `phone text` to `sql/Tenant/Table/customers.sql`. `pgproj diff` shows the change once, for all the shops
together (`+ Column shop.customers.phone`, in the stand-in schema).

**7. See drift.** Before publishing, change one shop by hand: `CREATE INDEX ON shop_globex.products (sku, active);`. `pgproj diff` now puts `shop_globex`
in a group of its own, "changed differently from the tenants above", with the new column and the index to remove. `pgproj publish --execute` brings every
shop to the template, each in its own transaction: the column added to each, the index dropped from `shop_globex`, and a `schema updated` event for each.

**8. Try the other ways of listing shops.** The shops are also in `platform.shops`. Replace `"file": "./tenants.csv"` in `pgproj.json` with
`"query": "SELECT slug FROM platform.shops WHERE active"`, and the database's table becomes the list (pgproj still creates a listed shop's schema). The
seed script lists only the first three shops, so after step 5 `diff` says `shop_umbrella is no longer a tenant` (and leaves its schema alone) until you add
its row: `INSERT INTO platform.shops (slug, display_name, plan) VALUES ('umbrella', 'Umbrella Pharmacy', 'pro');`. Or remove both `file` and `query`, and
the shops are simply the schemas named `shop_...` that the database has.

To start over: `dropdb storefront`.

## The generated documentation

`docs/` holds what `pgproj document --format html,markdown,json --output docs` and `pgproj build --erd docs/erd --erd-html` write for this project. The
template is described once, in the stand-in schema `shop`, beside the shared `platform` tables.
