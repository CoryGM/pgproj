# Schemas and unqualified names

The live database is read with `search_path` set to `public`, whatever the connecting role's own path is.

**A declared schema is created.** A schema the project declares with `CREATE SCHEMA` is created by `publish` (`CREATE SCHEMA IF NOT EXISTS`) when the database lacks it, even with nothing in it, and `diff` shows it as `+ Schema name`: a schema declared for another tool to fill, such as Supabase's `storage` or `vault`, is there after a publish. `publish` also creates a schema an added object, owner, or grant needs. The comment on a declared schema is compared only when the project states one (`COMMENT ON SCHEMA`; `IS NULL` states none), so a comment set in the database is otherwise kept. `public` and the system schemas are never created.

**Schemas are never dropped.** `publish` creates a schema as above, but a schema the database has that the project no longer has anything in is left in place: its objects are reported as removed and dropped as usual (with `--allow-destructive`), the schema itself is not. `diff` and `publish` warn and name each such schema; drop it yourself (`DROP SCHEMA ...`) if it is no longer wanted. `public` and the system schemas are never named. This is the user's decision (2026-10-02); the behaviour is unchanged, only the warning is new.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
