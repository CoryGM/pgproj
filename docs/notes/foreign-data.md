# Foreign data wrappers, servers, user mappings, and foreign tables

Foreign data wrappers, foreign servers, user mappings, and foreign tables are modeled: deployed, read back from the database, compared, and written by `init`. A wrapper usually
comes with an extension, and a server and its mappings are the project's:

```sql
CREATE EXTENSION postgres_fdw;
CREATE SERVER warehouse FOREIGN DATA WRAPPER postgres_fdw OPTIONS (host :'warehouse_host', dbname 'warehouse');
COMMENT ON SERVER warehouse IS 'the reporting warehouse';
GRANT USAGE ON FOREIGN SERVER warehouse TO reporting;
CREATE USER MAPPING FOR reporting SERVER warehouse OPTIONS (user 'reader', password :'warehouse_password');
```

None of the first three is in a schema, so `schemas` in `pgproj.json` does not limit them. A foreign table is in a schema, as a table is ([below](#foreign-tables)).

## Secrets and per-environment values

An option of a server or a user mapping can name its value with a variable, `:'name'`, given at publish time in `PGPROJ_VAR_<name>`, as the deploy scripts do
([variables](variables.md)). Use one for a password, and for a value that differs between environments, such as a host. A variable anywhere else in the
project's SQL is an error.

- **A value given by a variable is never compared**, nor is a `password`, however it is written: `diff` sees only whether the option is there. So a server
  whose host differs in each environment is no difference.
- **A secret is sent when its mapping is created**, and not after: a later `publish` needs no value for a mapping that exists. `--execute` fills it in; a
  printed or written script keeps `:'name'`, so it runs with `psql -v name=... -f`.
- **To send a changed secret again**, run `publish --execute --apply-secrets` (every variable the mappings name) or `--apply-secrets warehouse_password` (only
  those listed). Each is an `ALTER USER MAPPING ... OPTIONS (SET password :'name')`.
- **A password written as a literal** is deployed and never compared, and `build` warns that it belongs in a variable: anyone who can read the project can read it.
- A password is never read from a database, and never shown by `diff` or in a message.

The server may log a statement with its values (`log_statement = 'ddl'` or `'all'`), as for any secret a script sends.

## What `publish` does

- **Order:** a wrapper (or the extension that installs it), then a server, then its user mappings, then the grants on them; dropped in reverse.
- **A wrapper** needs a superuser to be created, changed, or dropped, so `publish --execute` runs that only with `--allow-superuser-statements`
  ([superuser statements](superuser-statements.md)). Its handler, validator, and options change in place. A wrapper an extension installs is the extension's.
- **A server's** version and options change in place (`ALTER SERVER`); a `VERSION` is compared only when the project states one. A changed wrapper or `TYPE`
  drops the server and creates it again, its user mappings with it, which sends their secrets again.
- **A user mapping's** options change in place (`ALTER USER MAPPING`). `FOR CURRENT_USER` is the role that publishes. The role a mapping is for must exist, or
  `publish` refuses before anything runs, as for a grant.
- **Dropping a server or a user mapping** stops every connection through it, so it needs `--allow-destructive`. One dropped only to be created again does not.
- Grants (`GRANT USAGE ON FOREIGN DATA WRAPPER|FOREIGN SERVER`), owners (`ALTER SERVER ... OWNER TO`; a wrapper's owner must be a superuser), and comments
  are compared as for other objects.

The `ALTER FOREIGN DATA WRAPPER`, `ALTER SERVER`, and `ALTER USER MAPPING` forms in a project are reported and ignored: a project states each one's final
form in its `CREATE`.

## Foreign tables

A foreign table is a table whose rows are on the server it names. It is written as PostgreSQL takes it, with its options and its columns' options:

```sql
CREATE FOREIGN TABLE reporting.orders (
    id integer OPTIONS (column_name 'order_id') NOT NULL,
    amount numeric(10,2),
    CONSTRAINT positive CHECK (id > 0)
) SERVER warehouse OPTIONS (schema_name 'sales', table_name 'orders');
COMMENT ON FOREIGN TABLE reporting.orders IS 'orders in the warehouse';
GRANT SELECT ON reporting.orders TO reporting;
```

The forms `pg_dump` writes after it are read onto it too: `ALTER FOREIGN TABLE ... ALTER COLUMN ... OPTIONS (...)`, `... OWNER TO`, and
`COMMENT ON FOREIGN TABLE`. Its other `ALTER FOREIGN TABLE` actions are read as a table's ([`ALTER TABLE`](alter-table.md)); one pgproj does not read, such
as `OPTIONS` on the table itself, is a warning. A foreign table can be a partition (`CREATE FOREIGN TABLE ... PARTITION OF parent FOR VALUES ... SERVER s`)
and can have `CHECK` and `NOT NULL` constraints, defaults, triggers, extended statistics, grants, an owner, and comments.
PostgreSQL refuses an index and any other constraint on one, and `build` reports them as errors. `IMPORT FOREIGN SCHEMA` is still a warning and ignored:
a project states each foreign table it wants.

- **Its options, and its columns' options,** are compared by name in no order and change in place (`ALTER FOREIGN TABLE ... OPTIONS (SET ...)`, and
  `... ALTER COLUMN ... OPTIONS`). A foreign table's option cannot be a variable.
- **A move to another server** (or a table that becomes, or stops being, a foreign table) cannot be done in place: `publish` drops the table and creates it
  again, with its constraints, triggers, statistics, grants, and owner. While anything else uses it (a view, for one) `publish` refuses the
  change, naming what uses it; change those first, or make the change by hand.
- **Dropping a foreign table** stops every query that reads it, so it needs `--allow-destructive`. Its rows are the server's, so dropping or changing a
  column, or a move to another server, loses none and needs no consent. An ordinary table that becomes a foreign one loses its rows and needs it.
- **A server that has to be dropped and created again** (a changed wrapper or `TYPE`) is refused while a foreign table is on it.
- `init` writes each one to `Schema/<schema>/ForeignTable/<name>.sql`, with its triggers, statistics, comments, grants, and owner.
## In the database and in `init`

A role can read a user mapping's options only if it is the mapped role, the server's owner, or a superuser. For any other role they are not compared, and
`init` writes the mapping with a comment saying so.

`init` writes `ForeignDataWrapper/<name>.sql` for a wrapper the database has of its own, and `Server/<name>.sql` for a server with its comment, grants, owner,
and user mappings. It writes each mapping's password as `:'<server>_<role>_password'` and names the variables to set.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
