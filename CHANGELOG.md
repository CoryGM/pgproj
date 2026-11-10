# Changelog

All notable changes to `pgproj` are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/). A version is the git tag `vX.Y.Z` that produced its release
(see `DEVELOPING.md`, "Releasing"); the section under a version heading is what that release's notes on GitHub say.

## [Unreleased]

### Documentation

- **The README and the destructive-changes note say plainly that pgproj comes with no warranty:** a publish can drop objects and their data,
  pgproj is provided "as is" under the Apache License 2.0 (sections 7 and 8), and they advise a `--dry-run` first, a copy to try changes on, and backups.

## [0.3.0] - 2026-11-10

### Installing

- **Linux builds work with OpenSSL 4**, so pgproj connects to databases on Fedora 45 and later, where 0.2.1 stopped with
  `No usable version of libssl was found`. They are built on a .NET runtime that loads OpenSSL 4, and the `.deb` also accepts `libssl4`.

### Commands

- **With `--source-connection`, the direction is the first line:** `diff` and `publish` printed the source's diagnostics (`No diagnostics.`) before the
  `Source A -> Target B` line; it now comes first, as the documents say.
- **`publish --apply-secrets` refuses a name no user mapping uses:** a misspelt name sent nothing and the run reported success, leaving the old secret in
  place. It now stops before anything runs (a plan warns), naming the variables the project's user mappings do use.
- **`publish --execute` says what uses an object it refuses to change:** when a change cannot be made while something uses the object (a base type a
  column uses, a foreign table a view reads moved to another server, a collation in use), the refusal now gives the reason and names what uses it, which
  only a `--dry-run`'s `-- TODO` comment did before.
- **An index whose expression compares a column with an untyped literal converges:** `CREATE INDEX ... ((status = 'open'))` on an enum column was
  reported changed after every `publish` and dropped and created again, since PostgreSQL stores the literal with its cast (`'open'::status`). An
  index's expression keys are now compared as PostgreSQL renders them, as a partial index's predicate already was; what is deployed is still what you wrote.
- **A project of more than about a thousand files no longer crashes `diff` and `publish` on Linux:** after reading the project, the command ended
  with a segmentation fault (exit code 139) and no message at its first connection to the database (seen with GitLab's schema, 2,753 files). The parser
  took one of the process's 1,024 thread-local keys for each file and never gave it back, leaving none for the TLS library; it now parses on threads it
  uses again. 0.2.1 has it too. Windows was not affected.
- **The script `publish` prints or writes is atomic:** it starts with `BEGIN;` and ends with `COMMIT;`, so `psql -f deploy.sql` applies all of it or,
  when a statement fails, none of it, as `--execute` does. The warning that it was not wrapped in a transaction is gone, and so is the advice to use
  `psql --single-transaction`, which now only adds warnings. A `SET lock_timeout` comes before the `BEGIN`. With `--concurrently`, its statements come
  after the `COMMIT`; with tenants, each part is a transaction of its own; for both, `publish` advises `psql -v ON_ERROR_STOP=1 -f`. `--execute` is
  unchanged.
- **Variables in the deploy scripts:** the project's pre- and post-deploy scripts, and a tenant's, can name a value with psql's syntax, `:'name'` or
  `:"name"`, instead of writing it, for a password or a value that differs between environments. `publish --execute` takes it from the environment
  variable `PGPROJ_VAR_<name>`, only in what it runs, and refuses to run when one is not set; the value is never printed and is masked in an error. A
  printed or written script keeps the variables, and `publish` names them, so it runs with `psql -v name=value -f`. `build` checks the scripts without
  the values. `pgproj about` lists the variables set, never their values. Before, a variable in the project's own scripts was an error, and a tenant's
  scripts could use only `tenant_schema` and `tenant_id` ([Variables in the deploy scripts](docs/notes/variables.md)).
- **An enum type that loses or reorders a label is rebuilt under the columns that use it**, instead of being refused: the old type is renamed out of
  the way, the new one created, each column moved to it (arrays too), and the old one dropped, with the columns' defaults, and the views, checks, and
  partial and expression indexes on them, and the owner the project states for the type, put back. A removed label needs `--allow-destructive`, and
  a row that still holds it stops the publish in words; a reorder does not need consent. A label marked `-- @renamed-label: old -> new` above the `CREATE TYPE` is renamed in place
  ([Enum types](docs/notes/enum-types.md)).
- **A composite type that retypes a field while table columns use it is rebuilt under them**, where `publish` used to write an `ALTER ATTRIBUTE ... TYPE`
  that PostgreSQL refused: the old type is renamed out of the way, the new one created, and each column moved to it (arrays too) with its value built
  field by field, by name, and read through text, so a value that no longer fits stops the publish in words instead of being cut; the columns' defaults,
  and the views, checks, and partial and expression indexes on them, are put back. A narrowed field needs `--allow-destructive`; a widened one does not.
  Adding and dropping fields are still done in place ([Composite types](docs/notes/composite-types.md)).
- **`build` reports transaction control in the deploy scripts:** a `COMMIT`, `ROLLBACK`, or other statement that ends the transaction, and a statement
  PostgreSQL refuses inside one (`VACUUM`, `CREATE INDEX CONCURRENTLY`, `CREATE DATABASE`, ...), are errors at their line, so `publish` does not run;
  a `BEGIN` is a warning. Before, a `COMMIT` in a pre-deploy script committed what ran before it, and `publish --execute` then failed with
  `Transaction is already completed`, leaving that part applied.
- **Privileges on types and domains are modeled:** `GRANT USAGE ON TYPE` or `ON DOMAIN` (enum, composite, and range types, and domains) is deployed,
  read back from the database, compared by `diff`, and written by `init` in the type's file, where before it was a warning and ignored. PostgreSQL gives
  `USAGE` on every new type to `PUBLIC`; a project takes that away with `REVOKE USAGE ON TYPE ... FROM PUBLIC` (the form `pg_dump` writes), the one
  `REVOKE` pgproj reads, and a type without it is compared as having the default. Taking it away from a type that exists needs `--allow-destructive`. A
  type `publish` drops and creates, or rebuilds under its columns, now gets its privileges back; before, it came back with the default, silently.
  **If a database revoked `PUBLIC`'s `USAGE` on a type and the project does not say so, `diff` now reports it, and `publish` gives it back**: add the
  `REVOKE` to the project (`init` writes it), or set `excludeGrants`. `publish` warns before anything runs when it gives `PUBLIC` a privilege on
  something the database already has ([Privileges on types and domains](docs/notes/type-privileges.md)).
- **A function or procedure `publish` drops and creates keeps `PUBLIC`'s `EXECUTE` revoked:** before, a routine recreated to change its return type or
  arguments came back executable by every role after its `EXECUTE` had been revoked from `PUBLIC` (a `SECURITY DEFINER` routine included), and `diff`
  reported no change. `REVOKE EXECUTE` or `ALL ON FUNCTION|PROCEDURE ... FROM PUBLIC` (the form `pg_dump` writes) is now read, deployed, compared, put back
  on a recreated routine, and written by `init` in the routine's file; taking it away from a routine that exists needs `--allow-destructive`.
  `ALTER DEFAULT PRIVILEGES [FOR ROLE r] REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC` (or `USAGE ON TYPES`), without `IN SCHEMA`, is read and deployed too, and
  a routine or type the project does not revoke on its own is then allowed with or without the default. **If a database revoked `PUBLIC`'s `EXECUTE` on a
  routine and the project does not say so, `diff` now reports it, and `publish` gives it back**: add the `REVOKE` to the project (the warning lists it,
  and `init` writes it), or set `excludeGrants`. The warning before anything runs now spells out each privilege given to `PUBLIC`, what every role can then
  do, which routines are `SECURITY DEFINER`, and the statement that keeps it revoked ([Privileges on functions and
  procedures](docs/notes/routine-privileges.md)).
- **An object created under default privileges converges:** PostgreSQL gives a new object what `ALTER DEFAULT PRIVILEGES` names, and pgproj used to
  report that as a grant to revoke, so the publish after the one that created a table in a database with such a rule planned a `REVOKE`. What one of the
  project's rules may give an object is now allowed and not required (a rule is not retroactive, so an older object without it is not changed either), and
  `publish` writes the rules before the objects they cover, so an object created in the same publish gets what they give
  ([Default privileges](docs/notes/default-privileges.md)).
- **A default-privileges rule on types or functions without `IN SCHEMA` converges:** PostgreSQL then stores the kind's whole default list, `PUBLIC`'s
  built-in `USAGE` on types or `EXECUTE` on functions included, and pgproj read that entry as a rule the project did not state, so `diff` reported it after
  every publish and `publish` planned `ALTER DEFAULT PRIVILEGES REVOKE ALL PRIVILEGES ON TYPES FROM PUBLIC`, a destructive change. It is now PostgreSQL's
  default and is not compared, and `init` no longer writes it ([Default privileges](docs/notes/default-privileges.md)).
- **`PUBLIC`'s privileges on the `public` schema are compared:** before, a database that had revoked `PUBLIC`'s `USAGE` on `public` showed no
  difference, and a project's `REVOKE ... ON SCHEMA public FROM PUBLIC` was a warning and ignored. `REVOKE USAGE`, `CREATE`, or `ALL ON SCHEMA public FROM
  PUBLIC` (the form `pg_dump` writes) is now read, deployed, compared, and written by `init` in `Schema/public/public.sql`; taking a privilege away needs
  `--allow-destructive`, on a new database too, since `public` always exists. A project that says nothing wants `USAGE` for `PUBLIC`, PostgreSQL's
  default. `CREATE`, which `PUBLIC` has there only on PostgreSQL 14, is compared only when the project revokes it, and never given back. **If a database
  revoked `PUBLIC`'s `USAGE` on `public` and the project does not say so, `diff` now reports it, and `publish` gives it back** after the warning: add the
  `REVOKE` to the project (`init` writes it), or set `excludeGrants` ([Privileges on the public schema](docs/notes/public-schema-privileges.md)).
- **Base types are modeled:** `CREATE TYPE name (INPUT = ..., OUTPUT = ...)`, written as `pg_dump` writes it (a shell, the input and output functions, the
  definition), is deployed, read back from the database, compared, and written by `init` in the type's file with its shell and functions. Before, `build`
  warned about it and left it and its functions out, and the type had to be created by hand. `publish` creates the shell, the functions, then the type as
  written, and runs them only with `--allow-superuser-statements`, since PostgreSQL lets only a superuser create a base type. `RECEIVE`, `SEND`,
  `TYPMOD_IN`, `TYPMOD_OUT`, `ANALYZE`, `SUBSCRIPT`, and `STORAGE` are changed in place with `ALTER TYPE ... SET`; any other change drops the type and its
  functions and creates them again, or is refused, naming what uses the type, when something does. A removed type is dropped with `CASCADE`, after a
  check that only its own functions depend on it. Every option is compared with PostgreSQL's default for one not stated, and `LIKE` with the values the
  database has ([Base types](docs/notes/base-types.md)).
- **Foreign data wrappers, servers, and user mappings are modeled:** `CREATE FOREIGN DATA WRAPPER`, `CREATE SERVER`, and `CREATE USER MAPPING`, with
  their grants, owners, and comments, are deployed, read back, compared, and written by `init`, where before `build` warned about them and left them out.
  A server's or mapping's option can name its value with a variable, `:'name'`, given in `PGPROJ_VAR_<name>`: such a value, and any `password`, is never
  compared, read back, or shown. A mapping's secret is sent when the mapping is created; the new `publish --apply-secrets [names]` sends the current values
  to mappings that exist. `init` writes each password as a variable and names the variables to set. A wrapper of the project's own needs
  `--allow-superuser-statements`; dropping a server or a mapping needs `--allow-destructive` ([Foreign data](docs/notes/foreign-data.md)).
- **Foreign tables are modeled:** `CREATE FOREIGN TABLE`, with its server and options, its columns' options, and its `CHECK` constraints, triggers,
  statistics, grants, owner, and comments, is deployed, read back, compared, and written by `init` to `Schema/<schema>/ForeignTable/`, where before
  `build` warned about it and left it out. A foreign table can be a partition. The forms `pg_dump` writes after it (`ALTER FOREIGN TABLE ... ALTER COLUMN
  ... OPTIONS`, `... OWNER TO`, `COMMENT ON FOREIGN TABLE`) are read too. Options change in place; a move to another server drops and creates the table,
  and is refused while a view or anything else uses it; dropping one needs `--allow-destructive`, dropping its column does not (its rows are the
  server's). `build` reports an index, or a constraint other than `CHECK` and `NOT NULL`, on a foreign table as an error, since PostgreSQL refuses
  them. `IMPORT FOREIGN SCHEMA` is still a warning and ignored ([Foreign tables](docs/notes/foreign-data.md#foreign-tables)).

### Library

- **`PublishResult.Script` and `PublishPart.Script` carry `BEGIN;` and `COMMIT;`**; `PublishPart.TransactionalScript` stays without them, for a program
  that runs it in a transaction of its own. `PublishNoticeKind.NotTransactional` is now raised only for a `--concurrently` script or one with tenants.
- **`PublishRequest.Variables`** gives the deploy scripts' variables their values for `PublishMode.Execute`, and **`PublishNoticeKind.Variables`** is the
  warning of a plan whose kept script uses variables, and the refusal of a run that lacks one.
- **`PublishNoticeKind.PublicGrant`** is the warning that the script gives `PUBLIC` a privilege on an object the database already has.
- **`PublishRequest.ApplySecrets`** sends the secrets of user mappings that exist again (`publish --apply-secrets`): null for none, empty for every variable
  their options name, or the variables to send.

## [0.2.1] - 2026-10-23

### Installing

- **The Windows builds are signed**: `pgproj.exe`, pgproj's DLLs, and the `.msi`, with a timestamped signature whose publisher is Cory Maass, so Windows
  shows who made them and that they have not been changed.

## [0.2.0] - 2026-10-23

### Commands

- **`pgproj about`** prints what a bug report needs, safe to paste into an issue: pgproj's version and how it was installed, the operating system and
  .NET runtime, whether PostgreSQL's parser loads, the project it finds, and the target and source databases with where each connection came from, the
  server's PostgreSQL version, and whether the role is a superuser and can create temporary objects. A project it cannot find and a database it cannot
  reach are reported, not errors; no password is printed.

### Library

- **`PgProjEngine.AboutAsync`** (`AboutRequest`, `AboutResult`, `AboutProject`, `AboutConnection`): the same description for a program, for a support or
  diagnostics page.

## [0.1.0] - 2026-10-23

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
  `.tar.gz`, `.zip`, `.deb`, `.rpm`, and an `.msi` installer, with `SHA256SUMS.txt` (signed), a software bill of materials, and build provenance
  (`gh attestation verify <file> --repo CoryGM/pgproj`).
- **A .NET tool** on nuget.org: `dotnet tool install --global pgproj`, with the same version as the other builds, for any of those platforms with the
  .NET 10 runtime installed.
- **Signed apt and dnf repositories** for Debian, Ubuntu, Fedora, and RHEL, on amd64 and arm64, at [corygm.github.io/pgproj](https://corygm.github.io/pgproj/):
  `apt install pgproj` and `dnf install pgproj`, with updates through the system's own. A `stable` channel for releases and a `testing` channel for
  release candidates. The `.rpm` packages are signed with the same key. The `.deb` and `.rpm` install
  what pgproj needs from the system, the Kerberos library included; on a Linux system without it, connections do not try GSSAPI, so nothing is printed
  about it.
- Licensed under the Apache License 2.0. pgproj is not accepting contributions yet; when it does, they will require a signed Contributor License Agreement
  ([`CLA.md`](CLA.md)).
