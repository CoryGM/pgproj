# GitHub Copilot CLI Instructions for pgproj

These instructions tell GitHub Copilot how to safely modify or extend the CLI
without breaking command behavior, option contracts, or help text.

---

## 1. CLI Contract (Do Not Break)

Copilot must preserve the following commands:

- pgproj init
- pgproj build
- pgproj diff
- pgproj publish

Copilot must not:
- rename commands
- remove commands
- add new top-level commands without explicit instruction
- change option names
- change option meanings
- change required/optional status unless instructed

`pgproj init` scaffolds a new project from a live database through the engine (`PgProjEngine.ScaffoldAsync`), which calls
`LiveDatabaseModelLoader`, the same class `diff`/`publish` use (through the engine) to read one. See `copilot-instructions.md` §1, "Live Database Loader / Scaffold", for the
full rule: any gap that adds support for a PostgreSQL feature a live database can contain is not done until
`init`'s scaffold output covers it too, or the gap's issue says explicitly why not.

---

## 2. Option Contract

Copilot must preserve:

- --project
- --source-connection / --target-connection (G-304a, replacing `--connection`, which is removed with no alias; the user's decisions on #197, 2026-10-15). A connection has a direction. `--source-connection [value]`
  is a database a command reads: `init` (required: the option or `PGPROJ_SOURCE_CONNECTION`) scaffolds a project from it, and `document` documents it instead of the project's files. `--target-connection <value>` is the
  database `diff` and `publish` compare with and change. Each falls back to an environment variable (`PGPROJ_SOURCE_CONNECTION`, `PGPROJ_TARGET_CONNECTION`) and then to `pgproj.json` (the target's
  `targetConnectionString`; the source's `sourceConnectionString`, read by `document --source-connection` given with no value). `document` reads a database only when `--source-connection` is on the command line.
  `build` takes `--source-connection [value]` only (G-304c: it models a live database in place of the project's SQL files, same rules as for `diff`), never a target; `diff` and `publish` also take `--source-connection [value]` (G-304b: a live database as the desired state in place of the project's SQL files, entered only when the option is on the command line, a bare option reading `PGPROJ_SOURCE_CONNECTION`; the file's `sourceConnectionString` and the variable alone never switch it; read in a read-only transaction; not the target; source -> target stated in the output; refused with `tenants`); `init` writes `sourceConnectionString`, never `targetConnectionString`
- --output (`publish`'s script-output-file path, and `init`'s scaffolded-SQL output directory — same name,
  different meaning per command; do not conflate them)
- -o (short alias for `init --output`)
- --execute
- --dry-run
- --force (`init`'s overwrite-safety opt-in)
- --name (`init`'s generated-project-name override)
- --allow-destructive (`publish`'s destructive-change opt-in, G-35)
- --lock-timeout <duration> (`publish`: wait at most this long for each lock; past it the script is rolled back and the waiting statement named; off unless given; a printed or written script starts with `SET lock_timeout`; the duration is validated (`5s`, `500ms`, `2min`, milliseconds) — G-270, scoped by the user 2026-10-13. Also: before anything runs, `publish` warns about each change that rewrites, scans, or blocks writes to a table that already exists)
- --concurrently (`publish`: after the transaction commits, one statement at a time, build each new index on a table that already exists with `CREATE INDEX CONCURRENTLY`
  (a partitioned table: `ON ONLY` the parent in the transaction, then each partition's built and attached), swap in a changed plain index built under `<name>_pgproj_new`,
  build a new unique key or a primary key on columns already `NOT NULL` as an index and add it `USING INDEX`, and validate each new foreign key and `CHECK` added
  `NOT VALID` (a foreign key on a partitioned table only from PostgreSQL 18); a failed step has what it built dropped, is named, and exits `1`; a printed or written
  script puts these below the line `-- pgproj --concurrently: ...`; not with `--comments-only`; no `pgproj.json` setting — G-271, design approved with corrections
  by the user 2026-10-14. Also: an `INVALID` index is read (`diff` reports `Valid`), rebuilt by `publish`, and left out by `init` with a warning)
- --allow-lock-limit (`publish`: let `--execute` run a script estimated to need more locks than the server's lock table holds, `max_locks_per_transaction *
  (max_connections + max_prepared_transactions)`; without it `--execute` refuses such a script before anything runs; from 80% of it `publish` warns; `--dry-run`
  and `--output` only warn; per run, no `pgproj.json` setting - G-280, option (b), the user's decision of 2026-10-13, details 2026-10-14)
- --allow-superuser-statements (`publish`: consent, per run, for `--execute` to run statements only a superuser can run: an event trigger created, changed, or dropped, a table's internal foreign-key triggers enabled or disabled (`ALTER TABLE ... TRIGGER ALL`, G-261), and a routine created or replaced `LEAKPROOF` or in an untrusted language; without it `--execute` refuses such a script even for a superuser, with it a role that is not a superuser is refused before anything runs; a printed or written script starts with a comment naming them; no `pgproj.json` setting — G-265, design signed off by the user 2026-10-13)
- --comments-only (`publish`: deploy only the `COMMENT ON` statements of the script, not the pre/post-deploy scripts or any other operation; a comment on an object the database lacks is skipped with a warning, the statement running in a `DO` block that catches "does not exist"; added at the user's request 2026-10-09, with the skip chosen by the user — G-169)
- --exclude-grants / --exclude-role-memberships (`build`/`diff`/`publish`: override pgproj.json's
  excludeGrants/excludeRoleMemberships for one run, in either direction; `init`: skip scaffolding that category
  and write the matching field into the generated pgproj.json — G-87)
- --exclude-owners (the same for object owners and pgproj.json's excludeOwners — G-146, added at the user's decision
  of 2026-10-01)
- --schemas / --exclude-schemas (`init`: a comma-separated list or the option repeated, not both; `--schemas` names the only schemas scaffolded and `public` is not
  assumed; `--exclude-schemas` leaves some out; the generated pgproj.json lists the schemas modeled. `--excludeSchemas` is accepted for `--exclude-schemas` and is not
  documented — G-157, specified by the user 2026-10-02)
- --report (`init`: also write init-report-<yyyy-MM-dd-HH-mm-ss>.md, UTC, at the root of `--output` — G-157)
- --tenant-prefix <prefix>, --tenant-from <identifier>, --tenant-file, --tenant-query <sql> (`init`, G-290d: the schemas named `<prefix>_...` are tenants; the shared schemas are scaffolded
  without them, one tenant (the first by name, or `--tenant-from`) into `<output>/Tenant` with its schema name taken out (a `search_path` setting names the stand-in), the
  other tenants compared with it and those that differ named, the `tenants` block written; `--tenant-file` writes `tenants.csv` beside pgproj.json, `--tenant-query` writes the
  query (not run), not both; the others need `--tenant-prefix`; the report gets a Tenants section — the user's decision 5 on #168, 2026-10-14)
- --erd [path] (`init`, `build`, `diff`, and `publish`; `document` always includes the diagram: also write a Mermaid entity relationship diagram of the project's tables, built from the project's SQL files; a bare .mmd file, plus a companion
  .md for `build`, `diff`, and `publish` (not `init`, whose report links the .mmd); the optional path is relative to the directory of pgproj.json unless absolute, and
  with none the file is erd-<yyyy-MM-dd-HH-mm-ss>.mmd in UTC there; nothing is written when the project has errors — G-158, specified by the user 2026-10-02)
- --erd-html (`init`, `build`, `diff`, and `publish`, only with `--erd`: also write a .html page of the same name that draws the diagram in a browser, loading Mermaid from a CDN; opt-in — G-158, specified by the user 2026-10-02)
- --erd-existing overwrite|backup|keep (`init`, `build`, `diff`, and `publish`, only with `--erd`: what to do with a file already at the path; default backs up a file --erd wrote and keeps any other; G-158, specified by the user 2026-10-02)
- --erd-schemas / --erd-exclude-schemas (`init`, `build`, `diff`, and `publish`, only with `--erd`: a comma-separated list or the option repeated, not both; draw only, or all but, the schemas named; optional — G-158, specified by the user 2026-10-02)
- `document` (a fifth command, G-159, specified by the user 2026-10-02): --project, --source-connection (document a live database instead), --format html,markdown,json (a list; default html and json),
  --output (directory; default docs, relative to the directory of pgproj.json), --existing overwrite|backup|keep (the same policy as --erd-existing: a file `document` wrote is backed up, any other kept),
  --schemas / --exclude-schemas (not both), --no-fail-on-doc-errors, --verbose / --quiet. The documentation is read from COMMENT ON text: description, then `@tag: value` lines (the starter set, custom tags kept);
  a bad tag value is shown and listed, the files are written, and the exit code is 1 unless --no-fail-on-doc-errors.
- (G-160) `COMMENT ON SCHEMA` is modeled: `init` scaffolds a schema's comment (not `public`'s default), `diff` and `publish` compare and apply a comment the project states, and `document` shows it.
- (G-161) `COMMENT ON CONSTRAINT` is modeled on the same rules as a schema's comment: `init` scaffolds it, `diff` and `publish` compare and apply a comment the project states, a recreated constraint keeps its comment, and `document` shows it.
- (G-162) `document` warns about a starter-set tag on a kind of object it does not apply to (classification and retention: schema, table, view, column; pii: table, view, column; example: column, constraint, routine, type); a warning does not change the exit code.
- (G-163) `COMMENT ON TRIGGER` is modeled on the same rules as a constraint's comment: `init` scaffolds it, `diff` and `publish` compare and apply a comment the project states (in place, without dropping the trigger), a recreated trigger keeps its comment, and `document` lists each table's triggers with their comments.
- (G-164) `COMMENT ON POLICY` is modeled on the same rules as a trigger's comment: `init` scaffolds it, `diff` and `publish` compare and apply a comment the project states (in place, without dropping the policy), a recreated policy keeps its comment, and `document` lists each table's row-level security policies with their comments.
- --verbose / --quiet (every command: `--verbose` adds `[verbose]` progress lines on standard error, `--quiet` leaves out progress,
  warnings, and notices and keeps the result and the errors; the two cannot be combined; a result is never filtered; secrets are
  masked in every line — G-143, design agreed by the user 2026-10-01)
- --exit-code (`diff`: exit 2 when there are differences, 0 when there are none, 1 on an error, which wins; warnings are not differences; without it,
  `diff` exits 0 whether or not it finds differences; works with `--format json`; no `pgproj.json` setting — G-282, option (a), the user's decision of 2026-10-14)
- --tenants <identifiers> (`diff` and `publish`, for a project with tenants: compare or publish only these tenants, by identifier, a comma-separated list or the option repeated; each
  must be a tenant the project has; the shared schemas are compared and published either way; an error on a project without tenants; no `pgproj.json` setting — G-290b, G-290c,
  the design agreed with the user 2026-10-14, #168)
- --format (`build`: `text`, the default, prints the header and the diagnostics; `json` writes the engine's document (`BuildResult.ToJson`, G-309f: the envelope `diff --format json` starts with, only its build part: `schemaVersion`, `status`, the project, the documents, the diagnostics with their places, the counts; with `--source-connection` also `source`, and the statuses `refused` and `connectionFailed`, G-318) on standard output, everything else on standard error, whatever the status; exit codes unchanged; the user's sign-off of the G-309 design on #213 is the explicit instruction for the option. `build --source-connection` is `BuildRequest.SourceConnectionString` (G-318), and the engine masks every message it returns, never the deployment SQL (G-318); the CLI's own masking of every line it writes stays.
  `diff`: `text`, the default, lists each change and ends with the counts; `json` writes the engine's result document (`CompareResult.ToJson`, G-309b: `schemaVersion`, `status`, the build part, then the change set and counts; the user's decision 3 on #213, nothing parsed the earlier shape; written whatever the status, a run that did not compare having only its status, message, and build part, G-316) as
  JSON on standard output for CI, everything else on standard error — G-107, user-chosen shape; a changed property is named for the user in both,
  `Default`, `Query`, `Columns changed`, never the model's name — G-285, the user's decision of 2026-10-13, since nothing parsed the JSON yet; a diagnostic with a place gives `file`, `line`, `column`, and, when it has an end, `endLine` and `endColumn`, the end being just past the last character, and its text form is the range `file:line:column-endLine:endColumn:` — G-308, the user's choices of 2026-10-18)

Copilot may:
- improve descriptions
- improve help text
- add examples

Copilot must not:
- change semantics of any option
- remove options
- rename options

---

## 3. Help Text

Copilot may:
- improve help text
- improve examples
- improve usage blocks
- improve error messages

Copilot must not:
- change behavior while improving help text
- introduce new required options
- remove existing examples

How help is laid out (G-168): `HelpLayout` (`src/PgProj.Cli/HelpLayout.cs`) prints `--help` for every command, not System.CommandLine's
own help. When writing help text:
- Give each command a one-line summary with `HelpLayout.SetSummary`; the root's command list shows it, and it heads the command's help.
- Write a command's description one source line per paragraph, list item (`  - ...`), or table row (`  @term    text`): the layout wraps
  each line itself and indents its continuation under the item's text. Do not hard-wrap a paragraph across source lines.
- Keep the examples in a last paragraph that starts with `Examples:`; it is printed after the options and is never wrapped.
- Give an option that takes a value a `HelpName` (`path`, `directory`, `schemas`, `overwrite|backup|keep`) so the help shows what to type.

---

## 4. Error Handling

Copilot may:
- improve clarity of error messages
- improve validation logic
- add missing error cases

Copilot must not:
- change exit codes
- change CLI behavior

---

## 5. Program.cs Behavior

Copilot must preserve:
- root command structure
- command registration
- option binding
- invocation pipeline

Copilot may:
- reorganize code for readability
- extract helpers

What the CLI owns, as the reference shell over the engine (`docs/shells.md`): the commands and their options and help, reading the
connection environment variables, building the engine's requests, printing results (text and `--format json`), writing the files the
engine returns, the placement of files already there (`--erd-existing`, `--existing`), password masking in what it prints, and the exit
codes. What a command does is the engine's (`PgProj.Engine`); a change to it is made there.

---

# End of Copilot CLI Instructions
