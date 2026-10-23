# What is supported

pgproj reads each kind below from a project's SQL files and from a live database, compares the two, deploys the difference, and writes it
back with `init`. What is deployed is the text you wrote; a normalized form is kept only for comparison ([what is deployed](deployed-as-written.md)). The
order of a publish is dependency-safe ([order](order.md)), and what the project changes under an object in use is dropped and recreated around the change
([dependent objects](dependent-objects.md)). Each kind is checked against real PostgreSQL 14, 15, 16, 17, and 18 servers
([PostgreSQL versions](postgresql-versions.md)).

## Modeled

| Kind | What is covered |
|---|---|
| Schemas | A declared schema is created even with nothing in it; none is ever dropped ([schemas](schemas.md)); its comment and owner. |
| Tables and columns | Types with their modifiers, nullability, defaults, identity ([identity columns](identity-columns.md)), stored and, on 18, virtual generated columns ([generated columns](generated-columns.md)), collation, `STORAGE` and `COMPRESSION`, type changes (a widening is not destructive), and renames declared with `-- @renamed-from` ([renaming columns](../renaming-columns.md)). `UNLOGGED`, storage parameters, `TABLESPACE`, and `USING` ([storage, partitioning, and comments](storage-partitioning-and-comments.md)). |
| Partitioning | `PARTITION BY`, `PARTITION OF` with its bound and element list, a partition written as `CREATE TABLE` plus `ATTACH PARTITION`, and a partition's own index, key, and exclusion constraint names ([storage, partitioning, and comments](storage-partitioning-and-comments.md), [`ALTER TABLE`](alter-table.md)). |
| Inheritance, typed tables, `LIKE` | `INHERITS` ([table inheritance](inheritance.md)), `CREATE TABLE ... OF type` ([typed tables](typed-tables.md)), and `LIKE ... INCLUDING` ([tables written with `LIKE`](table-like.md)). |
| Constraints | Primary key, unique, foreign key (with `MATCH` and `ON DELETE SET NULL (columns)`), `CHECK`, exclusion; a key's `INCLUDE` and index storage parameters; `NULLS NOT DISTINCT`; deferrability; `NOT VALID` ([constraint validity](constraint-validity.md)); PostgreSQL 18's `NOT ENFORCED` and temporal keys; unnamed ones under PostgreSQL's names ([unnamed objects](unnamed-objects.md)). |
| `NOT NULL` | Including PostgreSQL 18's named and `NOT VALID` NOT NULL, `NO INHERIT`, and a child's or partition's own names ([`NOT NULL`](not-null.md)). |
| Indexes | Expressions, predicates, `INCLUDE`, `NULLS NOT DISTINCT`, operator classes and their options, collation, ordering, storage parameters; an `INVALID` index is reported and rebuilt ([locks](locks.md)). |
| `ALTER TABLE` table settings | `CLUSTER ON` and `SET WITHOUT CLUSTER`, `REPLICA IDENTITY`, the firing mode of triggers and rules (`ENABLE`/`DISABLE [REPLICA|ALWAYS] TRIGGER|RULE`, `TRIGGER USER` and `TRIGGER ALL`, and a trigger's copy on a partition), and row-level security's `ENABLE`/`FORCE` ([`ALTER TABLE`](alter-table.md)). |
| Sequences | Options, `OWNED BY`, `UNLOGGED`; a changed start is recorded, never restarted. |
| Views and materialized views | Compared by PostgreSQL's own rendering; options (`security_barrier`, `security_invoker`, check option), column name lists, column comments and defaults, recursive views, views that read each other; a materialized view's indexes, storage parameters, `WITH NO DATA`, `USING`, and `TABLESPACE` ([views](views.md), [query bodies](query-bodies.md)). |
| Functions and procedures | Overload-safe identity, verbatim bodies, SQL-standard bodies ([SQL-standard routine bodies](sql-standard-routine-bodies.md)), `RETURNS TABLE` columns, any language, and their [options](routine-options.md), `SET search_path` included; dependencies from the bodies ([function calls](function-calls.md)). |
| Types | Enums (labels added in place), composite types (fields changed in place), range types (with `MULTIRANGE_TYPE_NAME`), and domains (default, `NOT NULL`, `CHECK`s, collation) ([composite types](composite-types.md)). |
| Triggers | `WHEN`, `UPDATE OF`, arguments, constraint triggers, transition tables, firing mode ([triggers](triggers.md)). |
| Row-level security | Policies and a table's `ENABLE`/`FORCE ROW LEVEL SECURITY` ([row-level security](row-level-security.md)). |
| Grants and owners | Grants on schemas, tables, views, sequences, routines, and columns, `ALL ... IN SCHEMA`, `WITH GRANT OPTION`, default privileges, role memberships ([excluding grants and owners](excluding-grants-and-owners.md)); owners stated with `OWNER TO` ([owners](owners.md)). |
| Comments | On schemas, tables, columns (of tables, views, materialized views, and composite types), constraints, triggers, policies, views, indexes, sequences, types and domains, extensions, routines, collations, statistics, and text search objects ([storage, partitioning, and comments](storage-partitioning-and-comments.md)). |
| Other kinds | [Extensions](extensions.md), [event triggers](event-triggers.md), [casts](casts.md), [operators](operators.md), [aggregates](aggregates.md), [rules](rules.md), [publications](publications.md), [collations](collations.md), [extended statistics](extended-statistics.md), and [text search dictionaries and configurations](text-search.md). |
| Schema-per-tenant databases | A template applied to each tenant's schema ([tenants](tenants.md)). |

## Not modeled

These are reported by `build` when a project states them, and not read from a database by `diff`, `publish`, or `init`
([statements that are not modeled](unmodeled-statements.md)):

- roles and their settings, tablespaces, databases and database-level settings, and subscriptions (cluster-wide: a grant, membership, or owner naming a
  role and a table's `TABLESPACE` are modeled, and `publish` checks they exist; [roles](roles.md), [tablespaces](tablespaces.md));
- foreign data wrappers, servers, user mappings, and foreign tables;
- operator classes and families, access methods, transforms, languages, conversions, text search parsers and templates, and security labels;
- base and shell types, with the functions they name ([base types](base-types.md));
- comments on event triggers, rules, operators, aggregates, casts, publications, and a domain's constraints;
- the `ALTER` forms the [statements page](unmodeled-statements.md) lists: a project states each object's final form in its `CREATE`.

An index PostgreSQL marks `INVALID` is reported by `diff` and rebuilt by `publish`, but `init` leaves it out, as `pg_dump` does.

Each kind's limits are on its page in the [notes](README.md), and the [changelog](../../CHANGELOG.md) lists what each release adds.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
