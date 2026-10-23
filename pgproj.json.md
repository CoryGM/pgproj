# pgproj.json Reference

`pgproj.json` is the project file that `pgproj` reads to locate your SQL source files, decide which schemas are in scope, run pre- and post-deploy scripts, and (optionally)
remember which databases a project comes from and deploys to. This page is the reference for the file: every property, how it is read, and what each command does with it. For the
tasks (a project in source control, starting from a database, promoting one database to another), see the [README](README.md), and for the options of each command the
[command reference](docs/commands.md); for the
two connection properties and how they combine with the options and environment variables, see [Connections](README.md#connections).

## How pgproj.json is discovered

Every command (`init`, `build`, `diff`, `publish`, `document`) accepts a `--project <path>` option (for `init` it is where the new file is written, not where one is read from; the rest of this document is about the commands that read one):

- If `--project` is omitted, the CLI defaults to `pgproj.json` in the **current
  working directory**.
- A relative path (the default included) is relative to the current working
  directory. There is no search of parent folders: the file must be at that
  path.
- If the resolved file does not exist, the command fails with:

  ```text
  <Command> failed: project file was not found.
  Looked for: <full resolved path>
  Use --project <path> to specify a pgproj.json file, or create one based on pgproj.sample.json.
  ```

  (`<Command>` is `Build`, `Diff`, `Publish`, or `Document` depending on which command was run.)

## Fields

pgproj reads only the fields below; any other property is ignored. Property names
are not case-sensitive, and JSON comments and trailing commas are allowed.

| JSON property     | Required | Type            | Description |
|--------------------|----------|-----------------|--------------|
| `name`             | Yes      | string          | Project name. Must be non-empty (after trimming). Used only for display, e.g. `Loaded project: <name>`. |
| `version`          | Yes      | string          | Project version. Must be non-empty (after trimming). Currently used for validation only; not displayed by the CLI commands. |
| `source`           | Yes      | string          | Path to the directory containing your `.sql` files, relative to the directory containing `pgproj.json`. Must resolve to an existing directory. A `Scripts` folder directly under it is not read as schema. |
| `schemas`          | No       | string[]        | List of schema names in scope for the project. Blank entries are ignored, values are trimmed, each name is read as PostgreSQL reads one (folded to lower case unless it is in double quotes: `"Sales"` keeps its case), and duplicates are removed. Defaults to an empty list if omitted. |
| `sourceConnectionString` | No    | string          | The database `init` scaffolded the project from; written by `init` with the password replaced by `<password>`. Read by `document --source-connection` given with no value. See [`sourceConnectionString` and `targetConnectionString`](#sourceconnectionstring-and-targetconnectionstring). |
| `targetConnectionString` | No    | string          | Default PostgreSQL connection string of the database `diff` and `publish` compare the project with and change, when `--target-connection` and `PGPROJ_TARGET_CONNECTION` are not given. No tool writes it. |
| `predeploy`        | No       | string          | Path to a pre-deploy script, relative to the directory containing `pgproj.json`. If specified, the file must exist. |
| `postdeploy`       | No       | string          | Path to a post-deploy script, relative to the directory containing `pgproj.json`. If specified, the file must exist. |
| `excludeGrants`    | No       | boolean         | When `true`, object/schema/column privilege grants are not modeled, diffed, or deployed for this project. Defaults to `false` (grants are included). |
| `excludeRoleMemberships` | No | boolean       | When `true`, role memberships (`GRANT role TO grantee`) are not modeled, diffed, or deployed for this project. Independent of `excludeGrants`. Defaults to `false` (role memberships are included). |
| `excludeOwners`    | No       | boolean         | When `true`, the owners a project states (`ALTER ... OWNER TO`) are not modeled, diffed, or deployed, and the database's owners are not read. Independent of `excludeGrants`. Defaults to `false` (owners a project states are included). See [`excludeOwners`](#excludeowners). |
| `searchPath`       | No       | string[]        | The schemas an unqualified name is looked up in, in order, as PostgreSQL's `search_path` does (`pg_catalog` is always searched first). Each name is folded to lower case unless it is in double quotes, as in `search_path`. Defaults to `["public"]`. See [`searchPath`](#searchpath). |
| `tenants`          | No       | object          | A database with a schema per tenant: `template` (a folder), `prefix` (a name), and at most one of `file` (a CSV file) and `query` (SQL). See [`tenants`](#tenants). |

### `source` and SQL file discovery

Every `*.sql` file under `source` is read, in every subfolder, in order of its
path (not case-sensitive); objects are deployed in the order their dependencies
need, whatever file they are in. A program that uses
pgproj as a library can supply the files from memory or from an editor's unsaved
buffers instead of the disk ([`docs/library.md`](docs/library.md)); the rules on
this page are the same either way.

Two things under `source` are **not** read as schema: a folder named
`Scripts` (any case) directly under `source`, with everything below it, and the
files named by `predeploy` and `postdeploy`, wherever they are. Keep data and
one-off scripts in `Scripts` and run them with `:r` from a pre- or
post-deployment script (below). A folder called `Scripts` deeper down is still
schema.

### `schemas`

`schemas` does not filter or restrict which SQL files are parsed — all `.sql`
files under `source` are always parsed. Two things read it:

**`build`** uses it to decide whether a reference `build` cannot resolve
anywhere in the project (a foreign key's target table, a column's type, a
function call, a referenced column) is an error or a warning.

- **`schemas` is empty or omitted (the default):** pgproj has no explicit list
  of schemas this project owns, so it cannot tell "genuinely missing" apart
  from "belongs to a schema this project doesn't declare." Every otherwise-
  unresolvable reference is a **warning**, not an error — `build` succeeds,
  but the gap stays visible in the diagnostics output. This is a materially
  more permissive default than an empty `schemas` list might suggest: it is
  not "nothing is in scope," it is "there is no boundary to check against, so
  nothing is refused for being out of it."
- **`schemas` is populated:** a reference into one of the listed schemas (or
  an unqualified one, which is always assumed to belong to this project) that
  still doesn't resolve is an **error**, exactly as if `schemas` were never
  set. A reference explicitly qualified to a schema **not** in the list is a
  **warning** instead — it most likely belongs to another project or team,
  not something this project owns or should expect to find declared anywhere
  in its own SQL. Populating `schemas` is what makes checking strict again,
  only for the schemas actually listed.

**`diff` and `publish`** use it to decide which schemas of the *connected
database* to read and compare against at all:

- **`schemas` is empty or omitted (the default):** every schema the database
  exposes is read.
- **`schemas` is populated:** only those schemas (plus, automatically, every
  schema this project's own SQL files actually declare an object in or declare
  with `CREATE SCHEMA`, even one left out of `schemas` by mistake) are read from the live database. A table,
  view, function, or other object in a schema outside that combined set is
  invisible to `diff`/`publish` entirely — not modeled, not compared, never
  proposed for removal — because it most likely belongs to another project or
  team sharing the same physical database. An event trigger or a cast belongs to no schema, so it is in scope when
  the function it runs (or, for a cast, either type it converts) is in one of those schemas; an operator is in scope when its own schema is, a rule when the schema of the table or view it is on is, an aggregate when its own schema is, a publication when a table or schema it names is (or when it names nothing, as `FOR ALL TABLES` does), and a collation, a text search dictionary or configuration, or an extended statistics object when its own schema is. `pgproj init`'s scaffold command is
  unaffected: it always reads the whole database, since a brand-new project
  has no `schemas` list yet to scope against.

Either way, a reference that does resolve — anywhere in the project's own SQL
files, regardless of `schemas` — is never affected; this only changes the
severity of a reference that was already going to be reported as unresolved.

### `sourceConnectionString` and `targetConnectionString`

A connection has a direction. The **source** is a database a command reads: `init` scaffolds a project from it and `document` can document it. The **target** is
the database `diff` compares the project with and `publish` changes. `build` never connects.

`targetConnectionString` is the default target. A command takes its target from, in this order:

1. `--target-connection`, if given and non-blank;
2. the `PGPROJ_TARGET_CONNECTION` environment variable, if set and non-blank (so a CI job keeps a password off the command line);
3. `targetConnectionString`, if non-blank.

If none is available, `diff` and `publish` fail with:

```text
A PostgreSQL target connection string is required. Specify --target-connection, set PGPROJ_TARGET_CONNECTION, or add targetConnectionString to pgproj.json.
```

No tool writes `targetConnectionString`: you add it yourself, so a scaffold of one database can never point `publish` at it.

`sourceConnectionString` records where a project came from. `init` writes it (the connection it read, with the password replaced by `<password>`), and
`document --source-connection` given with no value reads it after `PGPROJ_SOURCE_CONNECTION`. A command reads a source database only when it is told to: the property
and the variable supply the connection, they never switch a command to a database.

`connectionString`, the one property these replace, is no longer read. A project file that has it is refused when loaded:

```text
Project file '<path>' has a 'connectionString' property, which is no longer read. Use 'targetConnectionString' for the database diff and publish compare and change, and 'sourceConnectionString' for the database init scaffolded the project from.
```

### `predeploy` / `postdeploy`

These optional fields identify SQL scripts to run before/after the generated
deployment operations. If present, the referenced file must exist relative to
the project file's directory, or loading fails (see [Errors](#errors) below).
A blank or whitespace-only script is treated as absent.

`publish` merges the pre-deploy script ahead of, and the post-deploy script
after, the generated operations in the deployment script (the same script that
`--output` writes and `--execute` runs). `build`, `diff`, and `publish` syntax-check them. `publish --comments-only` leaves them out: it deploys only the `COMMENT ON` statements.

#### `:r` includes

In a pre- or post-deployment script, a line that starts with `:r <path>`
(SSDT's SQLCMD syntax) is replaced by the contents of that file when the
project is loaded, so `build`, `publish --dry-run`, and `--output` all show the
full script. The `:r` is recognized only at the start of a line (indentation is
allowed) and never inside a string, a quoted identifier, a comment, or a
dollar-quoted body. The path is relative to the file that contains the line, as
in SSDT, may be wrapped in double quotes, and the included file may include
others. `\` and `/` both separate folders on every operating system
(`:r .\Seed\file.sql` and `:r ./Seed/file.sql` name the same file), so a
project written on Windows builds on Linux and macOS too; a file whose name
contains `\` therefore cannot be included. Whether case matters follows the
file system, as for the project's other files. Each include is marked with a
`-- :r <path>` comment. A missing file, a `:r` without a path, or an include
cycle stops the load and names the file and
line (a cycle names the whole chain). Other SQLCMD syntax, such as `$(name)`
variables, is not interpreted.

The scripts are syntax-checked, each file on its own (so an error is reported
at its own `file:line:column-endLine:endColumn`), and a syntax error is a build error. They are
never modeled: an `INSERT`, a `DO` block, or a `CREATE TABLE` in a script is
neither warned about nor part of the schema.

#### Running a file only when something changed

A script runs on every publish by default. A file of it whose first comment lines include `-- pgproj: run when=changed` runs
only when the publish has changes; otherwise it is left out, with the files it includes, and a comment in the script says so
(`-- pgproj: skipped seed.sql (it runs when=changed)`). Each file of a script made of `:r` includes can have its own marker. Another marker value, two
markers in a file, and a tenant's variable (`:'tenant_id'`) in these scripts are errors when the project is loaded or built; a tenant's own scripts are
`tenants.predeploy` and `tenants.postdeploy`.

### `excludeGrants` / `excludeRoleMemberships`

These optional fields let a project opt out of modeling grants and/or role memberships entirely — for example,
when a role a project's `GRANT` references doesn't exist yet in a fresh/CI database, or when a live database's
grants belong to a separate access-control process this project should never propose changes to. `build`, `diff`,
and `publish` all read them; `pgproj init` does not (there is no existing `pgproj.json` for it to read), but it
has its own `--exclude-grants`/`--exclude-role-memberships` flags (see its own section below).

The two fields are independent — a project may exclude one without the other — and their effect is total: an
excluded statement produces no model entry and no diagnostic at all, identical to deleting it from the project's
SQL. **Both sides of a `diff`/`publish` comparison are excluded together**: a project's own `GRANT`/role-membership
statements *and* the live database's matching grants are both left out, so an excluded category never appears as
a phantom "Removed" — see [Overrides](#--project-and-the-connection-overrides) for how `--exclude-grants`/
`--exclude-role-memberships` can force either direction back on for a single run.

### `excludeOwners`

An optional field that leaves object owners out of the project: the `ALTER ... OWNER TO` statements (and the `AUTHORIZATION` of a
`CREATE SCHEMA`) in the project's SQL produce no model entry and no diagnostic, and the live database's owners are not read, so an
owner never appears as a difference, an `ALTER ... OWNER TO` is never generated, and the roles an owner would name are not required to
exist. Use it for a project that runs against databases whose roles are named differently from the ones the project's SQL names.
Defaults to `false`. Like `excludeGrants`, it applies to **both sides** of a `diff`/`publish` comparison, and `--exclude-owners` on
`build`, `diff`, and `publish` overrides it for one run in either direction (`--exclude-owners false` forces owners back on); `pgproj
init --exclude-owners` scaffolds no owner statement and writes `"excludeOwners": true` into the new `pgproj.json`.

When owners are modeled, the project states the owners it chooses to: an object whose owner is not stated is not compared. Once a
project states any owner, `build`, `diff`, and `publish` say how many objects state none.

### `searchPath`

`init` sets `searchPath` itself when the database needs it: if a function body or a type names another function or type without a schema, because the database found it through the `search_path` of the session that created it, the generated file lists the fewest schemas that find every such name, followed by `public`, and `init` says so. A database that needs nothing gets no `searchPath`.

PostgreSQL finds a name written without a schema through `search_path` only: `pg_catalog`, then each schema of the path in order.
It does that when it creates a view, a `LANGUAGE sql` function or procedure, a column (its type and `DEFAULT`), a `CHECK`, an index,
a domain, a type, a trigger, or a policy, and rejects the statement when the name isn't there. `searchPath` tells pgproj which path the
project is written for:

- **`build`** reports, as an **error**, an unqualified reference to a project object that is only in a schema outside the path. The
  message names the schema it is in and suggests qualifying it or adding that schema to `searchPath`. A name that isn't a project
  object at all (an extension's function, for example) is left to the usual "references missing" checks, and a PL/pgSQL body is not
  read, since PostgreSQL does not resolve its names until it runs.
- An **unqualified declaration** (`CREATE TABLE t`) is created in the path's first schema, and an **unqualified reference to an
  existing object** (`REFERENCES region`, `CREATE INDEX ... ON region`, `ALTER TABLE region`, a comment's or grant's target) is the
  one the path finds first, as in PostgreSQL. An unqualified type or trigger function the path finds outside `public` is written
  with its schema in the generated SQL.
- **When `searchPath` is set**, `publish` starts its script with `SET search_path TO ...`, and `diff`/`publish` run their session
  under it, so what `build` checked is what is deployed, whatever the deploying role's own path is. Without `searchPath` the path
  is `public` and nothing is added to the script.

`"$user"` is not accepted: it names the role that deploys, which `build` cannot know. List the schema itself. An empty list, a blank
entry, or a schema listed twice is refused.

The live database is always read with `search_path` set to `public`, so how PostgreSQL prints a name (with or without its schema)
doesn't depend on the connecting role's path or on `searchPath`.

### `tenants`

For a database that keeps one schema per tenant, all holding the same objects. The tenants' objects are written once, in a template, and
[Schema-per-tenant databases](docs/notes/tenants.md) explains the whole feature with examples, and [`samples/SchemaPerTenant`](samples/SchemaPerTenant/README.md) is a complete project that uses it. `build` reads and checks it, `diff` compares each tenant with the template, `publish` deploys the shared
changes and then each tenant, and `init --tenant-prefix` writes it.

```json
"tenants": {
  "template": "./sql/Tenant",
  "prefix": "tenant",
  "file": "./tenants.csv"
}
```

| Property | Required | Type | Description |
|---|---|---|---|
| `template` | Yes | string | The folder of SQL every tenant's schema holds, relative to the directory containing `pgproj.json`. Every `*.sql` file under it is read, in any subfolder. It may be inside `source` (it is then not read as part of the shared project), but it cannot be `source` or hold it. The pre- and post-deploy scripts are never part of it. |
| `prefix` | Yes | string | The start of every tenant's schema name: the tenant `acme`'s schema is `<prefix>_acme`. Folded to lower case unless double-quoted. It is also the template's stand-in schema. |
| `file` | No | string | A CSV file listing the tenants, relative to the directory containing `pgproj.json`: a header line naming a `tenant` column, then one identifier per line, taken exactly as written. |
| `query` | No | string | A query that returns the tenants' identifiers, one column. Not run by `build`. |
| `predeploy` | No | string | A script run in each tenant's transaction before the template's changes, relative to the directory containing `pgproj.json`, with `:r` includes. Its files are never read as schema. See [a tenant's own scripts](docs/notes/tenants.md#a-tenants-own-scripts). |
| `postdeploy` | No | string | The same, run after the template's changes. |

A tenant's scripts use psql's variables, `:"tenant_schema"` (a quoted name), `:'tenant_schema'` and `:'tenant_id'` (strings), filled in with each
tenant's values, and the transaction sets `pgproj.tenant_schema` and `pgproj.tenant_id` for function and `DO` bodies. They run on every publish for
every tenant, in this order inside the tenant's transaction: its schema created if it is new, the settings, `predeploy`, the template's changes,
`postdeploy`; unless a file's marker, a comment at its top, says otherwise: `-- pgproj: run when=tenant-changed` (only for a tenant with changes) or
`-- pgproj: run when=tenant-created` (only when pgproj creates the tenant's schema, or the schema has no objects yet). A file that does not run is left
out with what it includes, and the script says so.

With neither `file` nor `query`, the tenants are the database's schemas named `<prefix>_...`.

`build` reads the template once, for the stand-in schema (the prefix): an unqualified declaration is created there, and an unqualified name is looked for
in `pg_catalog`, the stand-in, then `searchPath`. It reports, as errors, a template statement that creates or changes an object in a shared schema, names
the tenant's own object with a schema, refers to one tenant's schema, or holds something that belongs to the whole database, and a shared statement that
declares or refers to an object in the stand-in or a tenant's schema; and, as a warning, template functions and procedures that do not set `search_path`.
Its header adds two lines, the template and where the tenants come from.

`tenants` is refused when loading if: `template` or `prefix` is missing; the template folder does not exist or is (or holds) `source`; the prefix starts
with `pg_`, is `public`, `pg_catalog`, or `information_schema`, or is too long to leave room for `_` and an identifier within PostgreSQL's 63-byte name;
`schemas` or `searchPath` lists the prefix or a schema named `<prefix>_...`; both `file` and `query` are given; or the file does not exist or has a problem
(a missing `tenant` column, an empty, repeated, or space-padded identifier, a schema name over 63 bytes, or a malformed line; each named with its line);
or `predeploy` or `postdeploy` names a file that does not exist, or one of their files has a marker a tenant's script does not take
(`when=tenant-changed` and `when=tenant-created` are).

## How pgproj.json interacts with each command

`init` is the exception below: it **writes** a new `pgproj.json` rather than loading an existing one (see its own
section). `build`, `diff`, `publish`, and `document` (unless it is given `--source-connection`, which documents a live database and reads no project) all start by loading the project the same way:

1. Read and validate `pgproj.json`.
2. Read the SQL files under `source`, the deploy scripts and their `:r` includes, the tenant template, and the tenant file, once, at the start, so
   a file saved while the command runs cannot mix two versions into one result.
3. Parse every SQL file with PostgreSQL's parser.
4. Build the model of the schema from them, checking the references between objects.

These commands then print:

```text
Loaded project: <name>
Source path: <resolved source path>
SQL files: <count>
```

followed by the semantic model's diagnostics.

`init`, `build`, `diff`, and `publish` also take `--erd [path]` (`document` always includes the diagram), which writes a Mermaid entity relationship diagram of the project's tables built from its SQL files (a `.mmd` file, and for `build`, `diff`, and
`publish` a companion `.md` too; `init` writes only the `.mmd` and links it from its report). The path is relative to the directory of `pgproj.json` unless absolute; with no value the file is
`erd-<yyyy-MM-dd-HH-mm-ss>.mmd` (UTC) there. Nothing is written when the project has errors. `--erd-schemas <list>` or `--erd-exclude-schemas <list>` (not both, and only with `--erd`) draws only, or all but, the schemas named. `--erd-html` (only with `--erd`) also writes a `.html` page of the same name that draws the diagram in a browser (it loads Mermaid from a CDN). `--erd-existing overwrite|backup|keep` (only with `--erd`) says what to do with a file already at the path; by default a file `--erd` wrote is backed up to a time-stamped name and any other file is kept. See the [command reference](docs/commands.md#entity-relationship-diagram---erd).

Every command also takes `--verbose` (more detail on standard error, each line prefixed `[verbose]`) and `--quiet` (these lines, the diagnostics' `No diagnostics.` line, and warnings are left out; the result and errors are still written); the two cannot be combined. See [How much is printed](docs/commands.md#how-much-is-printed---verbose-and---quiet).

### `init`

- Does not load an existing `pgproj.json`; connects to `--source-connection` (or `PGPROJ_SOURCE_CONNECTION`), scaffolds one SQL file per
  object from the live schema, and writes a **new** `pgproj.json` to `--project` (default `pgproj.json`).
- Refuses to run if the resolved `--project` path already exists, or if the resolved `--output`/`-o` directory
  (default `./sql`, relative to `--project`'s directory) exists and is not empty — unless `--force` is given.
- Creates the `--output`/`-o` directory, including any missing parent directories, if it doesn't already exist —
  even for a database with nothing to scaffold, since the `source` directory written into `pgproj.json` must
  resolve to an existing directory for `build`/`diff`/`publish` to load the project afterward.
- The written file sets `name` (from `--name`, or the database name parsed out of `--source-connection`), `version`
  (`"1.0"`), `source` (the relative path to `--output`), and `sourceConnectionString` (`--source-connection` as given, with
  the password's value replaced by `<password>`). `predeploy` and `postdeploy` are not set, and `schemas` is set only when `--schemas` or `--exclude-schemas` was used.
- `--schemas <list>` scaffolds only the schemas named and `--exclude-schemas <list>` all but the schemas named (comma-separated, or the option repeated; not both; a
  name in `--schemas` that the database lacks is an error; `public` is only included if `--schemas` names it or `--exclude-schemas` does not). When either is used, the
  written file's `schemas` field lists the schemas that were modeled; otherwise `schemas` is not set.
- `--report` also writes `init-report-<yyyy-MM-dd-HH-mm-ss>.md` (UTC) at the root of `--output`: the arguments, a summary, the objects modeled with their files, and what was
  not modeled (not supported, or excluded) and what is cluster-wide. See [The report](docs/commands.md#the-report).
- The files are written under Title Case folders (`Schema/<schema>/Table/`, `View/`, `EventTrigger/`, ...). The case of a folder name is never significant when a project is
  read: every `*.sql` under `source` is read.
- `--exclude-grants`/`--exclude-role-memberships`/`--exclude-owners` skip scaffolding that category's SQL entirely, and also write
  `excludeGrants`/`excludeRoleMemberships`/`excludeOwners: true` into the generated `pgproj.json`; otherwise a later `diff`/`publish` against the
  same live database would see what was deliberately left out of the scaffold and propose removing it.
- Does not read `predeploy`/`postdeploy` scripts, since there is no existing project to read them from.
- With `--tenant-prefix`, writes `tenants`: `template` (`<source>/Tenant`, where one tenant, the first by name or `--tenant-from`, is
  scaffolded with its schema name taken out), `prefix`, and, with `--tenant-file`, `file` (`./tenants.csv`, written beside `pgproj.json` with the tenants
  found) or, with `--tenant-query`, `query`. The tenants' schemas are not scaffolded as shared ones, and `schemas` is still set only by `--schemas` or
  `--exclude-schemas`.

### `build`

- With `--source-connection`, the project's SQL files are not read: the source database is scaffolded as `init` does (in a read-only transaction) and built, so `--dump-model` and `--erd` describe it. `pgproj.json` is optional; `tenants` is an error. `build` takes no target.
- Loads the project and prints diagnostics from the semantic model.
- Does **not** use `targetConnectionString` or `sourceConnectionString` — no database connection is made.
- Exits with code `0` if the model has no errors, otherwise `1`.
- `--format json` writes the result as one JSON document on standard output (the project, the documents read, the diagnostics with their places, and the counts:
  the start of the document `diff --format json` writes); the project header and the diagnostics' text go to standard error. A project that is not found or
  not valid is a document with that `status`, and so, with `--source-connection`, is a refused run (a malformed connection string, a project with tenants) or a
  database that cannot be reached (`connectionFailed`); a build of a database adds `source`. The exit code is the same. `--format text` is the default.
- Optional `--dump-model <file>` writes the built semantic model to `<file>` as
  indented JSON (a debugging aid). Nothing is written unless it is supplied.
- `--exclude-grants`/`--exclude-role-memberships`/`--exclude-owners` override `excludeGrants`/`excludeRoleMemberships`/`excludeOwners` from
  `pgproj.json` for this run (see [Overrides](#--project-and-the-connection-overrides)).

### `diff`

- With `--source-connection`, the project's SQL files are not read: the source database is scaffolded as `init` does (in a read-only transaction) into an in-memory project (nothing is written to disk; a diagnostic names its files as `source:/...`), which stands in for the project in everything below. The `pgproj.json` is optional here; when present it still supplies `schemas`, `searchPath`, the `exclude*` settings, and `predeploy`/`postdeploy` (run against the target), and a `tenants` block is an error. See [Promoting between environments](docs/commands.md#promoting-between-environments).
- Loads the project; stops (exit code `1`) if the semantic model has errors.
- Resolves the target connection string via `--target-connection`, then `PGPROJ_TARGET_CONNECTION`, then
  `targetConnectionString` from `pgproj.json` (see [Overrides](#--project-and-the-connection-overrides)).
- Connects to PostgreSQL, builds a live database model, and diffs it against the
  project's semantic model.
- Compares views by PostgreSQL's own rendering: each project view that also exists in the database is created as
  a temporary view inside a transaction that is always rolled back, so PostgreSQL renders both sides the same way.
  `publish` does the same. A partition's bound is compared the same way, attached to a temporary copy of its live
  parent; so is a column default, on a temporary column of the live column's type, and a generated column's
  expression or a partial index's predicate, on a temporary copy of the live table. This needs the default
  `TEMPORARY` privilege on the database; without it, these are compared by their text, which can show an unchanged
  one as modified.
- Lists each added, removed, and modified object (a modified one with each property that changed, old -> new, named as
  it is written: `Default`, `Query`, `Columns changed`), then prints the `Added: n`/`Removed: n`/`Modified: n` counts.
- `--format json` writes the whole comparison (the project, the documents read, the diagnostics, the change set, and the counts) as one JSON document on standard output
  instead; the project header and diagnostics text go to standard error. When nothing could be compared (the project has errors, the run is refused, a database
  cannot be reached), the document has that `status` and what was read, and the exit code is `1`. `--format text` is the default.
- Exits with code `0` when it ran, whatever it found, and `1` on an error. With `--exit-code`: `0` when there are no
  differences, `2` when there is an added, removed, or modified object, and `1` on an error; warnings are not differences.
- `--exclude-grants`/`--exclude-role-memberships`/`--exclude-owners` override `excludeGrants`/`excludeRoleMemberships`/`excludeOwners` from
  `pgproj.json` for this run, applied symmetrically to both the project's own model and the live database read
  (see [Overrides](#--project-and-the-connection-overrides)).
- With `tenants`, the shared schemas are compared once, against every schema but the tenants' (or `schemas`), then each tenant's schema
  against the template read for it, with `search_path` set to the tenant's schema and then `searchPath` while PostgreSQL renders it. The tenants are found
  by the prefix, the query (run in a read-only transaction; a query whose table does not exist yet is a warning, and the tenants are not read), or the
  file; `--tenants a,b` narrows them. The output groups the tenants by the changes they need, shown in the stand-in schema, and names new, up-to-date, and
  former tenants (a former tenant's schema is never dropped); the counts, and `--exit-code`, add up every tenant. See
  [Schema-per-tenant databases](docs/notes/tenants.md#comparing-diff).

### `publish`

- Same project-loading and connection-resolution behavior as `diff`.
- Additionally works out the order of the changes from the dependencies of both the project's objects and the database's (so a removal, or an
  object dropped and created again around a change, is ordered safely), and writes the deployment script, with the pre- and post-deploy scripts.
- `--output <path>` writes the script to a file (creating directories as
  needed).
- The script is printed to the console unless `--execute` is specified without
  `--dry-run`.
- `--execute` runs the generated script against the connected database, unless
  `--dry-run` is also specified (in which case the script is only printed).
- Before executing, `publish` fails with exit code `1` if the script has a change pgproj cannot write SQL for (it appears in the script as a
  `-- TODO` comment). It lists the affected objects and runs nothing; use `--output`/`--dry-run` to review the script.
- Also before executing, `publish` fails with exit code `1` if the project uses a construct the connected server is too old to
  accept (for example a `NOT ENFORCED` constraint on a server older than PostgreSQL 18). It names each construct and runs
  nothing. When the script is only printed or written (`--dry-run`, `--output`, no `--execute`), it may be meant for another
  server, so `publish` prints the same list as a warning on standard error and goes on.
- Also before executing, `publish` fails with exit code `1` if a table names a tablespace the server does not have, or a
  grant, role membership, default privilege, or owner names a role the server does not have and the pre-deploy script does not
  create. It lists each and runs nothing; `--dry-run` and `--output` print them as warnings. pgproj never creates a
  tablespace or a role (both are cluster-wide). A table that names the database's default tablespace is read as naming none,
  as PostgreSQL stores it.
- Also before executing, `publish` stops with exit code `1` when a collation, or a text search dictionary's template or
  configuration's parser, has changed and something uses it: PostgreSQL cannot alter it and refuses to drop it, so the script
  names the object and what uses it instead of failing partway.
- Also before executing, `publish` fails with exit code `1` if the script contains an
  operation that can discard data or access (a dropped table, column, materialized view,
  extension, or composite type field; a changed column type that can lose a value, so not a widening; a cast, operator, aggregate, rule,
  publication, or event trigger that is dropped and not created again; a revoked grant or role
  membership; or a changed owner), unless `--allow-destructive` is also given (see
  [Destructive changes](docs/notes/destructive-changes.md)). It lists the affected operations and
  their reasons and runs nothing; use `--output`/`--dry-run` to review the script, or add
  `--allow-destructive` to run it anyway. A revoked grant or removed role membership is excluded
  from this check when `excludeGrants`/`excludeRoleMemberships` also excludes it from the model.
- Also before executing, `publish` fails with exit code `1` if the script is estimated to need more locks than the server's lock table holds
  (`max_locks_per_transaction * (max_connections + max_prepared_transactions)`), unless `--allow-lock-limit` is given; from 80% of it, and with
  `--dry-run` or `--output`, it warns. See [Locks and large tables](docs/notes/locks.md).
- Before anything runs (with `--dry-run`, `--output`, or `--execute`), `publish` warns about each change that rewrites, scans, or blocks writes to a
  table that already exists, and `--lock-timeout <duration>` bounds how long each statement waits for a lock. See
  [Locks and large tables](docs/notes/locks.md).
- With `--concurrently`, the slow part of new indexes and constraints on tables that already exist runs after the transaction commits, one
  statement at a time: `CREATE INDEX CONCURRENTLY`, a changed index swapped in under a temporary name, a unique key added `USING INDEX`, and
  `VALIDATE CONSTRAINT` for a foreign key or `CHECK` added `NOT VALID`. A failed statement has what it built dropped and makes `publish` exit
  with `1`; the next run does the rest. There is no `pgproj.json` setting for it. See
  [Locks and large tables](docs/notes/locks.md#--concurrently).
- Also before executing, `publish` fails with exit code `1` if the script has a statement only a superuser can run (an event trigger
  created, changed, or dropped; `ALTER TABLE ... TRIGGER ALL` on a table's internal foreign-key triggers; a routine created or replaced `LEAKPROOF` or in an untrusted language), unless
  `--allow-superuser-statements` is given, and then if the connected role is not a superuser. It lists each statement and runs
  nothing; a script that is printed or written starts with a comment naming them. See
  [Statements only a superuser can run](docs/notes/superuser-statements.md).
- `--exclude-grants`/`--exclude-role-memberships`/`--exclude-owners` override `excludeGrants`/`excludeRoleMemberships`/`excludeOwners` from
  `pgproj.json` for this run, applied symmetrically to both the project's own model and the live database read
  (see [Overrides](#--project-and-the-connection-overrides)).
- With `tenants`, the shared changes are published in one transaction, with the `predeploy` and `postdeploy` scripts, then each tenant in a
  transaction of its own, its script starting with `SET search_path TO` the tenant's schema and then `searchPath`; a tenant the query or file lists whose
  schema does not exist is created. Every part is checked before anything runs (when the query reads a table the shared changes create, the tenants are
  read and checked after the shared changes are published). A failure stops the run, keeping what was published and naming what was not; `publish`
  again finishes. A former tenant's schema is never dropped. `--tenants a,b` publishes only those tenants. See
  [Schema-per-tenant databases](docs/notes/tenants.md#deploying-publish).

## `--project` and the connection overrides

- `--project <path>` (all commands): overrides the default `pgproj.json`
  location. It does not change any values inside the file — it only changes
  *which* file is loaded.
- `--target-connection <connection-string>` (`diff` and `publish` only): the database to compare with and change. Resolution order:
  1. `--target-connection`, if provided and non-blank.
  2. Otherwise, the `PGPROJ_TARGET_CONNECTION` environment variable, if non-blank.
  3. Otherwise, `targetConnectionString` from `pgproj.json`, if non-blank.
  4. Otherwise, the command fails with the "target connection string is required"
     error shown above.
- `--source-connection [<connection-string>]` (`init` and `document`): the database to read. `init` takes it from the option, then `PGPROJ_SOURCE_CONNECTION`
  (it has no project file yet), and fails if neither is set. `document` reads a database only when the option is on the command line; given with no value it
  takes `PGPROJ_SOURCE_CONNECTION`, then `sourceConnectionString` from `pgproj.json`.
  On `build` it models the source database in place of the project's SQL files, with the same rules, and `build` takes no target.
  On `diff` and `publish` it makes the source database the desired state instead of the project's SQL files; a bare option takes `PGPROJ_SOURCE_CONNECTION` only, and neither that variable nor `sourceConnectionString` turns the mode on by itself.
- `--exclude-grants`/`--exclude-role-memberships` (`build`, `diff`, and `publish`): override
  `excludeGrants`/`excludeRoleMemberships` from `pgproj.json` for that invocation, in either direction — passing
  `--exclude-grants false` forces grants back on even when `pgproj.json` sets `excludeGrants: true`. Resolution
  order, independently per flag:
  1. The CLI flag, if passed (`--exclude-grants` alone means `true`; `--exclude-grants true`/`--exclude-grants
     false` set it explicitly).
  2. Otherwise, the matching field from `pgproj.json` (`false`/absent by default).

## Errors

A project file can be missing, or not valid. Either way the command prints the message below and exits with code `1`
(a program that uses the library gets the status `ProjectNotFound` or `ProjectInvalid`, with the same message).

### Project file not found

When there is no file at the `--project` path, or at `pgproj.json` in the current directory, every command prints:

```text
<Command> failed: project file was not found.
Looked for: <full path>
Use --project <path> to specify a pgproj.json file, or create one based on pgproj.sample.json.
```

### Invalid or incomplete project file

Each command prints `<Command> failed: <message>`, for:

- **Invalid JSON**: `Project file '<path>' contains invalid JSON: <details>`
- **Missing `name`**: `Project file '<path>' is missing required property 'name'.`
- **Missing `version`**: `Project file '<path>' is missing required property 'version'.`
- **Missing `source`**: `Project file '<path>' is missing required property 'source'.`
- **`source` directory does not exist**: `The source directory '<full path>' configured in '<project path>' does not exist.`
- **`predeploy`/`postdeploy` file does not exist**: `The file '<full path>' configured in 'predeploy'/'postdeploy' for '<project path>' does not exist.`
- **`:r` file does not exist**: `<script path>:<line>: ':r <path>' names a file that does not exist: '<full path>'.`
- **`:r` without a path**: `<script path>:<line>: ':r' needs the path of a file to include.`
- **`:r` include cycle**: `<script path>:<line>: ':r <path>' includes a file that is already being included: <a> -> <b> -> <a>.`
- **`tenants` not valid**: `Project file '<path>': <what is wrong>`, for each case listed under [`tenants`](#tenants); a problem in the tenant file is `The tenant file '<path>' has <n> problem(s):` followed by one `<file>:<line>: <problem>` line each.
- **`connectionString` present**: `Project file '<path>' has a 'connectionString' property, which is no longer read. Use 'targetConnectionString' ...` (see [`sourceConnectionString` and `targetConnectionString`](#sourceconnectionstring-and-targetconnectionstring)).

Additionally, `diff` and `publish` fail when no target connection string is available:

```text
Publish failed: A PostgreSQL target connection string is required. Specify --target-connection, set PGPROJ_TARGET_CONNECTION, or add targetConnectionString to pgproj.json.
```

(the message is prefixed with `Diff failed:` when running `diff` instead.)

A connection string that is not valid stops every command that connects, with exit code `1`, before any connection is tried, and says what is wrong
without repeating a password:

```text
Diff failed: The connection string is not valid: the value of 'port' is not one it takes: The input string 'notaport' was not in a correct format.
Diff failed: The connection string is not valid: 'hots' is not a setting PostgreSQL connection strings have (for example Host, Port, Database, Username, Password).
Diff failed: The connection string is not valid: it is not a list of key=value settings separated by semicolons (the problem starts at character 16).
```

## Example pgproj.json files

### Minimal (required fields only)

```jsonc
{
  "name": "MyDatabase",
  "version": "1.0.0",
  "source": "./sql"
}
```

With this file, `build` will parse every `.sql` file under `./sql` (relative to
the `pgproj.json` file's directory). `diff` and `publish` will require
`--target-connection` (or `PGPROJ_TARGET_CONNECTION`) to be set, since no `targetConnectionString` is
configured.

### Full example

```jsonc
{
  "name": "MyDatabase",
  "version": "1.0.0",
  "source": "./sql",
  "schemas": [ "public", "app" ],
  "targetConnectionString": "Host=localhost;Database=mydatabase;Username=postgres;Password=postgres",
  "predeploy": "./scripts/predeploy.sql",
  "postdeploy": "./scripts/postdeploy.sql",
  "excludeRoleMemberships": true
}
```

With this file:

```bash
pgproj build
pgproj diff
pgproj publish --output ./deploy.sql --dry-run
```

all work without `--target-connection`, because `targetConnectionString` supplies the
default. Passing `--target-connection "..."` (or setting `PGPROJ_TARGET_CONNECTION`) overrides the configured
value for that invocation only.
