# Privileges on types and domains

A project grants `USAGE` on an enum, composite, or range type, or on a domain, with `GRANT`, and takes PostgreSQL's default away with `REVOKE ... FROM PUBLIC`:

```sql
CREATE TYPE app.status AS ENUM ('new', 'paid', 'shipped');
REVOKE ALL ON TYPE app.status FROM PUBLIC;
GRANT USAGE ON TYPE app.status TO app_writer;

CREATE DOMAIN app.positive AS integer CHECK (VALUE > 0);
GRANT USAGE ON DOMAIN app.positive TO app_writer WITH GRANT OPTION;
```

`USAGE` is the only privilege a type has, so `GRANT ALL` is the same grant. A domain may be named `ON TYPE` too, which is how `pg_dump` writes it.

## PostgreSQL's default, and the `REVOKE` pgproj reads

PostgreSQL gives `USAGE` on every new type and domain to `PUBLIC`, every role. A project that says nothing about a type's privileges wants that default, as
running its SQL on an empty database would give, and `diff` compares the type that way.

To take the default away, write `REVOKE USAGE ON TYPE t FROM PUBLIC` (or `REVOKE ALL`, or `ON DOMAIN`), the form `pg_dump` writes. With the same `REVOKE`
on a function or procedure ([routine privileges](routine-privileges.md)) and in `ALTER DEFAULT PRIVILEGES` ([default
privileges](default-privileges.md#revoking-publics-default-for-new-objects)), it is the one `REVOKE` pgproj reads; every other one is a warning and ignored, because a project states what it grants and pgproj revokes the rest. A `REVOKE` that also names a
role reads only its `PUBLIC` part, and says so.

What `USAGE` on a type controls is creating things that use it (a column, a function's argument or result, a cast, a domain over it), not reading or writing
its values: a role without it can still select a column of the type.

## What `publish` does

- **A type the project creates** is created, then its `REVOKE` and grants are applied after it.
- **A grant to a role** is added or revoked as on any other object. Revoking one needs `--allow-destructive`.
- **Taking `PUBLIC`'s default away from a type that already exists** is a revoke too, and needs `--allow-destructive`. On a type the script creates, it takes
  nothing away and needs nothing.
- **Giving the default back**, when the database's type has it revoked and the project does not, is `GRANT ... TO PUBLIC`. It takes nothing away, so it needs
  no consent, but it widens access, so `publish` names it:

  ```text
  WARNING: this publish gives PUBLIC privileges it does not have now. PUBLIC is every role, including roles created later:
  after this publish, every role has the privileges listed below. Read each one before you run it.

  PostgreSQL's default is given back, because the database has it revoked and the project does not revoke it:
    - type app.status: every role can use it.
        GRANT ALL PRIVILEGES ON TYPE "app"."status" TO PUBLIC;
  To keep them revoked, add to the project:
        REVOKE ALL PRIVILEGES ON TYPE "app"."status" FROM PUBLIC;
    pgproj init writes these REVOKE statements when it scaffolds a project from the database.

  If any of this is not what you want, do not run this publish.
  ```

  The same warning names a routine's `EXECUTE` given back ([routine privileges](routine-privileges.md)), a rule that revokes the default for new types or
  functions the project removes, and any grant to `PUBLIC` the project adds on a table, sequence, routine, type, or schema the database already has.
- **A type `publish` drops and creates again**, or rebuilds under the columns that use it (an [enum](enum-types.md) that loses or reorders a label, a
  [composite type](composite-types.md) that retypes a field), gets its `REVOKE` and grants back. With `excludeGrants`, it gets back the privileges the
  database had.

## In the database and in `init`

A type's privileges are read from the database: one whose privileges were never changed has the default and nothing is written; otherwise each role's
grant, and the `REVOKE ... FROM PUBLIC` when `PUBLIC` has no `USAGE`. `init` writes them in the type's own file, after the type. The type's owner is
never listed: it has every privilege on what it owns.

Not read on their own, because PostgreSQL gives them none: the array type PostgreSQL makes for each type (its privileges are its element type's), and a
range type's multirange type (its range type's). `excludeGrants` leaves type privileges out with the others ([excluding grants and
owners](excluding-grants-and-owners.md)).

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
