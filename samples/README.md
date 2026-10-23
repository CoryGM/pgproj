# Samples

Complete pgproj projects to read, build, and publish into a scratch database.

| Sample | Shows |
|---|---|
| [`MultiSchema`](MultiSchema/README.md) | An ordinary project: an online shop over seven schemas, with tables, types, functions, procedures, triggers, views, materialized views, extensions, and a post-deploy script that seeds data. |
| [`SchemaPerTenant`](SchemaPerTenant/README.md) | A database with a schema per tenant: a shared schema, a template every tenant's schema holds, the tenants listed in a CSV file, and per-tenant deploy scripts ([Schema-per-tenant databases](../docs/notes/tenants.md)). |

Each is built by the test suite, so it stays in step with pgproj.
