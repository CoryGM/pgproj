# Views, and other definitions PostgreSQL rewrites

## View options

A view's options are modeled: `WITH (security_barrier = true)`, `WITH (security_invoker = on)`, and `WITH [LOCAL | CASCADED] CHECK OPTION`.

- They are written into the view as you wrote them, an option set to its default included, and read back from the database, so `init` scaffolds them.
- They are compared in a canonical form, so `on`, `true`, and `1` are one option, and an option stated as false is the same as none.
- A change to only the options replaces the view in place with `CREATE OR REPLACE VIEW`, which sets the whole list, so nothing that uses the view is
  dropped.

## Defaults on a view's columns

A default set on a view's column (`ALTER VIEW v ALTER COLUMN c SET DEFAULT expr`, or `ALTER TABLE ONLY v ...` as `pg_dump` writes it) is part of the
view: an insert through an updatable view uses it for a column the statement leaves out.

- It is deployed as written with `ALTER VIEW ... SET DEFAULT` after the view, and read back from the database, so `init` scaffolds it.
- It is compared as a table column's default is, always, in a canonical form: `100` and `(100)` are one default, and `DEFAULT NULL` is none, as
  PostgreSQL stores it.
- A changed or removed default is changed in place (`SET DEFAULT`, `DROP DEFAULT`), with the view replaced by `CREATE OR REPLACE VIEW`, which keeps the
  others. A view dropped and created again for another reason gets its defaults back.
- A materialized view has no column defaults.

Not handled: the default is compared by its canonical text, not by PostgreSQL's rendering as a table column's is, so a typed literal PostgreSQL rewrites
in a way the canonical form does not fold can show as modified; and a view rebuilt around a change from the database's own definition (not in the
project) loses its defaults.

## Materialized views

**Indexes.** A materialized view can have indexes (`CREATE [UNIQUE] INDEX ... ON schema.mv (...)`, with expressions and a `WHERE` predicate). They are
created after the view, compared, changed, and removed like a table's, and `init` writes them into the view's file.

- A change that drops the view and creates it again also drops its indexes and creates them again after it, in the same publish, so the unique index
  `REFRESH MATERIALIZED VIEW CONCURRENTLY` needs is still there. A change to only the view's comment keeps the view and its indexes.
- The view's output columns are not modeled, so an index's column names are not checked against the view's query.

**Storage.** Storage parameters (`WITH (fillfactor = 50)`) and `WITH NO DATA` are deployed as written.

- The parameters are compared as a set, as PostgreSQL stores them, and a change is made in place with `ALTER MATERIALIZED VIEW ... SET (...)` and
  `RESET (...)`, without rebuilding the view.
- `WITH NO DATA` only says how the view is created, so it isn't compared: a view created that way and refreshed since is no change. `init` writes
  `WITH NO DATA` for a materialized view that hasn't been populated.

**`USING` and `TABLESPACE`** are deployed as written too. The tablespace is compared as a table's is ([tablespaces](tablespaces.md)); the access method
only when you state one, since the default is the server's `default_table_access_method`. A change to either is made in place, with
`ALTER MATERIALIZED VIEW ... SET TABLESPACE` or `SET ACCESS METHOD`. As for a table, a tablespace no longer stated is a `-- TODO` that
`publish --execute` refuses, since PostgreSQL has no `RESET TABLESPACE`. `init` writes the access method only when it isn't the server's default.

## Definitions PostgreSQL rewrites

PostgreSQL stores many definitions in its own rendering, not as written: a view loses a `public.` qualifier and gains aliases and casts, and `*` is
expanded. Comparing your text with that rendering would report a change on every run. So for each definition below that also exists in the database,
`diff` and `publish` have the server render the project's version too, on a temporary object inside a transaction that is always rolled back, and
compare PostgreSQL's rendering of the two.

| Definition | Rendered on | Why PostgreSQL rewrites it |
|---|---|---|
| A view's query | a temporary view | drops `public.`, adds aliases and casts, expands `*` |
| A partition's `FOR VALUES` bound | a temporary copy of the live parent | literals in the key's type: `'2026-01-01'` on a `timestamptz` key is stored as `'2026-01-01 00:00:00+00'` |
| A column `DEFAULT` | a temporary column of the live column's type | `0::numeric` is stored as `(0)::numeric`, `1.5::numeric` as `1.5` |
| A generated column's expression | a temporary copy of the live table | implicit casts: `qty * price` over `integer` and `numeric` is stored as `((qty)::numeric * price)` |
| A partial index's `WHERE` predicate | a temporary copy of the live table | each literal typed by its column: `status <> 'cancelled'` on an enum is stored as `status <> 'cancelled'::my_enum` |
| A `CHECK` constraint's expression | a temporary copy of the live table | typed literals, `IN` lists as `= ANY (ARRAY[...])`, a domain column cast to its base type |
| A domain's own `CHECK` | the live domain, as a scratch `NOT VALID` constraint | added casts: `length(VALUE)` on a `varchar` domain is stored as `length((VALUE)::text)` |

So a definition written differently from how PostgreSQL prints it is not a change, and a materialized view isn't rebuilt on every publish. A default
written with a cast (`'{}'::text[]`, `'new'::my_enum`), which is how `pgproj init` writes defaults, builds without diagnostics.

This needs the default `TEMPORARY` privilege on the database. Without it, or for a view whose new body can't be created against the database as it is
(it uses a table this publish adds), the definition is compared by its text instead, which can show an unchanged one as modified. A domain the
connecting role does not own is compared by its text.

**Partitions.** The primary, unique, and foreign keys, `CHECK` constraints, and indexes PostgreSQL copies from a partitioned table onto each partition
belong to the parent's declaration, so they are not read as the partition's own, and `pgproj init` does not scaffold them into the partition's file.

## Views that refer to each other

`CREATE OR REPLACE VIEW` lets two views read each other: create `v2`, create `v3` over it, then replace `v2` with a body that reads `v3`. No creation order works, so `pg_dump` breaks the cycle with a placeholder, `CREATE VIEW v2 AS SELECT NULL::bigint AS a`, and replaces it once `v3` exists. pgproj does the same:

- A view the project **replaced** keeps its first definition (the earlier body, or the `pg_dump` placeholder). When the views form a cycle, the first one by name that was replaced is created from that first definition, the others follow, and it is replaced by its real body after the last of them. The comment, grants, and options of the view go with the real body.
- `init` scaffolds a cycle the same way: the first view by name gets a `CREATE VIEW ... AS SELECT NULL::type AS column, ...` placeholder in its file, with the column types read from the database, before its real `CREATE OR REPLACE VIEW`.
- A cycle none of whose views was replaced in the project is still an error, as is a cycle with a materialized view (which cannot be replaced).
- Not handled: a view of a cycle that already exists in the database and is changed is replaced in place without this ordering.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
