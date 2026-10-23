# samples/MultiSchema: a shop with accounts, a catalog, inventory, sales, and billing

A hand-written pgproj project for an online shop that also sells subscriptions. It spreads its objects over seven schemas, the way
a real application often does, and shows the parts of a project most databases need:

- **Tables** with primary, unique, and foreign keys across schemas, partial unique indexes, and defaults that call project
  functions (`public.uuid_generate_v7()`, `public.utc_now()`).
- **Types:** enums (`billing.invoice_status`, `sales.address_kind`, ...) and a domain (`public.money_amount`).
- **Functions, procedures, and triggers** in SQL and PL/pgSQL (`inventory.apply_stock_movement`, `billing.run_billing_cycle`,
  `catalog.record_price_history`).
- **Views and materialized views** (`catalog.v_category_tree`, `reporting.mv_daily_sales`, ...).
- **Sequences** and **extensions** (`citext`, `uuid-ossp`).
- **A post-deploy script** that seeds sample data through `:r` includes and refreshes the materialized views.

```
samples/MultiSchema/
  pgproj.json          the project file; "source" is this folder
  Extension/           CREATE EXTENSION statements
  Schema/<schema>/     one folder per schema, one file per object, in the layout `pgproj init` writes
  Scripts/             not read as schema: postdeploy.sql and the Seed/ files it includes
  docs/                written by `pgproj document` and `pgproj build --erd`
```

| Schema | Holds |
|---|---|
| `identity` | principals, users, roles, and resources (who did what) |
| `accounts` | the accounts that own customers, subscriptions, and invoices |
| `catalog` | categories, products, and their price history |
| `inventory` | warehouses, stock levels, and stock movements |
| `sales` | customers, addresses, discount codes, orders with their lines and status history, and payments |
| `billing` | plans, subscriptions, usage, invoices, and invoice lines |
| `reporting` | materialized views over the rest, and the procedure that refreshes them |

## Trying it

From the repository root, with a PostgreSQL 14 to 18 server and an empty database to publish into:

```sh
pgproj build   --project samples/MultiSchema/pgproj.json
pgproj publish --project samples/MultiSchema/pgproj.json --target-connection "Host=localhost;Database=shop;Username=postgres;Password=secret" --dry-run
pgproj publish --project samples/MultiSchema/pgproj.json --target-connection "Host=localhost;Database=shop;Username=postgres;Password=secret" --execute
pgproj diff    --project samples/MultiSchema/pgproj.json --target-connection "Host=localhost;Database=shop;Username=postgres;Password=secret"
```

The first `publish --execute` creates everything and runs the seed; the `diff` after it finds nothing to change. Change a file
under `Schema/` and run `diff` and `publish` again to see how a change is deployed. The `targetConnectionString` in `pgproj.json`
has its password masked: pass `--target-connection` (or set `PGPROJ_TARGET_CONNECTION`) as above.

For a database that keeps one schema per tenant, see [`samples/SchemaPerTenant`](../SchemaPerTenant/README.md).
