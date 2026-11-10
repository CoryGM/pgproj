# Foreign data wrappers, servers, and user mappings

Foreign data wrappers, foreign servers, and user mappings are modeled: deployed, read back from the database, compared, and written by `init`. A wrapper usually
comes with an extension, and a server and its mappings are the project's:

```sql
CREATE EXTENSION postgres_fdw;
CREATE SERVER warehouse FOREIGN DATA WRAPPER postgres_fdw OPTIONS (host :'warehouse_host', dbname 'warehouse');
COMMENT ON SERVER warehouse IS 'the reporting warehouse';
GRANT USAGE ON FOREIGN SERVER warehouse TO reporting;
CREATE USER MAPPING FOR reporting SERVER warehouse OPTIONS (user 'reader', password :'warehouse_password');
```

None of them is in a schema, so `schemas` in `pgproj.json` does not limit them. Foreign tables are not modeled yet.

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

## In the database and in `init`

A role can read a user mapping's options only if it is the mapped role, the server's owner, or a superuser. For any other role they are not compared, and
`init` writes the mapping with a comment saying so.

`init` writes `ForeignDataWrapper/<name>.sql` for a wrapper the database has of its own, and `Server/<name>.sql` for a server with its comment, grants, owner,
and user mappings. It writes each mapping's password as `:'<server>_<role>_password'` and names the variables to set.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
