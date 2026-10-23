# Event triggers

Event triggers: `CREATE EVENT TRIGGER name ON event [WHEN TAG IN (...)] EXECUTE FUNCTION f()` is deployed after the function it runs, read back from `pg_event_trigger`, and compared by its event, its tag filter (a set, case-insensitive), the function, and when it fires (`ALTER EVENT TRIGGER name DISABLE`/`ENABLE REPLICA`/`ENABLE ALWAYS` in the project sets it, in any file). Any change is a drop and a create. `init` writes each one to `EventTrigger/<name>.sql`. Removing one is a destructive change (it stops guarding every DDL command it ran on), so `publish --execute` refuses it without `--allow-destructive`. An event trigger an extension owns (as `pgaudit`'s are) is left alone, and when `schemas` is set only the event triggers whose function is in one of those schemas are managed. PostgreSQL lets only a superuser create an event trigger, so the deploying role must be one, and `publish --execute` runs a script that creates, changes, or drops one only with `--allow-superuser-statements` ([statements only a superuser can run](superuser-statements.md)). The event trigger's owner is modeled (see [Owners](owners.md); its owner must be a superuser); its `COMMENT ON EVENT TRIGGER` is not (warned about and ignored).

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
