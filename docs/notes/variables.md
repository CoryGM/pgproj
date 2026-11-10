# Variables in the deploy scripts: secrets and values per environment

A pre- or post-deploy script can name a value instead of writing it, so the value stays out of the project's files: a password, or a setting that differs
between development, test, and production. It uses psql's variable syntax, so the same script also runs with psql:

```sql
-- sql/Scripts/post.sql
ALTER ROLE app_user PASSWORD :'app_password';
GRANT SELECT ON ALL TABLES IN SCHEMA app TO :"reporting_role";
```

- `:'name'` is the value as a quoted string (`'...'`), and `:"name"` as a quoted identifier (`"..."`).
- A name is letters, digits, and `_`.
- Only SQL is read: nothing inside a string, a comment, or a dollar-quoted body (`$$ ... $$`) is a variable, as in psql. Inside a `DO` block or a function
  body, put the value in a setting first (`SELECT set_config('app.region', :'region', true);`) and read it with `current_setting('app.region')`.

Variables work in the project's own scripts (`predeploy`, `postdeploy`) and in a tenant's (`tenants.predeploy`, `tenants.postdeploy`). `tenant_schema` and
`tenant_id` are the tenant's, which pgproj sets for each tenant ([Schema-per-tenant databases](tenants.md#naming-the-tenant-variables)); the project's own
scripts cannot use them, and they cannot be given a value.

## Where the values come from

`pgproj publish --execute` takes each value from an environment variable named `PGPROJ_VAR_` and the variable's name:

```sh
export PGPROJ_VAR_app_password='...'
export PGPROJ_VAR_reporting_role=reporting
pgproj publish --execute
```

In CI, set them from the system's secrets (a GitHub Actions secret as `env:`, a Nomad variable, a Key Vault reference), as for `PGPROJ_TARGET_CONNECTION`.
`pgproj about` lists the names it finds, never the values.

## What each command does

| Command | What it does with a variable |
|---|---|
| `build` | Checks the script with a stand-in for each variable; it never needs or reads a value. |
| `diff` | Nothing: the deploy scripts are not compared. |
| `publish --dry-run`, `--output` | Keeps the variable as written, and warns which variables the script uses. Run the script with `psql -v app_password=... -v reporting_role=... -f deploy.sql`. |
| `publish --execute` | Fills each value in, only in what it sends to the server. A variable with no value refuses the publish before anything runs: `the script uses :'app_password', which has no value, so nothing was run`. |

Every value is treated as a secret: it is never printed, and when a statement fails, pgproj names the statement as written (with its variable) and replaces
the value with `*****` wherever the server's message quotes it. The script `publish` prints or writes, and `PublishResult.Script` in the
[library](../library.md), keep the variables too.

## What the server keeps

pgproj cannot keep a value from the server: a statement such as `ALTER ROLE ... PASSWORD` takes its value as written, not as a parameter. Checked on
PostgreSQL 18:

- with `log_statement` set to `ddl` or `all`, the server log has the statement with the password in plain text;
- `pg_stat_statements`, when it is installed, keeps the same text.

Where that matters, turn statement logging off for the session at the top of the script (`SET log_statement = 'none';`, which needs a superuser, or on
PostgreSQL 15 and later a role granted `SET` on that parameter; it does not keep the text out of `pg_stat_statements`), or set the password some other way
(psql's `\password` sends it already encrypted).

## What is not done yet

Variables are for the deploy scripts, and for the options of a foreign server or user mapping, which send a secret when the mapping is created and with
`--apply-secrets` ([foreign data](foreign-data.md)). The other objects that hold a secret (a role's password, a subscription's connection) are not modeled;
when one is, it will use the same `:'name'` and `PGPROJ_VAR_<name>`. A changed value is not a difference `diff` can see: a deploy script sends its value each
time it runs.
