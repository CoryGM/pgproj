# Privileges on the public schema

Every database's `public` schema comes with privileges for `PUBLIC`, every role: `USAGE` on every PostgreSQL version, and `CREATE` too on PostgreSQL 14
(15 took it away). A common hardening step takes them away, in the form `pg_dump` writes:

```sql
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO app_reader;
```

`REVOKE USAGE`, `REVOKE CREATE`, or `REVOKE ALL` (both), `ON SCHEMA public FROM PUBLIC`: pgproj reads each. Like the [type](type-privileges.md) and
[routine](routine-privileges.md) cases, it is one of the few `REVOKE`s pgproj reads. On any other schema `PUBLIC` has nothing unless a project grants it, so a
`REVOKE ... FROM PUBLIC` there is a warning and ignored, as is a `REVOKE` from a role: a project states what it grants, and pgproj revokes the rest.

## `USAGE`: the default unless the project revokes it

A project that says nothing about `public` wants PostgreSQL's default, `USAGE` for `PUBLIC`, as a new database has, and `diff` compares the database that way.

- **Taking it away** is a revoke: a role that is not the owner, a superuser, or granted `USAGE` of its own loses access to what is in `public`. It needs `--allow-destructive`. The `public` schema
  always exists, so it needs it on a new database too.
- **Giving it back**, when the database has it revoked and the project does not, is `GRANT USAGE ON SCHEMA "public" TO PUBLIC`. It takes nothing away, so it
  needs no consent, but `publish` says so before anything runs:

  ```text
  WARNING: this publish gives PUBLIC privileges it does not have now. PUBLIC is every role, including roles created later:
  after this publish, every role has the privileges listed below. Read each one before you run it.

  PostgreSQL's default is given back, because the database has it revoked and the project does not revoke it:
    - schema public: every role can look up the objects in it, and use each one its own privileges allow.
        GRANT USAGE ON SCHEMA "public" TO PUBLIC;
  To keep them revoked, add to the project:
        REVOKE USAGE ON SCHEMA "public" FROM PUBLIC;
    pgproj init writes these REVOKE statements when it scaffolds a project from the database.

  If any of this is not what you want, do not run this publish.
  ```

**A project written before pgproj 0.3.0 cannot have said so.** If its database has `PUBLIC`'s `USAGE` on `public` revoked, `diff` now reports it, and
`publish` would give it back, after the warning. Add the `REVOKE` to the project first (`init` writes it), or set `excludeGrants`.

## `CREATE`: compared only when the project revokes it

`PUBLIC`'s `CREATE` on `public` is a default only on PostgreSQL 14, and one that lets every role create objects there. pgproj never gives it back:

- A project that states `REVOKE CREATE` (or `REVOKE ALL`) wants `PUBLIC` without it. On 14, where `PUBLIC` has it, `publish` revokes it, which needs
  `--allow-destructive`; on 15 and later there is nothing to do.
- A project that does not state it does not compare it: a 14 database keeps `CREATE` for `PUBLIC` or not, as it is.

## In the database and in `init`

`public` without `PUBLIC`'s `USAGE` is read as `REVOKE USAGE ON SCHEMA public FROM PUBLIC`, which `init` writes in `Schema/public/public.sql`. A database on 14
without `PUBLIC`'s `CREATE` is read, and scaffolded, as `REVOKE CREATE ON SCHEMA public FROM PUBLIC` too; on 15 and later that is the server's default, and
`init` does not write it. A `public` schema that was dropped and created again by hand has neither privilege for `PUBLIC` (PostgreSQL makes a new schema
its owner's alone), so it reads as revoked. pgproj never creates `public` itself.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
