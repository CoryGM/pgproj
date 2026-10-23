# Extensions

How pgproj treats `CREATE EXTENSION` and `COMMENT ON EXTENSION`.

## Identity

An extension is named by its name alone, as PostgreSQL names it: `citext` is the same extension whatever schema it is in. Its schema, version,
and comment are properties of it.

## What is deployed

`publish` runs your `CREATE EXTENSION` statement as you wrote it: `IF NOT EXISTS`, `SCHEMA`, `VERSION`, and `CASCADE` are all kept.
Extensions are created before everything else, in the order the project declares them; the project's files are read in path order, and
statements in a file in the order written. Declare an extension after the ones it requires, or write `CASCADE` and let PostgreSQL install
them.

pgproj never creates `pg_catalog`, `information_schema`, or another `pg_` schema, so `CREATE EXTENSION cube SCHEMA pg_catalog` works.

## What is compared

Only what the project states:

- **Schema:** compared when you write `SCHEMA`. A different one is changed in place with `ALTER EXTENSION ... SET SCHEMA`, after creating the
  schema if it's missing. Without `SCHEMA`, PostgreSQL chooses the schema and pgproj doesn't compare it.
- **Version:** compared when you write `VERSION`. A different one is changed with `ALTER EXTENSION ... UPDATE TO 'version'`. Without it, any
  installed version will do.
- **Comment:** compared when you write `COMMENT ON EXTENSION` (`IS NULL` states that it has none), and changed in place. An extension comes
  with a comment of its own, which is left alone when the project states none. `publish --comments-only` includes these comments.

An extension is never dropped and created again, since that would drop every object it installed.

## What belongs to an extension

What an extension installs is not the project's: `diff` doesn't report it, `publish` never drops it, and `init` doesn't write it. That covers its
members (the tables, views, functions, types, and other objects PostgreSQL records as part of it) and everything on a table or view it owns: the
indexes, constraints, triggers, rules, row-level security policies, extended statistics, comments, and grants. PostgreSQL doesn't record
a rule as a member of the extension that made it, so pgproj counts the rules on a table or view an extension owns as the extension's: PostGIS's
three rules on `geometry_columns` are not reported as removed, and `publish` never drops them.

## Scoped projects

When `schemas` scopes `diff` and `publish`, an extension the project declares is compared wherever it is, since an extension is named by its
name alone; one written without `SCHEMA` adds no schema to the scope. An extension the project doesn't declare is read only when it is in a
schema in scope.

## What is removed

An extension the database has and the project doesn't declare is dropped, like any other object, unless an extension the project keeps
requires it, directly or through others: one that `CASCADE` installed stays. Extensions are dropped after everything else, each before the
ones it requires.

## `init`

`init` writes `CREATE EXTENSION name SCHEMA schema;` without a version, so the project accepts whatever version a new database installs. It
writes a `COMMENT ON EXTENSION` only when the comment isn't the one the extension comes with (`IS NULL` when it was removed). Each
extension gets `Extension/<name>.sql`, except one that requires others: it is written after them, in the file of the requirement that sorts
last, so the project declares it after all of them.

## Known limits

- Moving an extension with `SCHEMA` carries its types with it. A table column that uses one, written `citext` before and `other.citext` after, is the
  same column and isn't altered. A domain or composite type field that uses one is still compared by its spelling.
- `document` doesn't describe extensions.
- A trigger, rule, policy, index, or statistics object you add yourself to a table or view an extension owns isn't modeled either: the database's
  is not read, so a project that declares one sees it as added on every `diff`, and its second `publish` fails because it exists.
