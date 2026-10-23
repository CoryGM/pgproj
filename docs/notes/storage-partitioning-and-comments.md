# Storage, partitioning, and comments

These are supported:

- **Storage:** a table's storage parameters, a materialized view's ([views](views.md)), an index's, and those of a primary key's, unique constraint's,
  or exclusion constraint's index (`WITH (fillfactor = 70)`); `UNLOGGED`; `TABLESPACE`; a table's `USING` access method; a column's `STORAGE` and
  `COMPRESSION`; a sequence's `UNLOGGED`.
- **Partitioning:** `PARTITION BY` and `PARTITION OF`.
- **Comments** on schemas, tables, columns (of a table, a view, a materialized view, or a composite type), table constraints, triggers, row-level
  security policies, views, materialized views, indexes, sequences, types (a domain's too, set with `COMMENT ON TYPE` or `COMMENT ON DOMAIN`),
  extensions, functions, procedures, collations, extended statistics, and text search dictionaries and configurations. A comment on any other kind is
  reported as not modeled.

[What is supported](supported-objects.md) lists every object kind.

## Storage

**A table's `USING` access method** (and a materialized view's, see [views](views.md)) is deployed as written and compared only when the project states
one, since the default is the server's `default_table_access_method`. A change is made in place with `ALTER TABLE ... SET ACCESS METHOD`, which
rewrites the table; `init` writes it only when it isn't the default.

**A column's `STORAGE` and `COMPRESSION`** in `CREATE TABLE` (`a text STORAGE main COMPRESSION lz4`) are deployed as written and compared only when the
project states them:

- `STORAGE default` (the type's storage) is not compared, and `COMPRESSION default` means the column has none of its own.
- A change is made in place with `ALTER TABLE ... ALTER COLUMN ... SET STORAGE` or `SET COMPRESSION`, which leave the rows already stored as they are.
- `init` writes a storage only when it isn't the type's.
- The `ALTER TABLE ... SET STORAGE` and `SET COMPRESSION` statements in a project are still reported as not modeled.

**An index's storage parameters**, and those of a key or exclusion constraint's index, are compared as a list of `name=value`: the order, the spacing
around `=`, and the quotes PostgreSQL prints around a value (`fillfactor='70'`) are not differences. A change is made in place with
`ALTER INDEX ... SET (...)` and `ALTER INDEX ... RESET (...)`, which keeps the index (a constraint's index has the constraint's name); a new `fillfactor`
applies to pages written from then on, not to the existing ones. A parameter set on the database's index that the project does not state is reset.

**A sequence's `UNLOGGED`** (`CREATE UNLOGGED SEQUENCE`) is deployed, compared, and changed in place with `ALTER SEQUENCE ... SET LOGGED | UNLOGGED`;
a serial's or an identity's sequence on an unlogged table is unlogged, as PostgreSQL makes it.

## Partitioning

**A partition key column** keeps its `COLLATE` and operator class (`PARTITION BY LIST (c COLLATE "C")`, `PARTITION BY RANGE (a text_pattern_ops)`),
deployed as written and compared element by element: the database's `pg_catalog."C"` is the project's `"C"`, an operator class is compared when the
project states one, and a class in `pg_catalog` or `public` is the same with or without its schema. A key written with an expression keeps its `COLLATE`
as part of the expression's text.

**A partition's element list** gives the columns it takes from its parent options of its own, and constraints:

```sql
CREATE TABLE child PARTITION OF parent (
    b WITH OPTIONS DEFAULT 5,
    a CONSTRAINT child_an NOT NULL,
    CONSTRAINT child_ck CHECK (b > 0)
) FOR VALUES ...;
```

- The list is deployed as written, in the partition's `CREATE TABLE`, and what it states is in the model: a column's default and NOT NULL (with its
  name) are the partition's own, as a child's default set with `ALTER TABLE ONLY` is ([`ALTER TABLE` in project SQL](alter-table.md)), and a constraint
  is the partition's like any other; it is not created a second time.
- Once the partition exists, a change to the list is made in place (`ALTER COLUMN ... SET DEFAULT`, a dropped and added constraint).
- A NOT NULL written as a table constraint in the list, `CONSTRAINT n NOT NULL col`, is a form only PostgreSQL 18 accepts, so `publish --execute`
  refuses it on an older server and names it; the column form, `col CONSTRAINT n NOT NULL`, is accepted by every supported version.
- An option for a column the parent does not have is a warning.
- A NOT NULL written in the list is the partition's own constraint, while one a partition takes from its parent is inherited; the two are not told
  apart.

`init` does not write this form back for a default or a named NOT NULL: it writes `ALTER TABLE ONLY ... SET DEFAULT`, and on PostgreSQL 18 a partition
with its own NOT NULL names is created with its columns and attached ([`NOT NULL`](not-null.md)). A partition column that is NOT NULL while its parent's
is not is written in the list (`PARTITION OF parent (a NOT NULL)`), since before PostgreSQL 18 nothing else says so.

## Comments

Most comments are compared like any other property. These follow their own rules:

- **A schema's comment** is compared only when the project states one with `COMMENT ON SCHEMA` (`IS NULL` states that it has none): a comment on a
  schema the project does not mention is left alone, `public`'s default comment is not scaffolded or compared, and a schema with a comment is created
  by `publish` if the database lacks it.
- **An extension's comment** follows the schema's rule: it is compared only when the project states one, and the comment an extension comes with is not
  scaffolded ([Extensions](extensions.md)).
- **A constraint's comment** (`COMMENT ON CONSTRAINT name ON table`; not a domain's constraint) is compared only when the project states one; `publish`
  sets, changes, or clears it without dropping the constraint, and a constraint that is dropped and added again for another reason gets its comment back
  (the project's, or the database's when the project states none).
- **A trigger's comment** (`COMMENT ON TRIGGER name ON table`; not an event trigger's) and **a policy's comment** (`COMMENT ON POLICY name ON table`)
  follow the same rule as a constraint's.
- **A key's index:** the index of a primary key, unique, or exclusion constraint has the constraint's name, and `COMMENT ON INDEX t_pkey IS '...'`
  comments it (`LIKE ... INCLUDING COMMENTS` copies such a comment too). The comment is kept on the constraint, not as an index of its own: it is
  deployed with `COMMENT ON INDEX` after the key is added, changed or removed in place without touching the key (`IS NULL` removes it), read back from
  the database so `init` scaffolds it, and compared only when the project states it, as a constraint's comment is. A comment on an ordinary index is
  compared like any other.
- **A column of a view, a materialized view, or a composite type** (`COMMENT ON COLUMN v.a`) is compared like a table column's comment, changed in
  place without rebuilding the view or the type, put back when a view is dropped and created again for a dependency, scaffolded by `init`, and shown by
  `document`.

To deploy only the comments of a project, without any other change, use [`publish --comments-only`](comments-only.md).

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
