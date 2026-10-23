# Statements only a superuser can run

PostgreSQL lets only a superuser run some of the statements pgproj deploys:

- any statement on an **event trigger**: creating, changing, or dropping one, and its owner, which must be a superuser ([event triggers](event-triggers.md));
- `ALTER TABLE t { ENABLE | DISABLE } TRIGGER ALL`, which also changes the **internal triggers** that enforce the table's foreign keys ([`ALTER TABLE` in project SQL](alter-table.md)); `TRIGGER USER` and a named trigger do not need a superuser;
- a function or procedure created or replaced **`LEAKPROOF`** ([routine options](routine-options.md));
- a function or procedure created or replaced in an **untrusted language**: `c`, `internal`, or a language the server marks untrusted (`plpython3u`, `plperlu`, ...; `pg_language.lanpltrusted`).

A role that is not a superuser used to fail partway through the script, which rolled back. Now:

- **`publish --execute` runs them only with `--allow-superuser-statements`.** The option is consent for this run, not a role switch: without it, `--execute` refuses the script before anything runs, even when the connected role is a superuser, and lists each statement:

  ```
  Publish failed: the script has 2 statement(s) that only a superuser can run, so it was not run:
    [EventTrigger] event trigger:audit_ddl: an event trigger is created, changed, or dropped only by a superuser
    [Function] public.lp(int4): only a superuser can make a routine LEAKPROOF
  Use --output/--dry-run to review the generated script, and pass --allow-superuser-statements to run it as a superuser.
  ```

- **With the option, the connected role must be a superuser** (`rolsuper`). One that is not is refused before anything runs, with the same list.
- **A script that is printed or written** (`--dry-run`, `--output`, no `--execute`) starts with a comment naming the statements, because a DBA may run it under another account, and a warning says so.
- **Only the statements in this script count.** A project with an event trigger that is already deployed and unchanged publishes without the option.
- There is no `pgproj.json` setting for it, and `diff` does not mention it.

What pgproj does not model yet and would also need a superuser (base types, foreign data wrappers) will join this list when it is modeled.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
