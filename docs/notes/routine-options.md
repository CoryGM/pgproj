# Routine options

A function's or procedure's options are deployed as written (a `SET` clause exactly as the project spells it), read back from `pg_get_functiondef`, compared, and scaffolded by `init`: `LANGUAGE`, the volatility (`IMMUTABLE`, `STABLE`, `VOLATILE`), `STRICT`, `SECURITY DEFINER`, `LEAKPROOF`, `PARALLEL`, `COST`, `ROWS`, `WINDOW`, `SUPPORT`, `TRANSFORM FOR TYPE`, and every `SET` clause. A `SECURITY DEFINER` function written with `SET search_path = ...` keeps that pinned path, as PostgreSQL's documentation advises for such functions.

A change to any of them but `WINDOW` is made with `CREATE OR REPLACE`, which sets every option, so one the project no longer states is reset to PostgreSQL's default: a `SET search_path` removed from the project is removed from the function. `CREATE OR REPLACE` cannot turn an ordinary function into a window function or back, so that change drops and creates the function.

How they are compared:

- A clause stating PostgreSQL's default is the same as none: `PARALLEL UNSAFE`, `NOT LEAKPROOF`, `COST 100` (`COST 1` for a C or internal function), `ROWS 1000` on a set-returning function, and `SET name TO DEFAULT`.
- `COST` and `ROWS` are compared as PostgreSQL stores them, a `real` printed with six significant digits.
- `SET` clauses are compared by setting name (in any case), in any order; when one name is set twice, the last value is the one PostgreSQL keeps. The values are compared as text, without their quotes, so `SET search_path = pg_catalog, "My S"` and PostgreSQL's `SET search_path TO 'pg_catalog', 'My S'` are the same, while the order of the schemas in a `search_path` is a change. A value is not normalized: `'64MB'` and `'65536kB'` differ.
- `SET name FROM CURRENT` stores the value the setting had when the routine was created, so it is compared by name alone.

**Languages:** a routine may be written in any language. `sql` and `plpgsql` bodies are read for what they use. `internal` and `c` name compiled code; a C function's `AS 'library', 'symbol'` keeps its link symbol, which is compared. A body in any other language (`plperl`, `plpython3u`, `pltcl`, `plv8`, ...) is deployed and compared as written: a change to the code is a change, whitespace alone is not. pgproj doesn't read it, though, so the objects it uses aren't ordered before it, and `build` warns once per such routine. A language the server lacks fails at publish with PostgreSQL's own error, and the publish is rolled back. The language's extension (`CREATE EXTENSION plperl`) is created before the routine when the project declares it, as written ([Extensions](extensions.md)).

`SUPPORT` is compared by the function's name, without a `pg_catalog` or `public` schema, and `TRANSFORM` by its types, in any order. A support function is not a dependency of the routine: it must be written in C, which pgproj does not accept, so it is always a built-in one. `TRANSFORM` applies to procedural languages such as PL/Perl, which pgproj accepts. `LEAKPROOF` can only be set by a superuser, and so can a routine in an untrusted language (`c`, `internal`, `plpython3u`, ...): `publish --execute` creates or replaces one only with `--allow-superuser-statements`, and refuses a role that is not a superuser before anything runs ([statements only a superuser can run](superuser-statements.md)).

**Procedures that differ by an `OUT` parameter.** From PostgreSQL 14 a procedure's `OUT` parameters are part of its signature, so `p(IN a int, IN b int)` and `p(OUT a int, IN b int)` both exist, and a statement that names one by its types alone (`ALTER PROCEDURE p(integer, integer)`) is refused as not unique. A procedure that shares its name with one that has an `OUT` parameter is named with every parameter's mode in `ALTER`, `COMMENT ON`, `GRANT`, and `DROP` (`p(IN int, IN int)`); every other procedure is named by its input types as before.

**A blank body.** A function or procedure whose body is blank (`AS $p$ $p$`, a stub or a placeholder) is deployed as written. A routine whose body was not read at all (nothing after `AS`) still cannot be written, and the script says so.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
