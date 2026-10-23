# Why pgproj? Compared with migrations

Most .NET teams already manage a database schema somehow: EF Core migrations, Flyway, DbUp, Liquibase, or folders of numbered scripts. This page
explains how pgproj differs, when it fits, and when one of those fits better.

## Two ways to change a schema

**Migration-based** tools keep a list of changes. Each change is a script, or a class, that moves the database from one version to the next, and the
tool records which ones a database has run. To add a column you write a new migration that adds it, by hand or by having a tool generate it.

- *EF Core migrations* generate a C# migration class from the difference between your model and a snapshot of the last one, and record the applied ones
  in `__EFMigrationsHistory`.
- *Flyway*, *DbUp*, and similar tools run versioned SQL scripts you write, once each, in order, and record them in a history table.
- *Liquibase* runs changesets from a changelog (SQL, XML, YAML, or JSON) and records them too.

**State-based** tools keep the schema itself. The files say what the database should look like now (every table, view, function, and grant, as
`CREATE` statements), and the tool compares them with a real database and works out the `ALTER`, `CREATE`, and `DROP` statements that close the gap.
SQL Server Data Tools (SSDT) works this way for SQL Server. **pgproj is state-based, for PostgreSQL.**

```sql
-- sql/Schema/app/Table/customer.sql: the table as it is now, not the history of how it got there
CREATE TABLE app.customer (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      text NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now()
);
```

## What that changes in practice

| | Migration-based | pgproj |
|---|---|---|
| What is in source control | the changes, in order | the schema as it should be, one file per object |
| Seeing a table's current definition | read the latest snapshot, or the database | open its file |
| A code review of a schema change | a new migration file | a diff of the object's file, like any code change |
| Writing the change | you write it, or a tool generates it from a model | pgproj writes it from the files |
| A database changed by hand (drift) | the history table does not record it (some tools offer a separate diff command) | `pgproj diff` reports it; `--exit-code` fails CI on it |
| Two branches changing the schema at once | migrations can conflict in order or number | ordinary merge of the files |
| Views, functions, triggers, grants, policies | usually raw SQL inside a migration | first-class objects, compared and deployed like tables |
| Bringing a new database up | run every migration from the first | one `publish` creates everything |
| Moving data, backfilling a column | part of the migration, in order | a `predeploy` or `postdeploy` script you write |

## Where pgproj fits

- **The schema is the source of truth**, and PostgreSQL is more than tables to you: views, functions and procedures, triggers, row-level security,
  types, extensions, grants. pgproj models each of these and deploys them in dependency order, dropping and recreating what is in the way of a change.
- **You want to know what is really in a database.** `pgproj diff` compares any database with the project, so drift between environments is visible,
  and `pgproj init` turns an existing database into a project in one command.
- **You review scripts before they run.** `publish --dry-run` shows the exact SQL, `--execute` runs it in one transaction, and changes that lose data,
  need a superuser, or lock a large table are refused or named before anything runs.
- **Several databases share one schema**: many environments, or a schema per tenant ([tenants](notes/tenants.md)).

## Where a migration tool fits better

- **The application's model is the source of truth**, and the database is a detail of it. EF Core migrations keep the two in step without a second
  description of the schema.
- **Most changes move data**, not structure: splitting a column, rewriting values, multi-step refactorings that must run in a precise order. A
  migration expresses that directly; with pgproj it goes in a deploy script beside the structural change.
- **You need to support more than PostgreSQL.** pgproj is PostgreSQL only.

Two things a state-based tool cannot guess, pgproj asks you to state: a **renamed column** is written with a `-- @renamed-from: old_name` comment, or
it would be dropped and added ([Renaming columns](renaming-columns.md)), and a change that **drops data** runs only with `--allow-destructive`
([Destructive changes](notes/destructive-changes.md)).

## Using pgproj with EF Core

pgproj owns the schema; your application keeps using EF Core, Dapper, or Npgsql for data access. With EF Core, map your entities to the existing tables
(by hand, or with `dotnet ef dbcontext scaffold`) and do not add migrations, since pgproj deploys the schema. Run `pgproj publish` in your pipeline
before the application starts, as you would run migrations ([CI/CD](ci.md)).

Moving an existing database over is one command: `pgproj init --source-connection "..."` writes a project from it, and from then on the files are the
schema. If migrations created that database, their history table (`__EFMigrationsHistory`, `flyway_schema_history`) is scaffolded with the rest. Once you
stop running migrations, delete its file and run `publish` with `--allow-destructive` to drop the table, or keep the file and the table as they are.

---

[README](../README.md) · [A first project](../README.md#a-first-project-in-five-minutes) · [CI/CD](ci.md)
