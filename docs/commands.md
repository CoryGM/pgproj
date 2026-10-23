# Command reference

Every `pgproj` command and option, in full. For what pgproj is, how to install it, and a first project, start with the [README](../README.md);
`pgproj --help` and `pgproj <command> --help` print the same options at the terminal.

- [The commands](#commands): [`init`](#init), [`build`](#build), [`diff`](#diff), [`publish`](#publish), [`document`](#document)
- [Promoting between environments](#promoting-between-environments): the rules of `--source-connection` on `build`, `diff`, and `publish`
- [Options every command takes](#options-every-command-takes): `--help`, `--verbose` and `--quiet`, and the diagram (`--erd`)

The project file is [`pgproj.json.md`](../pgproj.json.md), connections are [Connections](../README.md#connections), and how pgproj treats each part of a
schema is in the [notes](notes/README.md).

## Commands

| Command | What it does |
|---|---|
| [`init`](#init) | Scaffolds a new project from a live database. |
| [`build`](#build) | Builds the project and reports problems. |
| [`diff`](#diff) | Compares the project with a live database. |
| [`publish`](#publish) | Writes, and optionally runs, the script that updates a database. |
| [`document`](#document) | Writes a data dictionary and diagram of the project or a database. |

Every command also takes `--verbose` or `--quiet` and `--help` (see [Options every command takes](#options-every-command-takes)).
`init`, `build`, `diff`, and `publish` take `--erd` to draw the project's tables ([Entity relationship diagram](#entity-relationship-diagram---erd)).

### init

Scaffolds a new project from an existing database: a `pgproj.json` and one SQL file per object.

Files go in Title Case folders under the output directory:

- `Schema/<schema>/<schema>.sql` for the schema itself, with its grants and default privileges.
- `Schema/<schema>/Table/<table>.sql` for a table, with its constraints, indexes, triggers, row-level security policies, rules, extended
  statistics, and grants.
- `Schema/<schema>/View/`, `Function/`, `Procedure/`, `Sequence/`, `Type/`, `Collation/`, `Aggregate/`, `Operator/`,
  `TextSearchDictionary/`, and `TextSearchConfiguration/` for the other kinds in a schema. A materialized view's indexes, and a view's
  rules, are in the view's file.
- `Extension/`, `EventTrigger/`, `Cast/`, and `Publication/` at the top, for what belongs to no schema. An extension that requires others is
  written after them, in the file of the one that sorts last ([extensions](notes/extensions.md)).

Each object's owner is written beside it as `ALTER ... OWNER TO` (a sequence owned by a column has none; it follows its table). The
folder names are only a convention: `build`, `diff`, and `publish` read every `*.sql` file under `source`, whatever its folder is called.

Two objects whose names differ only by case, such as tables `"T"` and `t`, get files of those names (`T.sql`, `t.sql`) when the output folder's
file system tells them apart, and `t~2.sql` for the second when it doesn't. pgproj asks the file system by writing a temporary file and checking
whether the same name in the other case finds it. If it can't write there, it assumes Windows and macOS are case-insensitive and other systems
case-sensitive.

Options:

- `--source-connection <connection-string>` - the database to read (the source). Without it, the `PGPROJ_SOURCE_CONNECTION` environment variable is used; one of the two is required.
- `--project <path>` - where to write `pgproj.json` (default `pgproj.json`).
- `--output <directory>`, `-o <directory>` - where to write the SQL files, relative to `--project`'s folder unless absolute (default
  `./sql`). It is created if it does not exist.
- `--name <name>` - the project's `name` (default: the database name from `--source-connection`).
- `--force` - overwrite an existing `pgproj.json`, or write into an output directory that is not empty. Without it, `init` refuses to.
- `--schemas <list>` - scaffold only these schemas, as a comma-separated list (`--schemas app,reporting`) or the option repeated.
  `public` is included only if it is named. A schema the database does not have is an error.
- `--exclude-schemas <list>` - scaffold every schema except these. It cannot be combined with `--schemas`. `pg_catalog` and
  `information_schema` are never scaffolded.
- `--exclude-grants`, `--exclude-role-memberships`, `--exclude-owners` - leave that category out, and set `excludeGrants`,
  `excludeRoleMemberships`, or `excludeOwners` to `true` in the new `pgproj.json`.
- `--report` - also write a Markdown report of the run (see [The report](#the-report)).
- `--tenant-prefix <prefix>` - for a database with a schema per tenant (`tenant_acme`, `tenant_globex`, ...): scaffold the shared schemas as usual,
  one tenant (the first by name, or `--tenant-from <identifier>`) into `Tenant/` as the template with its schema name taken out, name the tenants that
  differ from it, and write the `tenants` block into `pgproj.json`. `--tenant-file` also writes `tenants.csv` listing the tenants found; `--tenant-query
  <sql>` writes that query instead. See [Schema-per-tenant databases](notes/tenants.md#starting-from-a-database-init).
- `--erd [path]` and the other ERD options - also draw the tables (see [Entity relationship diagram](#entity-relationship-diagram---erd)).

Examples:

```sh
pgproj init --source-connection "Host=localhost;Database=mydb;Username=postgres;Password=postgres"
pgproj init --source-connection "Host=localhost;Database=mydb" -o ./schema --project ./db/pgproj.json
pgproj init --source-connection "Host=localhost;Database=mydb" --force
pgproj init --source-connection "Host=localhost;Database=mydb" --exclude-grants --exclude-role-memberships
pgproj init --source-connection "Host=localhost;Database=mydb" --schemas app,reporting --report --erd
pgproj init --source-connection "Host=localhost;Database=mydb" --exclude-schemas audit,staging
pgproj init --source-connection "Host=localhost;Database=mydb" --tenant-prefix tenant --tenant-file
```

**The connection string** in the new `pgproj.json` is `sourceConnectionString`: `--source-connection` as you gave it, with the password replaced by `<password>`. It records where
the project came from and is never the database `diff` and `publish` change; name that with `--target-connection` ([Connections](../README.md#connections)).

**Choosing schemas.** With `--schemas` or `--exclude-schemas`, the new `pgproj.json` lists the schemas that were scaffolded in `schemas`,
so later commands read only those; without either, `schemas` is left out. Objects in a schema that was left out are not in the
project, and references to them (a foreign key, a column type, a view's source) are kept as they are: `pgproj build` reports each one
as a warning, and the report lists them.

**What `init` prints:**

```
Wrote 7 SQL file(s) to: C:\work\shop\sql
Wrote project file: C:\work\shop\pgproj.json
Wrote report: C:\work\shop\sql\init-report-2026-10-02-14-03-11.md
Not read by pgproj, so absent from the scaffolded project even if the database has them:
  - roles and their settings (role memberships are scaffolded)
  - text search parsers and templates (a text search configuration and a dictionary are scaffolded)
  - tablespaces (a table's TABLESPACE clause is scaffolded)
  - foreign data wrappers, servers, user mappings, and foreign tables
  - subscriptions
  - operator classes and families
  - conversions
  - base types (CREATE TYPE with INPUT and OUTPUT functions), with their input and output functions
  - languages
  - transforms
  - access methods
  - security labels
  - database-level settings
  - comments on event triggers, rules, operators, aggregates, casts, publications, and a domain's constraints (the objects themselves are scaffolded)
  - indexes PostgreSQL marks INVALID, left by a failed CREATE INDEX CONCURRENTLY (each is named)
Check for them before relying on the scaffold as complete.
Run 'pgproj build' to validate the scaffolded project.
```

The list of kinds pgproj does not read is also in `pgproj init --help`; `--quiet` leaves it out of the output. Everything else a database
can hold is scaffolded, including event triggers, casts, operators, aggregates, collations, text search dictionaries and
configurations, publications, rules, and extended statistics. See [What is supported](notes/supported-objects.md). A base type is left out with its input and output functions, and `init` names it ([base types](notes/base-types.md)). An index PostgreSQL marks `INVALID` (left by a failed `CREATE INDEX CONCURRENTLY`) is left out, as `pg_dump` leaves it out, and `init` names it.

#### The report

`--report` writes `init-report-<yyyy-MM-dd-HH-mm-ss>.md` (the time `init` started, in UTC) at the root of the output directory. It is
Markdown, not SQL, so `build` ignores it. It starts with a contents list and has these sections, leaving out any that would be empty:

- **Arguments** - the options used, with the password masked.
- **Summary** - when the run started and ended and how long it took; the server, its PostgreSQL version, and the database; the schemas
  scaffolded; a link to the diagram if `--erd` was used, saying what was done with the file and why; and, per schema, the number of
  objects of each type.
- **Modeled objects** - a table per schema, by type then name, with the file each object is in. Indexes, constraints, triggers,
  policies, rules, and extended statistics are listed too; grants, owners, and comments are not (they are in their object's file).
- **Not modeled: not supported** - the kinds pgproj cannot read, and the objects of those kinds it found: in the scaffolded schemas (a
  foreign table, a text search parser or template, an operator class or family, a conversion, a base or shell type) and in the database (a foreign data
  wrapper, server, or user mapping, a language, a transform, an access method).
- **Not modeled: excluded** - the schemas left out and the files that refer to them, and how many grants, memberships, or owners each
  `--exclude-*` option left out. References are found by looking for `schema.` in the SQL, and PostgreSQL writes names in `public`
  without the schema, so a reference into an excluded `public` is not found.
- **Not modeled: cluster-wide** - the roles that own, or hold a privilege or membership on, a scaffolded object; the tablespaces its
  tables use; and the database's subscriptions. These belong to the whole server, so pgproj reports them and does not model them.

### build

Builds the project and prints what it finds wrong. Each problem starts with where it is, the way a compiler reports it, so editors and
terminals can link to it:

```
sql/Schema/s/Table/b.sql:3:30-3:31: [Error] syntax error at or near ","
```

That is `file:line:column-endLine:endColumn`, 1-based, counted in characters. The end is the position just after the last character, as an editor's
range has it, so the comma above (one character at column 30) ends at column 31, and an error at the end of the text, with nothing to underline, starts
and ends in the same place. A syntax error covers the token the parser stopped at; a warning about a statement pgproj does not model covers the
statement (without its closing `;`); a warning about an expression or type whose text could not be read as written runs from where it starts to the end
of its statement; a warning about a `-- @renamed-from` comment covers the comment. A problem found once the project is read (a missing table, type, or
function, a duplicate, a name off the search path, a dependency cycle) covers what it is about: the name it cannot find where that name is written (the
table after `REFERENCES`, a column's type), else the column or the statement that names it (without its `;`); a cycle is shown at its first object. Only a
problem about the project as a whole (how many objects state no owner) has no position. A place that is not a range, because the end is not known, is
`file:line:column`, `file:line`, or `file`.

The path is relative to the current directory when the file is under it. `diff` and `publish` report problems the same way.

Options:

- `--project <path>` - the project file (default `pgproj.json`).
- `--dump-model <file>` - write the model pgproj built as indented JSON, to see how it read the project.
- `--format text|json` - `text` (the default) is the header and the problems above. `json` writes the result as one JSON document on standard output, for an
  editor or CI: the start of the document `diff --format json` writes (`schemaVersion`, `status`, which is `succeeded`, `failed`, `projectNotFound`, or
  `projectInvalid`, and with `--source-connection` also `refused` or `connectionFailed`, `project`, `documents` with their versions, `diagnostics`, each with its
  `file`, `line`, `column`, `endLine`, and `endColumn` when known, `counts` by severity, and with `--source-connection` the `source` database). Everything else, the
  header and the problems' text included, goes to standard error. The exit code is the same.
- `--exclude-grants`, `--exclude-role-memberships`, `--exclude-owners` - override `excludeGrants`, `excludeRoleMemberships`, or
  `excludeOwners` for this run, either way (`--exclude-grants false` brings grants back).
- `--source-connection [<connection-string>]` - build a live database in place of the project's SQL files: it is scaffolded as `init` does, in a read-only transaction, so
  `--dump-model` and `--erd` describe the database and the diagnostics say whether what `init` would write builds. With no value it reads `PGPROJ_SOURCE_CONNECTION`; only the
  option on the command line turns the mode on. `pgproj.json` is optional and still supplies `schemas`, `searchPath`, and the `exclude*` settings; not for a project with tenants.
  `build` never connects to a target.
- `--erd [path]` and the other ERD options - also draw the tables.

Examples:

```sh
pgproj build
pgproj build --project ./pgproj.json
pgproj build --dump-model ./model.json
pgproj build --format json
pgproj build --quiet
pgproj build --verbose
pgproj build --erd
pgproj build --erd docs/model
pgproj build --source-connection "Host=localhost;Database=mydb" --dump-model ./model.json
pgproj build --erd policies --erd-schemas policies
```

### diff

Compares the project with a live database. It lists each added (`+`), removed (`-`), and changed (`~`) object, each changed property
as `old -> new`, and ends with the counts (`Added: n`, `Removed: n`, `Modified: n`). A property is named as you would write it (`Default`,
`Query`, `Owner`, `Columns changed`).

Options:

- `--project <path>` - the project file (default `pgproj.json`).
- `--target-connection <connection-string>` - the database to compare with (the target). Without it, the `PGPROJ_TARGET_CONNECTION` environment variable is
  used, then `targetConnectionString` from `pgproj.json`; one of the three is required.
- `--source-connection [<connection-string>]` - use a live database as the desired state in place of the project's SQL files, to compare two databases
  ([Promoting between environments](#promoting-between-environments)). With no value it reads `PGPROJ_SOURCE_CONNECTION`; only the option on the command line turns
  the mode on.
- `--format text|json` - `text` (the default) is the list above. `json` writes the comparison as one JSON document on standard output, for
  an editor or CI, the same document the engine returns (it starts as `build --format json`'s does: `schemaVersion`, `status`, `project`, `documents` with their versions, `diagnostics` (each with its `file`,
  `line`, `column`, `endLine`, and `endColumn` when known; the end is just past the last character) and `counts`, then `added`, `removed`, and `modified` (each change with its
  `property`, named as in the text, `old`, and `new`), `bodyNotes`, `undroppedSchemas`, and `summary` counts; `source` and `target` with `--source-connection`, and the tenant blocks for a project with
  tenants). Everything else goes to standard error. When nothing could be compared (the project has errors, the run is refused, a database cannot be
  reached), the document has that `status` (`failed`, `refused`, `projectNotFound`, `connectionFailed`, ...), a `message`, and what was read, without the
  comparison's part, and the exit code is `1`.
- `--exit-code` - exit with `2` when there are differences (an added, removed, or modified object) and `0` when there are none, for a CI
  drift check; an error is still `1`, and warnings are not differences. Without it, `diff` exits `0` whether or not it finds differences.
- `--exclude-grants`, `--exclude-role-memberships`, `--exclude-owners` - override the `pgproj.json` setting for this run; it applies to
  both the project and the database.
- `--tenants <identifiers>` - for a project with tenants, compare only these tenants (`--tenants acme,globex`; identifiers, not schema names). Such a
  project is compared in parts: the shared schemas once, then each tenant's schema against the template, the tenants grouped by the changes they need
  ([Schema-per-tenant databases](notes/tenants.md#comparing-diff)).
- `--erd [path]` and the other ERD options - also draw the tables.

Examples:

```sh
pgproj diff --project ./pgproj.json
pgproj diff --target-connection "Host=localhost;Database=mydb;Username=postgres;Password=postgres"
pgproj diff --target-connection "Host=localhost;Database=mydb" --exclude-grants
pgproj diff --target-connection "Host=localhost;Database=mydb" --format json
pgproj diff --target-connection "Host=localhost;Database=mydb" --exit-code
pgproj diff --target-connection "Host=localhost;Database=mydb" --verbose
pgproj diff --target-connection "Host=localhost;Database=mydb" --erd
pgproj diff --target-connection "Host=localhost;Database=mydb" --tenants acme,globex
```

### publish

Writes the script that brings a database up to date with the project, and runs it if you ask. The statements are in dependency order,
and an object in the way of a change (a view on a column whose type changes, say) is dropped and created again around it (see
[Dependent objects](notes/dependent-objects.md)).

- **Review first:** `--dry-run` prints the script, and `--output <file>` writes it to a file. Without `--execute`, the script is only
  printed.
- **Run it:** `--execute` runs the script in a single transaction, so a failure partway leaves nothing applied. The error gives the server's message, its
  hint and detail, and the statement that failed.
- **Changes that lose data are refused** unless you add `--allow-destructive`. See [Destructive changes](notes/destructive-changes.md).
- **Changes that rewrite or lock a table that already exists are named** before anything runs (a column type change, `SET NOT NULL`, a new
  index or constraint, ...), and `--lock-timeout 5s` makes a publish give up and roll back rather than wait behind another session's long
  transaction. See [Locks and large tables](notes/locks.md).
- **Indexes and constraints can be built without blocking writes:** with `--concurrently`, a new index on a table that already exists is
  built with `CREATE INDEX CONCURRENTLY` after the transaction commits, and a new foreign key or `CHECK` is added `NOT VALID` and validated
  after it. See [Locks and large tables](notes/locks.md#--concurrently).
- **A script that needs more locks than the server's lock table holds is refused** unless you add `--allow-lock-limit`: the transaction keeps every
  relation it changes locked until it commits, and PostgreSQL would fail partway with `out of shared memory`. See [Locks and large tables](notes/locks.md).
- **Statements only a superuser can run are refused** unless you add `--allow-superuser-statements` and connect as a superuser. See
  [Statements only a superuser can run](notes/superuser-statements.md).
- **A printed or written script is not wrapped in a transaction**, and `publish` warns so. Run it inside one yourself
  (`psql --single-transaction -f deploy.sql`), or use `--execute`.
- **A project with tenants is published in parts:** the shared changes in one transaction, then each tenant in a transaction of its own, a new tenant's
  schema created first; every part is checked before anything runs, and a failure stops the run and says what was published (see
  [Schema-per-tenant databases](notes/tenants.md#deploying-publish)).
- **A drop of something a PL/pgSQL body names is noted.** PostgreSQL records no dependency for such a body, so it does not stop a table, view, function, or qualified column that a
  routine's body uses from being dropped, renamed, or retyped, and the routine fails when it runs. `diff` and `publish` say which routine names what ([PL/pgSQL bodies](notes/plpgsql-bodies.md)).
- **Schemas are never dropped.** A schema the project no longer uses is left in place (its objects are dropped as usual), and `diff` and
  `publish` warn and name it. Drop it yourself if you no longer want it (see [Schemas](notes/schemas.md)).

Options:

- `--project <path>` - the project file (default `pgproj.json`).
- `--target-connection <connection-string>` - the database to update (the target). Without it, the `PGPROJ_TARGET_CONNECTION` environment variable is
  used, then `targetConnectionString` from `pgproj.json`; one of the three is required.
- `--execute` - run the script, in a single transaction.
- `--source-connection [<connection-string>]` - use a live database as the desired state in place of the project's SQL files: the target is changed to match it
  ([Promoting between environments](#promoting-between-environments)). With no value it reads `PGPROJ_SOURCE_CONNECTION`; only the option on the command line turns
  the mode on.
- `--dry-run` - print the script without running it, even with `--execute`.
- `--output <file>` - write the script to a file.
- `--allow-destructive` - let `--execute` run a script that can discard data or access (a dropped table, column, materialized view,
  extension, or composite type field; a changed column type that can lose a value, so not a widening such as `varchar(50)` to
  `varchar(100)` or `int` to `bigint`; a cast, operator, aggregate, rule, publication, or event trigger that is dropped and not created
  again; a revoked grant or role membership; or a changed owner). Without it, `--execute` refuses such a script and lists the operations.
- `--allow-superuser-statements` - let `--execute` run statements only a superuser can run: an event trigger created, changed, or dropped, a table's foreign-key triggers enabled or disabled (`ALTER TABLE ... TRIGGER ALL`),
  and a function or procedure created or replaced `LEAKPROOF` or in an untrusted language (`c`, `internal`, `plpython3u`, ...). Without it,
  `--execute` refuses such a script, even for a superuser; with it, a role that is not a superuser is refused before anything runs (see
  [Statements only a superuser can run](notes/superuser-statements.md)).
- `--allow-lock-limit` - let `--execute` run a script estimated to need more locks than the server's lock table holds
  (`max_locks_per_transaction * (max_connections + max_prepared_transactions)`). Without it, `--execute` refuses such a script; from 80% of the table,
  `publish` warns.
- `--lock-timeout <duration>` - wait at most this long for each lock (`5s`, `500ms`, `2min`): past it, the script is rolled back and the
  statement that was waiting is named. Off unless given; a printed or written script starts with `SET lock_timeout`.
- `--concurrently` - after the transaction commits, build each new index on a table that already exists with `CREATE INDEX CONCURRENTLY`
  (a partitioned table's on each partition, then attached), swap in a changed index built under a temporary name, build a new unique key
  (or a primary key on columns already `NOT NULL`) as an index and add it `USING INDEX`, and validate each new foreign key and `CHECK`,
  added `NOT VALID` in the transaction. One statement at a time; a failed one has what it built dropped, and running it again does the
  rest. A printed or written script has these after a marked line (see [Locks and large tables](notes/locks.md#--concurrently)).
- `--tenants <identifiers>` - for a project with tenants, publish only these tenants (`--tenants acme,globex`; identifiers, not schema names); the
  shared changes are published either way.
- `--comments-only` - deploy only the script's `COMMENT ON` statements: no schema changes, grants, or owners, and not the `predeploy` or
  `postdeploy` script. A comment on an object the database does not have yet, such as a new table or column, is skipped with a warning
  that names it (see [Publishing only the comments](notes/comments-only.md)).
- `--exclude-grants`, `--exclude-role-memberships`, `--exclude-owners` - override the `pgproj.json` setting for this run; it applies to
  both the project and the database.
- `--erd [path]` and the other ERD options - also draw the tables.

Examples:

```sh
pgproj publish --project ./pgproj.json --dry-run
pgproj publish --output ./deploy.sql
pgproj publish --dry-run --quiet > deploy.sql
pgproj publish --dry-run --verbose
pgproj publish --dry-run --erd docs/model
pgproj publish --execute --target-connection "Host=localhost;Database=mydb;Username=postgres;Password=postgres"
pgproj publish --execute --allow-destructive --target-connection "Host=localhost;Database=mydb;Username=postgres;Password=postgres"
pgproj publish --execute --lock-timeout 5s --target-connection "Host=localhost;Database=mydb;Username=postgres;Password=postgres"
pgproj publish --execute --concurrently --lock-timeout 5s --target-connection "Host=localhost;Database=mydb;Username=postgres;Password=postgres"
pgproj publish --execute --allow-lock-limit --target-connection "Host=localhost;Database=mydb;Username=postgres;Password=postgres"
pgproj publish --execute --allow-superuser-statements --target-connection "Host=localhost;Database=mydb;Username=postgres;Password=postgres"
pgproj publish --comments-only --execute --target-connection "Host=localhost;Database=mydb;Username=postgres;Password=postgres"
pgproj publish --comments-only --output ./comments.sql
pgproj publish --execute --tenants acme,globex --target-connection "Host=localhost;Database=mydb;Username=postgres;Password=postgres"
pgproj publish --execute --exclude-role-memberships --target-connection "Host=localhost;Database=mydb;Username=postgres;Password=postgres"
```

### document

Writes the database's documentation: a data dictionary and an entity relationship diagram, as HTML, Markdown, and JSON. The dictionary
covers schemas; tables with their columns, keys, constraints, triggers, row-level security policies, and indexes; views; functions and
procedures; types; and sequences.

It documents the project's SQL files, or a live database with `--source-connection`, and the result is the same either way, because the
documentation lives in the database's own comments (`COMMENT ON`). A comment's text is the description, and lines starting with
`@tag: value` add structure:

```sql
COMMENT ON COLUMN app.customer.email IS $$
The address receipts and password resets are sent to.

@business-name: Customer Email
@pii: true
@classification: confidential
@retention-months: 84
@example: jane@example.com
@see-also: app.customer
$$;
```

Every tag is optional. The starter set is `@description`, `@business-name`, `@owner`, `@classification`, `@pii`, `@deprecated`,
`@since-version`, `@since-date` (ISO 8601), `@retention-months`, `@retention-date` (ISO 8601, UTC; not with `@retention-months`),
`@example`, and `@see-also`. Any other `@name: value` is kept as a custom attribute. [`documentation.md`](../documentation.md) has the
full rules and the output format.

- **A tag on the wrong kind of object** gets a warning, which does not change the exit code. `@pii` applies to tables, views, and
  columns; `@classification` and the retention tags to schemas, tables, views, and columns; `@example` to columns, constraints,
  routines, and types.
- **A tag whose value cannot be read** is shown with its object and listed in a summary. The files are still written, but the exit code
  is 1 unless you pass `--no-fail-on-doc-errors`.

Options:

- `--project <path>` - the project to document (default `pgproj.json`); not read with `--source-connection`.
- `--source-connection [<connection-string>]` - document a live database instead (the source). Given with no value, it reads the
  `PGPROJ_SOURCE_CONNECTION` environment variable, then `sourceConnectionString` from `pgproj.json`; the variable and the file alone never make
  `document` read a database. Grants, role memberships, and owners are not read.
- `--format <list>` - any of `html`, `markdown` (or `md`), and `json`, as a comma-separated list or the option repeated (default `html`
  and `json`).
- `--output <directory>` - where to write `database-documentation.html`, `.md`, and `.json`, relative to the folder of `pgproj.json` (to
  the current directory with `--source-connection`); default `docs`.
- `--existing overwrite|backup|keep` - what to do with a file already there. By default, a file `document` wrote (it has a
  `Generated by pgproj document` line) is backed up to `name.<yyyy-MM-dd-HH-mm-ss>.ext` (UTC), and any other file is kept and the new
  documentation goes to the stamped name, with a line saying so even with `--quiet`. The files are handled together. Old backups are
  not pruned.
- `--schemas <list>`, `--exclude-schemas <list>` - document only, or all but, the schemas named (a comma-separated list or the option
  repeated; not both; `public` only if named; an unknown name is an error). The diagram follows. Run the command once per schema for a
  document of each.
- `--no-fail-on-doc-errors` - exit with 0 even when a comment's tags have errors.

The HTML is one page with a contents list, the data dictionary, and the diagram, which Mermaid draws; the page loads Mermaid from a CDN,
so it needs internet access to show the diagram. The Markdown has the diagram in a `mermaid` block. The JSON is the canonical form
(`schemaVersion` 1). Schemas, constraints, triggers, and policies take their description from their comment like everything else, and so
do the columns of views and the fields of composite types. Not
documented yet: event triggers, extensions, cluster-wide objects, and comments on rules and on domain constraints (pgproj does not model
those comments).

Examples:

```sh
pgproj document
pgproj document --format html,markdown,json --output ./docs
pgproj document --source-connection "Host=localhost;Database=mydb;Username=postgres;Password=postgres" --format markdown
pgproj document --schemas billing --output ./docs/billing
pgproj document --existing overwrite --no-fail-on-doc-errors
```

## Promoting between environments

`--source-connection` on `diff` and `publish` (and on `build`) makes a live database the desired state in place of the project's SQL files; see
[workflow 3](../README.md#3-promote-one-database-to-another) and the full guide, [Promoting between environments](notes/promoting-environments.md). The rules, in short:

- **The target is the only database changed.** The source is read in a read-only transaction and must not be the target (the same host, port, and database, or the same server
  reached under another name, is refused). Every check `publish` makes runs against the target.
- **The direction is stated**: the first line of output is `Source A -> Target B`. `diff` marks `+` for the source only, `-` for the target only, `~` for both with a difference;
  what only the target has is dropped by `publish`, which needs `--allow-destructive`. `--format json` names both databases.
- **The project file is optional.** When present it still supplies `schemas` (scoping both databases), `searchPath`, the `exclude*` settings, and the `predeploy` and `postdeploy`
  scripts, which run against the target. A project with `tenants` is refused.
- **Environments differ in roles and grants**, so add `--exclude-owners`, `--exclude-grants`, and `--exclude-role-memberships`; they apply to both sides.
- **The source is read as `init` reads a database**, so what `init` cannot read ([`init`](#init) lists it) is not promoted.

## Options every command takes

### Help

`pgproj --help` (or `-h`) lists the commands. `pgproj <command> --help` shows that command's description, its options with the value
each takes, and examples.

### How much is printed: `--verbose` and `--quiet`

By default a command prints a header (the project, its source folder, and the number of files), any problems it found, and its result.

- `--quiet` prints only the result and the errors. It leaves out progress lines (`Loaded project:`, `No diagnostics.`,
  `Wrote deployment script:`, `Executed deployment script.`), warnings (a feature the server lacks; a missing tablespace or role on
  `--dry-run` or `--output`), and notices (`init`'s list of kinds it cannot read). `publish` still prints its script and `diff` its
  changes; errors and refusals still go to standard error, and the exit code is the same.
- `--verbose` also writes what the command is doing to standard error, each line starting `[verbose]`: the project file read, each SQL
  file parsed, the problems by severity, the server version, how long each step took, and the number of changes (`init` also names each
  file it writes). Standard output is unchanged, so `pgproj publish --dry-run --verbose > deploy.sql` still holds only the script.

They cannot be used together. The result goes to standard output and everything else to standard error, except that without `--quiet`
the header and problems go to standard output too (but to standard error for `build --format json` and `diff --format json`, so that standard output
holds only the JSON). Passwords are masked (`Password=*****`) in every line, at every level.

One line is written even with `--quiet`: the one saying `--erd` or `document` kept a file you wrote and put its output under another
name, because the output is not where you asked for it.

### Entity relationship diagram: `--erd`

`init`, `build`, `diff`, and `publish` take `--erd [path]`, which also draws the project's tables, columns, and foreign keys as a
[Mermaid](https://mermaid.js.org/) diagram. It is drawn from the project's SQL files, never from a database, and is not written if the
project has errors.

- `--erd` alone writes `erd-<yyyy-MM-dd-HH-mm-ss>.mmd` (UTC) beside `pgproj.json`; `--erd docs/model` writes `docs/model.mmd`. `build`,
  `diff`, and `publish` also write a `.md` that GitHub and VS Code render.
- `--erd-html` also writes a web page that draws the diagram in any browser.
- `--erd-schemas <list>` or `--erd-exclude-schemas <list>` draws only, or all but, the schemas named.
- `--erd-existing overwrite|backup|keep` says what to do with a file already at the path. By default a diagram `--erd` wrote earlier is
  backed up, and any other file is kept.

Because the path is optional, `--erd` takes the next word as its path unless it starts with `-`; put `--erd` last for the default name.
[Entity relationship diagrams](notes/erd.md) covers the file names, what is drawn, and the options in full.

---

[README](../README.md) · [All notes](notes/README.md)
