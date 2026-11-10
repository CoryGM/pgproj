# Privileges on functions and procedures

PostgreSQL gives `EXECUTE` on every new function and procedure to `PUBLIC`, every role. A project that says nothing about a routine's `PUBLIC` privilege
wants that default, as running its SQL on an empty database would give, and `diff` compares the routine that way. To take the default away, write the
`REVOKE` `pg_dump` writes:

```sql
CREATE FUNCTION app.close_books() RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER AS $$ ... $$;
REVOKE ALL ON FUNCTION app.close_books() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.close_books() TO accounting;
```

`REVOKE EXECUTE` or `REVOKE ALL`, `ON FUNCTION` or `ON PROCEDURE`, `FROM PUBLIC`: each is the same. Like the [type case](type-privileges.md), it is one of
the few `REVOKE`s pgproj reads; every other one is a warning and ignored, because a project states what it grants and pgproj revokes the rest. Not read:
`REVOKE ... ON ROUTINE` (`GRANT ... ON ROUTINE` is not read either; write `FUNCTION` or `PROCEDURE`), `REVOKE ... ON ALL FUNCTIONS IN SCHEMA`, and
`REVOKE GRANT OPTION FOR`. A `REVOKE` that also names a role reads only its `PUBLIC` part, and says so.

This matters most for a `SECURITY DEFINER` routine, which runs with its owner's privileges whoever calls it.

## What `publish` does

- **A routine the project creates** is created, then its `REVOKE` and grants are applied after it.
- **Taking `PUBLIC`'s default away from a routine that already exists** is a revoke: callers that relied on it stop working. It needs
  `--allow-destructive`. On a routine the script creates, it takes nothing away and needs nothing.
- **A routine `publish` drops and creates again** (a changed return type or argument list, say) gets its `REVOKE` and grants back. Before pgproj 0.2.2 it came
  back with `PUBLIC`'s `EXECUTE`, and `diff` reported no change. A routine changed with `CREATE OR REPLACE` keeps its privileges, as PostgreSQL keeps them.
- **Giving the default back**, when the database's routine has it revoked and the project does not, is `GRANT ... TO PUBLIC`. It takes nothing away, so it
  needs no consent, but it opens the routine to every role, so `publish` says so before anything runs, in words, with the statement that keeps it revoked:

  ```text
  WARNING: this publish gives PUBLIC privileges it does not have now. PUBLIC is every role, including roles created later:
  after this publish, every role has the privileges listed below. Read each one before you run it.

  PostgreSQL's default is given back, because the database has it revoked and the project does not revoke it:
    - function app.close_books() (SECURITY DEFINER: it runs with its owner's privileges, whoever calls it): every role can execute it.
        GRANT ALL PRIVILEGES ON FUNCTION "app"."close_books"() TO PUBLIC;
  To keep them revoked, add to the project:
        REVOKE ALL PRIVILEGES ON FUNCTION "app"."close_books"() FROM PUBLIC;
    pgproj init writes these REVOKE statements when it scaffolds a project from the database.

  If any of this is not what you want, do not run this publish.
  ```

  `--dry-run` shows the same warning.

**A project written before pgproj 0.2.2 cannot have said so.** If its database has routines with `PUBLIC`'s `EXECUTE` revoked, `diff` now reports each one,
and `publish` would give it back, after the warning. Add the `REVOKE` statements to the project first (the warning lists them, and `init` writes them), or
set `excludeGrants`.

## Default privileges

`ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC` (or `ON ROUTINES`, or `USAGE ON TYPES`) makes new routines (or types) without the default.
pgproj reads, deploys, and compares it, and `publish` writes it before the objects it covers. A routine or type the project does not revoke on its own is then
allowed either way: created under the rule it has no `PUBLIC` privilege, created before it it has the default, and neither is a difference. See [default
privileges](default-privileges.md#revoking-publics-default-for-new-objects).

## In the database and in `init`

A routine whose privileges were never changed has the default and nothing is written; one without `PUBLIC`'s `EXECUTE` is read as `REVOKE ALL ON
FUNCTION|PROCEDURE ... FROM PUBLIC`, which `init` writes in the routine's own file. With `excludeGrants`, a routine dropped and created again gets back the
privileges the database had ([excluding grants and owners](excluding-grants-and-owners.md)). Aggregates' privileges are not read.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
