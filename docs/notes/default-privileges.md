# Default privileges

`ALTER DEFAULT PRIVILEGES` sets the privileges PostgreSQL gives an object when it is created. pgproj deploys it, reads it back, and compares it like a grant:

```sql
ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO reader;
ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO app_user;
```

A rule without `IN SCHEMA` applies in every schema, and one with it adds to that in its schema. `TABLES` covers tables, views, and materialized views;
`SEQUENCES` sequences; `FUNCTIONS` functions, procedures, and aggregates; `TYPES` enum, composite, and range types and domains; `SCHEMAS` schemas. A rule
belongs to the role that creates the objects: `FOR ROLE` names another, and without it the rule is the role that deploys.

## PUBLIC's built-in privileges

PostgreSQL gives `PUBLIC` `USAGE` on every new type and `EXECUTE` on every new function and procedure. Once a rule without `IN SCHEMA` exists on `TYPES`
or `FUNCTIONS`, PostgreSQL stores the kind's whole list, and that entry for `PUBLIC` is in it. pgproj leaves it out when it reads the database, and a project
that writes `ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO PUBLIC` asks for nothing more than the default, so neither is a difference. A rule granting to
`PUBLIC` in one schema, or on tables, sequences, or schemas, is a real rule and is compared.

## What a rule gives an object

PostgreSQL applies a rule only when an object is created: it never changes an object that already exists, and an object created before the rule keeps the
privileges it had. So pgproj treats what a rule may give as **allowed, not required**:

- **An object that has a privilege one of the project's rules gives** (of its kind, globally or in its schema, to the same role with the same grant option) is
  not a difference, although the project writes no `GRANT` for it. A table `publish` creates under the rule gets the privilege from the server, and the next
  `diff` is empty.
- **An object that lacks it** is not a difference either: a rule is not retroactive, and pgproj does not make it so. To give existing objects the privilege
  too, write `GRANT ... ON ALL TABLES IN SCHEMA app TO reader` (or a grant on each object) beside the rule.
- **Anything else is compared as usual:** a privilege no rule gives, one on a column, one to another role, or with another grant option, is revoked when the
  project does not grant it. A rule the project drops no longer covers what it gave, so what it gave is revoked too, unless the project grants it another way;
  both need `--allow-destructive`.

A rule of any role counts, since pgproj cannot tell which role created an object that already exists.

## Order in a publish

`publish` writes a rule after its schema and before the objects it covers, so an object created in the same publish gets what the rule gives, as running the
project's SQL on an empty database would.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
