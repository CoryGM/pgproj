# Publishing only the comments: `publish --comments-only`

`pgproj publish --comments-only` deploys just the `COMMENT ON` statements that a normal `publish` would run, and nothing else. It is for publishing documentation
(see [`documentation.md`](../../documentation.md)) to a database without touching its schema: no new or changed tables, columns, indexes, or constraints, no grants or owners, and not the project's
`predeploy` or `postdeploy` script. The changes it leaves out stay in `pgproj diff` and are deployed by a later `publish` without the option.

```
pgproj publish --target-connection "Host=localhost;Database=app" --comments-only --dry-run
pgproj publish --target-connection "Host=localhost;Database=app" --comments-only --execute
pgproj publish --target-connection "Host=localhost;Database=app" --comments-only --output ./comments.sql
```

## What it deploys

The comments `publish` would set, change, or clear (`COMMENT ON ... IS NULL` for a comment the project removes) on schemas, tables, columns, constraints, triggers, policies, indexes, views, routines, types,
sequences, and the other objects that take one, exactly as they appear in the full script. It prints a line saying how many statements it deploys and how many other operations are not part of the script.

## A comment on something the database does not have yet

A comment on a table or column that the project adds, or on any other object that does not exist in the database yet, is **skipped, with a warning that names it**. Each statement is written inside a `DO` block that
catches PostgreSQL's "does not exist" errors, so the rest of the script goes on:

```
Warning: pgproj publish --comments-only skipped a comment: COMMENT ON TABLE "app"."orders" (relation "app.orders" does not exist)
```

Because the skipping is in the script itself, a script written with `--output` behaves the same under `psql`, where the server reports it as a `WARNING`. `publish --execute` repeats each as a warning of its own.
Publish again without `--comments-only` to create the table; the full publish sets its comments with it.

## What does not apply

Because nothing but comments is deployed, `--allow-destructive` has nothing to guard, and the checks for what the rest of the project needs from the server (a tablespace or role that is missing, a feature
the server's version lacks) and the warning about a schema the project no longer uses are not made. `--execute` still runs the script in one transaction, and `--dry-run`, `--output`, `--quiet`, `--verbose`,
`--exclude-grants`, `--exclude-role-memberships`, `--exclude-owners`, and `--erd` work as they do for any `publish`. A project `searchPath` is still set at the start of the script.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
