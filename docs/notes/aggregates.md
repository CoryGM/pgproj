# Aggregates

Aggregates are modeled: ordinary, `(*)`, ordered-set `(direct ORDER BY ordered)`, and hypothetical-set, in the current and the old `BASETYPE` syntax.

```sql
CREATE AGGREGATE name (arguments) (SFUNC = ..., STYPE = ..., ...);
```

An aggregate is identified like an overloaded function, by its schema, its name, and its argument types. `init` writes each one to
`Schema/<schema>/Aggregate/<name>.sql`.

## Order and comparing

An aggregate is deployed after the state, final, combine, and moving-aggregate functions and the types it uses, and before whatever calls it (a view, a
default, a `CHECK`, a routine body), whatever order the files are in. It is read back from `pg_aggregate` and compared option by option; an option the
project leaves out and PostgreSQL defaults (`PARALLEL = UNSAFE`, `FINALFUNC_MODIFY`, a zero `SSPACE`) is not a difference.

An argument's name (`CREATE AGGREGATE f(VARIADIC items anycompatiblearray)`) is deployed and read back, so `pg_proc.proargnames`, `\df`, and `document`
keep it. The names are not part of the aggregate's identity and are not compared, as a routine's parameter names are not; `DROP AGGREGATE` and
`OWNER TO` name the aggregate by its types alone.

## Changing one

An aggregate cannot be altered, so any change is a drop and a create.

- The objects PostgreSQL records as using it (a view, an index, a `CHECK`, a rule, a default) are taken off first and put back after.
- A function under it that has to be dropped and created again rebuilds it too.
- Removing one is a destructive change, so `publish --execute` refuses it without `--allow-destructive`.

## Not the project's, and limits

- The aggregates PostgreSQL ships and one an extension owns are left alone. When `schemas` is set, only the aggregates in one of those schemas are
  managed.
- `DROP AGGREGATE`, `ALTER AGGREGATE`, and `COMMENT ON AGGREGATE` are warned about and ignored, and an aggregate's comment and privileges are not read.
  Its owner is: see [Owners](owners.md).
- A hypothetical-set aggregate needs a function written in C, which pgproj does not deploy, so this kind is not tested against a server.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
