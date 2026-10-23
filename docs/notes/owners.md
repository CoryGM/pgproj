# Owners

An object's owner is stated with `ALTER ... OWNER TO`, for these kinds: `TABLE`, `VIEW`, `MATERIALIZED VIEW`, `SEQUENCE`, `FUNCTION`, `PROCEDURE`,
`AGGREGATE`, `OPERATOR`, `TYPE`, `DOMAIN`, `SCHEMA`, `EVENT TRIGGER`, `PUBLICATION`, `COLLATION`, `STATISTICS`, `TEXT SEARCH DICTIONARY`, and
`TEXT SEARCH CONFIGURATION`. A schema's owner can also be stated with `CREATE SCHEMA name AUTHORIZATION role`.

```sql
ALTER TABLE app.orders OWNER TO app_owner;
```

The owner is applied after the object is created, read back from the catalogs, and compared: `diff` reports an object whose owner differs from the one
stated.

## A project states the owners it chooses

**An object whose owner the project does not state is not compared**, and is created owned by the role that deploys. So that this is not silent, once a
project states any owner, `build`, `diff`, and `publish` say how many objects state none:

```text
23 objects state no owner (ALTER ... OWNER TO); their owners are not compared.
```

`excludeOwners` in `pgproj.json` (overridable per run with `--exclude-owners`, in either direction) leaves owners out on both sides, for a project that
runs against databases whose roles are named differently ([Excluding grants and owners](excluding-grants-and-owners.md)).

## Changing an owner

- Changing the owner of an existing object takes the old owner's privileges away, so `publish --execute` refuses it unless `--allow-destructive` is
  given, naming the old and the new owner. Setting the owner of a new object is not destructive.
- An object that `publish` drops and creates again (a view rebuilt around a column change, for one) is given the owner the project states.
- The role must exist ([Roles](roles.md)). PostgreSQL itself requires an event trigger's owner to be a superuser.

## Sequences a column owns

The owner of a sequence a column owns follows the column's table, so `init` does not write one, and PostgreSQL refuses a project that states one. The
sequence is handed to its column (`ALTER SEQUENCE ... OWNED BY`) before the first owner statement, because PostgreSQL refuses it once the table and the
sequence have different owners.

## Not modeled

`OWNER TO CURRENT_USER`, an index's owner (it follows its table), and the owner of a kind of object pgproj does not model (a foreign data wrapper, a
language, ...) are each reported as not modeled.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
