# Operators

Operators are modeled:

```sql
CREATE OPERATOR name (LEFTARG, RIGHTARG, FUNCTION | PROCEDURE, COMMUTATOR, NEGATOR, RESTRICT, JOIN, HASHES, MERGES);
```

An operator is identified by its schema, its name, and its two operand types (`integer` and `pg_catalog.int4` are one; a prefix operator has one
operand), so `===` can be defined for several pairs. `init` writes each one to `Schema/<schema>/Operator/`, named by its symbols spelled out (`===` is
`eq_eq_eq`) and its operands.

## Comparing and changing

An operator is deployed after the function and the types it uses, read back from `pg_operator`, and compared by its function, commutator, negator,
estimators, and flags. PostgreSQL links a commutator and a negator both ways, so naming the link on one operator is enough: the other is compared with it
too.

- A modified operator is dropped and created again.
- Removing one is a destructive change, so `publish --execute` refuses it without `--allow-destructive`.

## What uses an operator

What uses an operator (a view, a column default, a `CHECK`, an index predicate or key expression, a domain `CHECK`, a `BEGIN ATOMIC` or `RETURN` routine
body) is created after it, whatever order the files are in. When the operator has to be dropped and created again, the objects PostgreSQL records as
using it are taken off first and put back after: a default and a domain `CHECK` are detached and reattached, and a view, an index, and a `CHECK` are
dropped and recreated.

Not ordered after it yet: a policy, a trigger `WHEN`, a generated column, an exclusion constraint, and a `PL/pgSQL` or string body that use an operator.
On a new database, put such an operator in a `predeploy` script.

## Not the project's, and not modeled

- The operators PostgreSQL ships, one an extension owns, and the shell operator PostgreSQL makes for a commutator or negator that was never defined are
  left alone. When `schemas` is set, only the operators in one of those schemas are managed.
- `DROP OPERATOR`, `ALTER OPERATOR`, `COMMENT ON OPERATOR`, and operator classes and families are warned about and ignored.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
