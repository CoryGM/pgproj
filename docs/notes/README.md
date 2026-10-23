# pgproj notes

How pgproj treats each part of a PostgreSQL schema: what it models, how it compares and deploys it, and its limits. The command
reference is [`docs/commands.md`](../commands.md), and the project file [`pgproj.json.md`](../../pgproj.json.md).

## Workflows

How to use pgproj beyond one project deployed to one database. The three ways in are in the [README](../../README.md#three-ways-to-use-it), and the connection options in
[Connections](../../README.md#connections).

- [Schema-per-tenant databases](tenants.md) — one template for every tenant's schema, and where the list of tenants comes from
- [Promoting between environments](promoting-environments.md) — a live database as the desired state: `--source-connection`, `--target-connection`, parity checks, and CI
- [CI/CD](../ci.md) — checking every pull request, catching drift, and deploying with a review, with GitHub Actions workflows
- [Troubleshooting](../troubleshooting.md) — what a message means and what to do about it
- [Why pgproj?](../why-pgproj.md) — pgproj side by side with EF Core migrations, Flyway, Atlas, and pgCodeKeeper, and which fits which situation
- [Using pgproj as a library](../library.md) — `PgProj.Engine` for an editor, a language server, or a web service: the file sources, the calls, their results, and the JSON document

## The basics

- [PostgreSQL versions](postgresql-versions.md) — 14 to 18, what each newer version adds, and the PostgreSQL 18 features pgproj supports
- [What is supported](supported-objects.md) — the object kinds pgproj models end to end, and those it does not
- [Statements that are not modeled](unmodeled-statements.md) — what `build` warns about and ignores (a statement, or a clause of one), and how passwords are masked
- [Order](order.md) — which ordering is a change and which is not
- [Identifiers](identifiers.md) — quoted names and case
- [Schemas and unqualified names](schemas.md) — `public`, `searchPath`, and names written without a schema
- [How `schemas` scopes `diff` and `publish`](schema-scoping.md) — sharing a database with other projects

## Checking a project

- [References build cannot resolve](unresolved-references.md) — when a missing table, type, function, or column is an error or a warning
- [Function calls](function-calls.md) — built-in, project, and extension functions
- [Query bodies](query-bodies.md) — what views and SQL routine bodies read, and the order that follows from it
- [PL/pgSQL bodies](plpgsql-bodies.md) — the names a PL/pgSQL body uses that the project does not define: a warning, never an order
- [Deeply nested statements](statement-depth.md) — how deep a statement can be, and the errors past that

## Deploying

- [Destructive changes](destructive-changes.md) — what `publish --execute` refuses without `--allow-destructive`
- [Dependent objects](dependent-objects.md) — what `publish` drops and recreates around a change
- [`ALTER TABLE` in project SQL](alter-table.md) — which actions are applied
- [Publishing only the comments](comments-only.md) — `publish --comments-only`
- [Statements only a superuser can run](superuser-statements.md) — `publish --allow-superuser-statements`
- [Locks and large tables](locks.md) — the warning before a rewrite or a long lock, `publish --lock-timeout`, and `publish --concurrently`
- [Entity relationship diagrams](erd.md) — `--erd`: the files, what is drawn, and a file already there
- [Excluding grants, role memberships, and owners](excluding-grants-and-owners.md) — `excludeGrants`, `excludeRoleMemberships`, `excludeOwners`

## Cluster-wide objects and ownership

- [Roles](roles.md) — reported, not modeled; the check before `publish --execute`
- [Tablespaces](tablespaces.md) — the `TABLESPACE` clause, and the check before `publish --execute`
- [Owners](owners.md) — `OWNER TO`, and why an owner the project does not state is not compared

## Tables, columns, and types

- [`NOT NULL`](not-null.md) — named and PostgreSQL 18 `NOT NULL` constraints
- [Generated columns](generated-columns.md) — stored and virtual
- [Identity columns](identity-columns.md) — options as written; generation, options, adding and dropping changed in place
- [Storage, partitioning, and comments](storage-partitioning-and-comments.md) — storage parameters, partitions, and which comments are modeled
- [Typed tables](typed-tables.md) — `CREATE TABLE ... OF type`
- [NOT VALID constraints](constraint-validity.md) — a table's `CHECK` or `FOREIGN KEY`, and a domain's `CHECK`, added without checking the old rows
- [Table inheritance](inheritance.md) — `CREATE TABLE ... INHERITS (...)` and `CHECK ... NO INHERIT`
- [Unnamed constraints and indexes](unnamed-objects.md) — the names PostgreSQL gives them, and when `build` warns
- [Tables written with LIKE](table-like.md) — `CREATE TABLE ... (LIKE source INCLUDING ...)` as written, and what it copies
- [Composite types](composite-types.md) — changes made in place, and the retype limitation
- [Base types](base-types.md) — `CREATE TYPE ... (INPUT = ..., OUTPUT = ...)` is not modeled; its functions are left out with it
- [Views, and other definitions PostgreSQL rewrites](views.md) — comparing by PostgreSQL's own rendering
- [SQL-standard routine bodies](sql-standard-routine-bodies.md) — `BEGIN ATOMIC` and `RETURN`
- [Routine options](routine-options.md) — `SET`, `COST`, `ROWS`, `PARALLEL`, `LEAKPROOF`, and `WINDOW`
- [What is deployed is what you wrote](deployed-as-written.md) — your text is deployed, a normalized form is only compared

## Other object kinds

- [Triggers](triggers.md)
- [Event triggers](event-triggers.md)
- [Extensions](extensions.md) — deployed as written; schema, version, and comment compared only when stated
- [Row-level security policies](row-level-security.md)
- [Casts](casts.md)
- [Operators](operators.md)
- [Rules](rules.md)
- [Aggregates](aggregates.md)
- [Collations](collations.md)
- [Extended statistics](extended-statistics.md)
- [Text search](text-search.md)
- [Publications](publications.md)

Renaming a column is covered in [Renaming columns](../renaming-columns.md).
