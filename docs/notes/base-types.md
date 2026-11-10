# Base types

A base type, `CREATE TYPE name (INPUT = ..., OUTPUT = ...)`, is modeled: deployed, read back from the database, compared, and written by `init`. Write it the way
`pg_dump` does, with its shell first:

```sql
CREATE TYPE public.xint4;
CREATE FUNCTION public.xint4in(cstring) RETURNS public.xint4 LANGUAGE internal IMMUTABLE STRICT AS $$int4in$$;
CREATE FUNCTION public.xint4out(public.xint4) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS $$int4out$$;
CREATE TYPE public.xint4 (INTERNALLENGTH = 4, INPUT = public.xint4in, OUTPUT = public.xint4out, ALIGNMENT = int4, PASSEDBYVALUE);
```

The shell and the definition are one type, in one file or in two. A shell the project never defines is a warning and is ignored. A base type nearly always comes
from an extension, and an extension's types are the extension's, not the project's ([extensions](extensions.md)).

## It needs a superuser

PostgreSQL lets only a superuser create a base type or its shell, change its options with `ALTER TYPE ... SET` (even the type's owner), or create a function in
`C` or `internal`. So `publish --execute` runs these statements only with `--allow-superuser-statements`, and then only as a superuser
([superuser statements](superuser-statements.md)). Dropping a base type needs only its owner.

## What `publish` does

- **A new type** is created as a shell, then its support functions (`INPUT`, `OUTPUT`, `RECEIVE`, `SEND`, `TYPMOD_IN`, `TYPMOD_OUT`, `ANALYZE`, `SUBSCRIPT`), then
  the `CREATE TYPE` as written, then what uses it (a column, a cast, a function that takes it).
- **`RECEIVE`, `SEND`, `TYPMOD_IN`, `TYPMOD_OUT`, `ANALYZE`, `SUBSCRIPT`, and `STORAGE`** are changed in place, with `ALTER TYPE ... SET`, whatever uses the type.
- **Any other change** (`INPUT`, `OUTPUT`, `INTERNALLENGTH`, `ALIGNMENT`, `CATEGORY`, `ELEMENT`, ...) drops the type and creates it again, its functions with it, when
  nothing else uses it. When something does (a column, a domain, a function that is not one it names), `publish` refuses before anything runs and names it:
  change that first, or make the change by hand.
- **A removed type** is dropped with `DROP TYPE ... CASCADE`, which drops its support functions too: they and the type depend on each other, so neither can go first.
  `CASCADE` would also drop anything else that uses the type, a column included, so the statement before it stops the publish, naming what is left, unless the
  type's own functions are all that depends on it. What the project removes with it is dropped first. Removing a base type is not destructive in itself; a
  column of it is.

## How it is compared

Every option is compared, with PostgreSQL's default for one the definition does not state (`INTERNALLENGTH = VARIABLE`, `ALIGNMENT = int4`, `STORAGE = plain`,
`CATEGORY = 'U'`, `DELIMITER = ','`, not `PASSEDBYVALUE`, `PREFERRED`, or `COLLATABLE`, and `SUBSCRIPT = raw_array_subscript_handler` with an `ELEMENT` and no
`SUBSCRIPT`). So a type written with its defaults, or without them, is the same type. A function is compared with its schema: an unqualified one is the
project's in the type's schema, then on the project's `searchPath`, else PostgreSQL's. A type (`ELEMENT`, `LIKE`) is compared as a column's type is, so `int4` and
`integer` are one.

`LIKE = t` copies `t`'s `INTERNALLENGTH`, `PASSEDBYVALUE`, `ALIGNMENT`, and `STORAGE`, and a database stores those values, not the `LIKE`. Against a database the
server says what they are; between two projects `LIKE` is compared as written. An option written beside `LIKE` stands.

## In the database and in `init`

A base type is read with every option as PostgreSQL stores it, and its support functions are read as functions. Not read: the array type PostgreSQL makes for
it, an extension's base types, and a shell left without a definition (`init` names it). `init` writes `Schema/<schema>/Type/<name>.sql` with the shell, the
support functions, and the definition, every option explicit, in that order; the functions are in no file of their own. The type's owner, comment, and grants
are read and written as for any type ([type privileges](type-privileges.md)).

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
