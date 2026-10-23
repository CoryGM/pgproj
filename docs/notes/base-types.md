# Base types

A base type, `CREATE TYPE name (INPUT = ..., OUTPUT = ...)`, is not modeled. Its input and output functions are written in C or are `internal` functions, and PostgreSQL lets only a superuser create a base type, its shell (`CREATE TYPE name;`), or an `internal` function. A base type nearly always comes from an extension, and an extension's types are the extension's, not the project's ([extensions](extensions.md)).

**In a project**, `build` warns about the type and leaves it out:

```
sql/schema.sql:1:1-1:25: [Warning] CREATE TYPE (shell type) is not modeled; the statement is ignored: CREATE TYPE public.xint4
sql/schema.sql:4:1-7:2: [Warning] CREATE TYPE (base type) is not modeled (a base type needs C or internal functions and a superuser); the statement and the functions it names are ignored: CREATE TYPE public.xint4 (...
[Warning] Base type public.xint4 is not modeled, so the functions it names are left out with it: public.xint4in, public.xint4out.
```

- **The functions the definition names** (`INPUT`, `OUTPUT`, `RECEIVE`, `SEND`, `TYPMOD_IN`, `TYPMOD_OUT`, `ANALYZE`, `SUBSCRIPT`) are left out with it, wherever they are written, together with their owners, comments, and grants. An unqualified function name is the one in the type's schema.
- **What uses the type** (a column, a cast, an operator, a domain) stays in the project. `build` reports the type as missing, and `publish` deploys what uses it, which works only once the type exists.
- **A project written the way `pg_dump` writes a base type** (a shell, the functions, then the full definition) used to fail `build` with `Duplicate Type definition`. It builds now, with the warnings above.

**In `init` and the live read**, a base type, or a shell type left without a definition, is not read:

- `init` leaves the type and its support functions out, and says so: `Left out a base type, with its input and output functions: public.xint4.` The `--report` lists it under "Not modeled: not supported". The array type PostgreSQL makes for it is not listed.
- `diff` and `publish` do not read a base type's support functions from the database either, so they are neither proposed for dropping nor compared.

**To use a base type with pgproj**, create it before publishing, as a superuser: by hand, or in a `predeploy` script run by a superuser that creates it only when it does not exist yet (the script runs on every publish). pgproj does not model base types yet.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
