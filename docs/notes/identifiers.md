# Identifiers

Identifiers: a quoted name keeps its case, as in PostgreSQL. `CREATE TABLE "MyTable" ("Mixed" integer)` deploys a table named `MyTable` with a column named `Mixed`, and names with spaces or reserved words (`"order"`, `"Col Name"`) are written back with their quotes. Names are compared the way PostgreSQL compares them:

- **Unquoted names:** an unquoted name is folded to lower case, so `MyTable` and `mytable` are the same table.
- **Quoted names:** a quoted name keeps its case and is compared exactly. A project can declare `"T"` and `t` as two tables, `f(m "Mood")` and `f(m mood)` as two overloads, and enum labels `'ok'` and `'OK'`. A change of case in a quoted name is a change: `"MyTable"` becoming `mytable` is another table, so the old one is dropped and the new one created.
- **Types named like a built-in:** a quoted type name with a capital is your type, never the built-in one, as in PostgreSQL: a column of `"Name"` or `"Date"` (common in schemas ported from SQL Server) uses your domain or type of that name and is created after it, while `NAME`, `name`, and `"name"` are the built-in `name`. The same holds for a domain's base type: `CREATE DOMAIN d AS "Mytype"` is over your `"Mytype"`, and a change between `"Mytype"` and `mytype` is a change.
- **Still case-insensitive:** keywords and the names PostgreSQL treats as keywords: type keywords (`INTEGER`, `VARCHAR`), setting names in `SET`, option names, privileges, and `PUBLIC`.
- **Names typed outside SQL:** a name in `pgproj.json` (`schemas`, `searchPath`), in a command-line option (`--schemas`, `--exclude-schemas`, `--erd-schemas`, `--erd-exclude-schemas`), or in an `@renamed-from` annotation is read as PostgreSQL and `pg_dump -n` read a name. It's folded to lower case unless it's in double quotes: `App` means `app`, and `"App"` means `App`.

File names follow the file system rather than the operating system. `init` writes `T.sql` and `t.sql` for two objects whose names differ only by case when the output folder tells them apart, and `t~2.sql` for the second when it doesn't. To find out, it writes a temporary file and checks whether the same name in the other case finds it; if it can't write there, it assumes Windows and macOS are case-insensitive and other systems case-sensitive. The project loader and `:r` includes in deployment scripts use the same check to decide whether two paths name one file.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
