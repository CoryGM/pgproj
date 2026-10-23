# Rules

Rules are modeled:

```sql
CREATE [OR REPLACE] RULE name AS ON event TO table_or_view [WHERE condition]
    DO [ALSO | INSTEAD] { NOTHING | command | (command; ...) };
```

A rule is identified by its relation and its name. It is deployed after the table or view it is on and after what its condition and actions use,
whatever order the files are in, and `init` writes each one to the file of its table or view.

## Comparing and changing

A rule is read back from `pg_rewrite`. PostgreSQL rewrites a rule when it stores it (it qualifies names, adds parentheses, and spells out `new` and
`old`), so a rule is compared by `pg_get_ruledef`: for a project rule that also exists in the database, `diff` and `publish` have the server render it,
by replacing the live rule with it inside a transaction that is always rolled back. A rule written differently from how PostgreSQL prints it is
therefore not a change. This needs the connecting role to own the table.

- A modified rule is dropped and created again.
- A column, routine, or operator that a rule uses can be changed: the rule is dropped first and created again after.
- Removing one is a destructive change, so `publish --execute` refuses it without `--allow-destructive`: an `INSTEAD` rule is what makes a view writable.
- A rule with several actions, `DO ALSO (INSERT ...; INSERT ...)`, is run whole by `publish --execute`: the script is split into statements as `psql`
  splits it, so a `;` inside parentheses, a quoted name, or an `E'...'` string does not end a statement.

## Not the project's

A view's own `_RETURN` rule, the rules PostgreSQL ships, a rule an extension owns, and one on a table or view an extension owns (PostGIS's on
`geometry_columns`) are left alone. When `schemas` is set, only the rules on relations in one of those schemas are managed.

## Limits

- A rule's enabled state (`ALTER TABLE ... DISABLE RULE`) is not modeled.
- `DROP RULE`, `ALTER RULE`, and `COMMENT ON RULE` are warned about and ignored.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
