# What is deployed is what you wrote

`publish` deploys the SQL in the project's files as it is written. pgproj also keeps a normalized form of each object, but only to compare it with the database: `diff` decides what changed from the normalized forms, and the script is built from the text in your files.

What pgproj generates itself is the statement structure around your text. This includes the `CREATE TABLE` layout, constraints and indexes written as their own statements, `ALTER` statements for a change, and the quotes on a name that needs them. Your expressions keep your spelling, spacing, comments, and casts:

- column defaults and generated columns;
- `CHECK` constraints and exclusion constraints' expressions;
- index expressions and predicates;
- view and routine bodies;
- row-level security policies;
- domains' defaults and checks;
- routine parameters' defaults and `SET` clauses;
- extended statistics expressions;
- publication row filters;
- types, wherever they are written: a column's, a routine parameter's and result's, a domain's base type, a composite type's fields, and the types a cast, an operator, or an aggregate names. `INT` is deployed as `INT`, `VARCHAR(10)` as `VARCHAR(10)`, and a name that needs quotes (`"char"`, `"My S"."My T"`) keeps them.

Because the comparison is between normalized forms, a change of spelling alone is not a difference. Changing `interval '1 day'` to `'1 day'::interval`, or `INT` to `integer`, in the project gives no `diff`, and the database keeps what it already has. A live database's text is PostgreSQL's own rendering, so a project scaffolded by `init` uses PostgreSQL's spelling until you edit it.

**How the text is found:** pgproj takes an expression's text from the file at the position PostgreSQL's parser reports for it. It finds the end with PostgreSQL's own lexer, and keeps the slice only when parsing the slice gives exactly the same tree. A type is found the same way, confirmed by parsing `NULL::<type>`. If that ever fails, pgproj rebuilds the expression from the parse tree, or writes the type in its own spelling, and reports a warning in `build`, `diff`, and `publish`, naming the file, line, and column, from where the expression starts to the end of its statement:

```
sql/schema.sql:2:17-2:23: [Warning] The text of an expression could not be read as written, so it is deployed as pgproj rebuilt it, which may differ from what was written: a + 1
```

The rebuilt text means the same thing in most cases but is not guaranteed to. Please report such a warning as an issue.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
