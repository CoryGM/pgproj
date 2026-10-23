# Triggers

Triggers: a trigger's timing, events, row or statement level, function and arguments, `WHEN` condition, `UPDATE OF` columns, constraint-trigger options (`DEFERRABLE`, `INITIALLY DEFERRED`, `FROM`), and transition tables are compared, and a change is deployed by dropping and recreating the trigger with the statement as written (a change to only its comment is `COMMENT ON TRIGGER`, in place, and a recreated trigger gets its comment back). A `WHEN` condition is compared structurally, so it matches how PostgreSQL stores it; a few forms that need the type of the other operand (typed date and timestamp literals, `SIMILAR TO`) can read as changed, which recreates the trigger on every publish. The firing mode (`ALTER TABLE ... ENABLE/DISABLE TRIGGER`, `USER`, and `ALL`, which includes the foreign-key triggers) is kept; see [`ALTER TABLE` in project SQL](alter-table.md).

`init` writes a trigger into its table's file, and a trigger on a view (`INSTEAD OF`, or statement-level) into the view's file, after the view.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
