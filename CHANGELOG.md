# Changelog

All notable changes to `pgproj` are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/). A version is the git tag `vX.Y.Z` that produced its release
(see `DEVELOPING.md`, "Releasing"); the section under a version heading is what that release's notes on GitHub say.

## [Unreleased]

The first release of pgproj: a PostgreSQL database's schema kept as plain SQL files in source control, built, compared with a
live database, and deployed, the way SQL Server Data Tools does it for SQL Server.

### Commands

- **`pgproj build`** parses every SQL file of a project with PostgreSQL's own parser (libpg_query), checks the references
  between objects, and reports each problem as `file:line:column-endLine:endColumn`, the range an editor can underline: a missing table, type, or function, a duplicate, or a cycle too, at the name or statement it is about. A statement pgproj does not model is a warning naming it,
  never silently dropped. `--dump-model` writes the model it built. `--format json` writes the result as one JSON document for an editor or CI: the project, the
  documents read, the diagnostics with their ranges, and the counts; with `--source-connection`, the database built, and a document too when it is refused or
  cannot be reached.
- **`pgproj diff`** compares the project with a live database and lists what is added, removed, and changed, property by
  property. `--format json` writes the comparison as one JSON document for CI (the engine's result: `schemaVersion`, `status`, the project and the documents read, the diagnostics with their ranges, the changes, and the counts; when nothing could be compared, its status and what was read), and `--exit-code` exits with `2` when there are differences, for a
  drift check.
- **`pgproj publish`** writes the script that brings a database up to date, in dependency order, and with `--execute` runs it in
  one transaction. Objects in the way of a change (a view on a retyped column, say) are dropped and created again around it.
  `--dry-run` and `--output` show the script first.
- **`pgproj init`** scaffolds a new project from an existing database, one file per object, and says what it cannot read.
  `--schemas`, `--exclude-schemas`, and `--report` choose what is scaffolded and describe the run.
- **`pgproj document`** writes a data dictionary and an entity relationship diagram as HTML, Markdown, and JSON, from the
  project or from a database. The documentation lives in `COMMENT ON` text, with optional `@tag: value` lines
  ([`documentation.md`](documentation.md)).
- **`--erd`** on `init`, `build`, `diff`, and `publish` draws the project's tables as a Mermaid diagram.
- **`--verbose`** and **`--quiet`** on every command; passwords are masked in everything pgproj prints.

### Library

- **`PgProj.Engine`**, the pipeline as a .NET library, which every command runs through: `PgProjEngine.BuildAsync`, `CompareAsync`, `PublishAsync`,
  `ScaffoldAsync`, and `DocumentAsync` do what `build`, `diff`, `publish`, `init`, and `document` do and return results instead of printing them: a status for
  every problem a user can cause, diagnostics with their file and range, the change set, typed warnings and refusals with the objects they name, the script,
  and the files' text. The engine never prints, writes a file, or exits.
- **The project's files from anywhere:** from disk, from memory (a web service that receives a project), or from an editor's unsaved buffers laid over the
  disk, with each buffer's version reported back; `pgproj.json`'s settings can be passed as an object. Or from a live database: `BuildRequest.SourceConnectionString`
  builds a database in place of the project's files, as `build --source-connection` does (`BuildStatus.Refused` and `ConnectionFailed`, `BuildResult.Source`).
- **Passwords masked by the engine:** every message a result holds or an event carries (statuses, diagnostics, warnings and refusals, a failed statement's report,
  what `init` and `document` say) is masked before a program sees it, so a front end needs no masker of its own. The deployment SQL is never masked: a secret a
  project puts into its SQL on purpose is deployed, printed, and written as it is.
- **`PublishAsync`** plans the script, or plans and runs it (it plans again on its own connection, so what runs is what the database needs then), with the same
  consents `publish` asks for. `ScaffoldAsync` and `DocumentAsync` return the files and write none.
- **Building again after an edit:** a `ParseCache` kept by an editor and passed on each build (`BuildRequest.ParseCache`) parses only the files whose text
  changed since the last build.
- **Checking one document as it is typed:** `PgProjEngine.CheckDocumentAsync` gives one file's syntax diagnostics (and a deploy script's check) without a
  whole build, in milliseconds on a project of any size; the semantic checks still come from `BuildAsync`.
- **Progress and cancellation:** events as the run goes, in the words the command line's `--verbose` prints, and a cancellation token on every call.
- **One JSON document:** `BuildResult.ToJson` and `CompareResult.ToJson`, what `build --format json` and `diff --format json` print, with `schemaVersion` 1.
- **Pre-release:** the API may change before 1.0, and each change is listed here; from 1.0 it follows semantic versioning.
- **A NuGet package:** `PgProj.Engine` on nuget.org, with the same version as the command line (`dotnet add package PgProj.Engine`). It carries PostgreSQL's
  parser for win-x64, win-arm64, linux-x64, linux-arm64, and osx-arm64, and a build for another runtime fails with error `PGPROJ001`; symbols and Source Link
  are included. [Using pgproj as a library](docs/library.md) says how to use it, and [Adding a shell](docs/shells.md) how to put a new front end over it.

### Connections and environments

- A connection has a direction: the **source** is a database a command reads (`--source-connection`,
  `PGPROJ_SOURCE_CONNECTION`), the **target** the database `diff` and `publish` compare with and change
  (`--target-connection`, `PGPROJ_TARGET_CONNECTION`, or `targetConnectionString` in `pgproj.json`). `init` records where a
  project came from and never names a target.
- `diff` and `publish` with `--source-connection` use a live database as the desired state, to promote a change from one
  environment to another or check two for parity ([Promoting between environments](docs/notes/promoting-environments.md)).

### What is modeled

- Schemas, tables (columns, defaults, identity and generated columns, constraints, storage parameters, inheritance,
  partitioning, `LIKE`), indexes, views and materialized views, functions and procedures in any language, types (enum,
  composite, range, domain), sequences, triggers, row-level security policies, rules, extensions, event triggers, casts,
  operators, aggregates, collations, text search dictionaries and configurations, extended statistics, publications, grants,
  role memberships, default privileges, owners, and comments. The full list, and what is not modeled, is in
  [What is supported](docs/notes/supported-objects.md).
- A project converges: after a `publish`, `diff` finds nothing. Views, defaults, checks, policies, and other expressions are
  compared as PostgreSQL itself renders them, so a cosmetic difference is not a change.
- Columns are renamed in place with a `-- @renamed-from: old_name` comment ([Renaming columns](docs/renaming-columns.md)).
- A schema per tenant: a `tenants` block in `pgproj.json` keeps one template for every tenant's schema, which `diff` compares
  and `publish` deploys tenant by tenant ([Schema-per-tenant databases](docs/notes/tenants.md)).
- `predeploy` and `postdeploy` scripts, with `:r` includes and `-- pgproj: run when=changed` markers.

### Safety

- `publish --execute` refuses, before anything runs, a change that can lose data or access (a dropped table or column, a
  narrowing type change, a revoked grant, a changed owner) unless `--allow-destructive` is given
  ([Destructive changes](docs/notes/destructive-changes.md)).
- It also refuses a construct the server is too old for, a tablespace or role the server lacks, a statement only a superuser
  can run (unless `--allow-superuser-statements`), and a script that needs more locks than the server's lock table holds
  (unless `--allow-lock-limit`).
- Changes that rewrite or lock an existing table are named before anything runs; `--lock-timeout` bounds each lock wait, and
  `--concurrently` builds new indexes and validates new constraints without blocking writes
  ([Locks and large tables](docs/notes/locks.md)).
- A failed statement is named with the server's message, hint, and detail, and the transaction is rolled back.
- `publish --comments-only` deploys only the `COMMENT ON` statements, to publish documentation without touching the schema.

### Platforms and packaging

- PostgreSQL 14, 15, 16, 17, and 18 ([PostgreSQL versions](docs/notes/postgresql-versions.md)).
- Self-contained builds (no .NET installation needed) for Linux (x64, arm64), Windows (x64, arm64), and macOS (Apple silicon):
  `.tar.gz`, `.zip`, `.deb`, `.rpm`, and an `.msi` installer, with `SHA256SUMS.txt` and a software bill of materials. The `.deb` and `.rpm` install
  what pgproj needs from the system, the Kerberos library included; on a Linux system without it, connections do not try GSSAPI, so nothing is printed
  about it.
- Licensed under the Apache License 2.0; contributions require a signed Contributor License Agreement ([`CLA.md`](CLA.md)).
