# Why pgproj? Choosing a schema tool

Most .NET teams already manage a database schema somehow: EF Core migrations, Flyway, DbUp, Liquibase, Atlas, or folders of numbered scripts. This page
explains the two ways these tools work, compares pgproj with four of them side by side, and suggests which fits which situation. Each of the other tools
is mature, widely used, and the right choice for many teams; the aim here is to help you choose, not to argue that one is best.

The comparison was written by pgproj's maintainer, from each tool's own documentation as of October 2026 (linked at the end). Features and editions change,
so check a tool's current documentation before deciding.

## Two ways to change a schema

**Migration-based** tools keep a list of changes. Each change is a script, or a class, that moves the database from one version to the next, and the
tool records which ones a database has run. To add a column you write a new migration that adds it, by hand or by having a tool generate it.

- *EF Core migrations* generate a C# migration class from the difference between your model and a snapshot of the last one, and record the applied ones
  in `__EFMigrationsHistory`.
- *Flyway*, *DbUp*, and similar tools run versioned SQL scripts you write, once each, in order, and record them in a history table.
- *Liquibase* runs changesets from a changelog (SQL, XML, YAML, or JSON) and records them too.

**State-based** (or declarative) tools keep the schema itself. The files say what the database should look like now (every table, view, function, and
grant, as `CREATE` statements), and the tool compares them with a real database and works out the `ALTER`, `CREATE`, and `DROP` statements that close
the gap. SQL Server Data Tools (SSDT) works this way for SQL Server; *Atlas*, *pgCodeKeeper*, and **pgproj** work this way for PostgreSQL. Some tools
offer both: Atlas can also generate versioned migrations from a desired schema, and Flyway's paid editions can generate migrations from a schema model.

```sql
-- sql/Schema/app/Table/customer.sql: the table as it is now, not the history of how it got there
CREATE TABLE app.customer (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      text NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now()
);
```

Neither approach is better in general. A migration records exactly what ran and in what order, and expresses data changes naturally; a state-based
project shows what the schema is now and lets a tool work out the steps.

## What that changes in practice

| | Migration-based | State-based (pgproj) |
|---|---|---|
| What is in source control | the changes, in order | the schema as it should be, one file per object |
| Seeing a table's current definition | read the latest snapshot, or the database | open its file |
| A code review of a schema change | a new migration file | a diff of the object's file, like any code change |
| Writing the change | you write it, or a tool generates it from a model | the tool writes it from the files |
| A database changed by hand (drift) | the history table does not record it (some tools offer a separate check) | `pgproj diff` reports it; `--exit-code` fails CI on it |
| Two branches changing the schema at once | migrations can conflict in order or number | ordinary merge of the files |
| Views, functions, triggers, grants, policies | usually raw SQL inside a migration | first-class objects, compared and deployed like tables |
| Bringing a new database up | run every migration from the first | one `publish` creates everything |
| Moving data, backfilling a column | part of the migration, in order, exactly where it is needed | a `predeploy` or `postdeploy` script you write |
| Knowing exactly what ran on a database | the history table lists every migration | the script `publish` printed or wrote for that run |

## The tools side by side

| | EF Core migrations | Flyway | Atlas | pgCodeKeeper | pgproj |
|---|---|---|---|---|---|
| **Approach** | Migration-based, generated from your model | Migration-based, scripts you write | Declarative, or versioned migrations it plans | State-based compare | State-based |
| **What you write** | C# entities and configuration | Versioned (and repeatable) SQL scripts | The desired schema in SQL, its own HCL, or an ORM's model | A project of SQL files, one per object | A project of SQL files, one per object |
| **How the current state is known** | A model snapshot from the last migration, and the history table | The schema history table | Reads the live database | Reads the live database, a dump, or another project | Reads the live database's catalog |
| **How a change is produced** | Model compared with the snapshot when you add a migration | You write it (paid editions can generate it from a schema model) | Desired schema compared with the database, then a plan | Two sources compared; you pick the differences to apply | Project compared with the database at each `diff` and `publish` |
| **How SQL is understood** | EF Core generates the SQL through its provider | Your scripts run as written | Its own parsers, plus a dev database (a temporary PostgreSQL) that loads and normalizes the desired schema | Its own parser (ANTLR) | PostgreSQL's own parser (libpg_query), with the target server rendering expressions for comparison |
| **What is deployed** | SQL generated from the migration's operations | Your scripts, as written | The planned statements | A script generated for the selected differences, which you can edit | Your statements for new objects, and generated `ALTER`s for changes |
| **Drift (a hand-made change)** | Not checked against the database | Paid editions report drift | Paid plans detect and monitor drift | Shown when you compare | Shown by `diff` |
| **Safety checks before deploying** | Warns of possible data loss when generating; review the migration | Each migration in a transaction; paid editions add policy checks | Paid plans lint for destructive and locking changes | You review the script before running it | Refuses data loss without consent; names rewrites and locks; checks before running |
| **Databases** | Many, through providers | Many | Many | PostgreSQL, SQL Server, ClickHouse, Greenplum | PostgreSQL only |
| **Form** | `dotnet ef` tools, bundles, runtime API | CLI, Java API, Maven and Gradle plugins; desktop app in paid editions | CLI, CI integrations, Kubernetes operator, cloud service | Eclipse plug-in and a CLI (Java) | CLI (`dotnet tool`, packages) and a .NET library |
| **Licence and cost** | Free, open source | Community: free (open source, Apache 2.0); Enterprise: paid | Starter free; Pro and Enterprise paid; an Apache 2.0 Community Edition | Free, open source (Apache 2.0) | Free, open source (Apache 2.0) |

Notes on the table:

- **EF Core**: when a property is renamed, EF Core generates a drop and an add of the column, and the documentation says to replace them with
  `RenameColumn` by hand. `dotnet ef migrations has-pending-model-changes` checks the model against the migrations, not against a database.
- **Flyway**: the open-source project and the free Community edition run the migrations you write. Generating migrations from a schema model (compared
  through a shadow database), drift reports, and generated undo scripts come with the paid editions.
- **Atlas**: on PostgreSQL, the free Starter plan and the Community Edition cover tables, columns, indexes, constraints, foreign keys, enums, and comments.
  Views, materialized views, functions, procedures, triggers, sequences, extensions, row-level security, partitions, domains, and composite types are in
  the paid Pro plan, as are migration linting and drift detection. A rename is confirmed when Atlas asks, or stated in the schema with a
  `renamed_from` directive.
- **pgCodeKeeper**: compares any two of a project, a `pg_dump` file, and a live database, in either direction, and generates a dependency-aware script for
  the differences you select, which you review and run from the plug-in or with the CLI.

## Which fits which situation

| Your situation | A good fit |
|---|---|
| Your application's EF Core model is the source of truth, and the database exists to serve it | **EF Core migrations**: one description of the schema, in C#, and migrations generated from it |
| You want every change as hand-written SQL, run in a fixed order, with data changes next to the DDL and a full record of what ran | **Flyway** (Community), or DbUp for a .NET-native runner |
| As above, plus migrations generated from a schema model, drift reports, a desktop app, and vendor support | **Flyway Enterprise** |
| You manage schemas across several database engines, or want a Terraform-style declarative workflow with CI checks and a Kubernetes operator | **Atlas** (the Pro plan for PostgreSQL views, functions, and triggers, linting, and drift detection) |
| You want to compare PostgreSQL databases, dumps, and projects in a desktop tool and pick which differences to apply, or you also work with SQL Server, ClickHouse, or Greenplum | **pgCodeKeeper** |
| A PostgreSQL schema kept as SQL files and reviewed like code, checked without a database, deployed from .NET pipelines, with a schema per tenant, or embedded in your own .NET tool | **pgproj** |

**pgproj is a good fit when:**

- **The schema is the source of truth**, and PostgreSQL is more than tables to you: views, functions and procedures, triggers, row-level security,
  types, extensions, grants. pgproj models each of these and deploys them in dependency order, dropping and recreating what is in the way of a change.
- **You want problems found before a database is touched.** `pgproj build` parses every file with PostgreSQL's own parser and checks the references
  between objects, offline, reporting each problem at its file, line, and column.
- **You review scripts before they run.** `publish --dry-run` shows the exact SQL, `--execute` runs it in one transaction, and changes that lose data,
  need a superuser, or lock a large table are refused or named before anything runs.
- **Several databases share one schema**: many environments, or a schema per tenant ([tenants](notes/tenants.md)).
- **You work in .NET**: it installs as a `dotnet tool`, and the same engine is a NuGet package for your own tools ([library](library.md)).

**Another tool fits better when:**

- **The application's model is the source of truth.** EF Core migrations keep the two in step without a second description of the schema.
- **Most changes move data**, not structure: splitting a column, rewriting values, multi-step refactorings that must run in a precise order. A migration
  expresses that directly; with pgproj it goes in a deploy script beside the structural change.
- **You need more than PostgreSQL**, or a GUI, an approval workflow, continuous drift monitoring, or a support contract. pgproj is a PostgreSQL command
  line and library, before 1.0, maintained by one person.
- **You need what pgproj does not model yet**: foreign data wrappers and foreign tables, subscriptions, and base types are reported, not deployed (the
  [open issues](https://github.com/CoryGM/pgproj/issues) list them).

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

## Sources

- EF Core: [Migrations overview](https://learn.microsoft.com/ef/core/managing-schemas/migrations/), [Managing migrations](https://learn.microsoft.com/ef/core/managing-schemas/migrations/managing),
  [Applying migrations](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying)
- Flyway: [Migrations](https://documentation.red-gate.com/fd/migrations-271585107.html), [Editions](https://www.red-gate.com/products/flyway/editions),
  [Generating migrations](https://documentation.red-gate.com/fd/generating-migrations-138347054.html), [the open-source repository](https://github.com/flyway/flyway)
- Atlas: [Documentation](https://atlasgo.io/docs), [Dev database](https://atlasgo.io/concepts/dev-database), [Features](https://atlasgo.io/features),
  [Community Edition](https://atlasgo.io/community-edition), [Pricing](https://atlasgo.io/pricing), [Declarative renames](https://atlasgo.io/changelog/declarative-schema-renames)
- pgCodeKeeper: [User guide](https://pgcodekeeper.readthedocs.io/), [CLI](https://pgcodekeeper.readthedocs.io/en/latest/cli_version.html),
  [the repository](https://github.com/pgcodekeeper/pgcodekeeper)

---

[README](../README.md) · [A first project](../README.md#a-first-project-in-five-minutes) · [CI/CD](ci.md)
