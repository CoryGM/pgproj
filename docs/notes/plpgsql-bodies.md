# PL/pgSQL bodies

`build` reads the body of a PL/pgSQL function or procedure (`LANGUAGE plpgsql`) and **warns** about a table, view, or function it names that the project does not define. It is
the same kind of check a `LANGUAGE sql` body gets ([Query bodies](query-bodies.md)), with two differences that follow from how PostgreSQL treats PL/pgSQL:

- **It is a warning, never an error.** PostgreSQL does not look up the names in a PL/pgSQL body when it creates the routine, only when it runs. A body may name a temporary table
  it creates, build SQL as a string, rely on a `search_path` set while it runs, or use an object another system provides (an extension's table, another project's schema).
- **It never changes an order.** What a body names is not a dependency. Two PL/pgSQL routines may call each other, a body may name a table created later, and
  `publish` creates objects in the same order it would if the body were empty. (A `DECLARE` section's types and `%ROWTYPE` tables are the one exception, and are older:
  [Order](order.md).)

```
$ pgproj build
[Warning] The PL/pgSQL body of function 'public.get_customer_balance(int4,timestamptz)' calls function 'if', which the project does not define.
[Warning] The PL/pgSQL body of procedure 'public.archive()' names table or view 'audit_log', which the project does not define.
```

## What is read

libpg_query, the parser pgproj already uses, parses the body into its statements, and each statement that runs SQL gives its text. That text is parsed as SQL and read like a
view's query: the tables and views in `FROM`, `INSERT INTO`, `UPDATE`, `DELETE`, and `MERGE`; the functions and procedures called (`PERFORM f()`, `CALL p()`, calls in expressions
and conditions); with names resolved as PostgreSQL resolves them, a qualified name in its schema and an unqualified one along the `searchPath`.

## What is not reported

A warning should be believable, so a name is left alone when the body can make it true:

- a relation the body creates (`CREATE TEMP TABLE`, `CREATE TABLE ... AS`);
- a **transition table** a trigger names (`REFERENCING NEW TABLE AS new_rows`);
- anything inside `EXECUTE` (the SQL is a string built at run time);
- a relation in PostgreSQL's own schemas (`pg_catalog`, `information_schema`, `pg_stat_activity`);
- a **qualified name in a schema the project has nothing in** (`cron.job`): it belongs to another system, such as an extension;
- a sequence, a built-in function, and a procedure or aggregate the project defines.

A name in a schema the project does have (`public.missing`) is reported when it is not there. A variable, a parameter, `NEW`, and `OLD` are never reported: only a name used as a
table, view, or function is looked at, and those are never variables.

On the real-world schemas pgproj is tested against (GitLab, Synapse, MusicBrainz, Supabase, AdventureWorks, Pagila, and others, about 2,500 PL/pgSQL routines), the check reports
one name, and it is a real defect in the source (Pagila calls MySQL's `IF()` in a query).

## Limits

- **A body libpg_query cannot read has no references, and nothing is said.** It writes a trigger function's special variables as broken JSON (pgproj reads the body's statements
  around the damage) and stops at `RETURN QUERY` and `RETURN NEXT` (pgproj reads the body again with those two statements written as plain ones, which keeps every query and
  expression). A body that still cannot be read, for example one with a syntax error, is reported by PostgreSQL when it is created.
- **Types are not checked.** A type named in a `DECLARE` section is ordered by the dependency analyzer; one that does not exist is not reported here.
- **A column is not checked.** Only relations and functions are.
- **A function is matched by name,** not by its arguments, as a call in a `LANGUAGE sql` body is.
- A table or function that exists only in the database (the project does not define it) is reported, because the project does not define it; `diff` and `publish` leave such an object alone.
- The routine is not rebuilt when something it names changes (see below).

## When a change drops what a body names (`diff` and `publish`)

PostgreSQL records no dependency for a PL/pgSQL body, so it lets you drop a table a body reads, and the routine fails the next time it runs. When a change **drops, renames, or retypes**
something a routine's body names, `diff` and `publish` say so (a warning; the script and its order are unchanged):

```
[Warning] This publish drops, renames, or retypes what the body of a PL/pgSQL routine names. PostgreSQL records no dependency for such a body and looks its names up only when the routine runs, so nothing stops this and the routine fails then:
  - function 'public.audit_count()' names table 'public.audit_log' is dropped.
  - function 'public.report()' names column 'public.orders.total' is given another type.
```

What it covers: a table or view that is removed; a function or procedure that is removed (an overload or replacement of the same name is not a removal); and a column of a table, named
through its qualifier (`o.total`, `orders.total`), that is dropped, renamed (`-- @renamed-from`), or given another type. An unqualified column (`SELECT total FROM orders`) is not read.

pgproj does **not** rebuild the routine. A PL/pgSQL function survives a table being dropped and created again (a changed table is a modification, not a drop), and recreating a function
cannot make a body valid whose table is gone, so a rebuild would add risk (the function's triggers, policies, and grants would have to be rebuilt too) and fix nothing. The note is for you
to change the routine in the same publish, or to confirm the drop is meant. `--comments-only` says nothing.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
