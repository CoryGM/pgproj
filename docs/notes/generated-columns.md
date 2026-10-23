# Generated columns

Generated columns: `GENERATED ALWAYS AS (expression) STORED` (or `VIRTUAL`, PostgreSQL 18) is created with its expression, and a change to the expression, or between stored and virtual, re-adds the column (its values are derived). A generated column that becomes ordinary keeps its values (`DROP EXPRESSION`). An ordinary column that becomes generated cannot be converted without losing what it holds, so the script says so and `publish --execute` refuses it.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
