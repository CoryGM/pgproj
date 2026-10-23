# Real-world QA sweep

This runs pgproj over the schemas of real projects instead of PostgreSQL's regression SQL (see [`../README.md`](../README.md) for that harness). It was first run on
2026-10-13 and again on 2026-10-15. The sweep finds defects that small, hand-written test schemas do not: a statement nested too deeply, a rule on an
extension's view, a pgvector operator class, a schema too big for the server's lock table.

## The schemas

| Name | What it is | Source (pinned) |
|---|---|---|
| `northwind`, `chinook`, `pagila`, `adventureworks`, `temporal` | Well-known sample and product schemas | `pthom/northwind_psql`, `lerocha/chinook-database`, `devrimgunduz/pagila`, `lorint/AdventureWorks-for-Postgres`, `temporalio/temporal` |
| `synapse`, `synapse54` | The Matrix homeserver's full schema, version 72 (the upgrade target) and version 54 (the upgrade start) | `element-hq/synapse` (72 at a commit; 54 at tag `v1.30.0`) |
| `supabase`, `plausible`, `gitlab` | Large production schemas (GitLab's is 2,700 files and 4,000 indexes once scaffolded) | `supabase/postgres`, `plausible/analytics`, `gitlabhq/gitlabhq` |
| `musicbrainz` | The MusicBrainz database, 730 objects | `metabrainz/musicbrainz-server` `admin/sql`, eight files joined (see `fetch.sh`) |
| `osm` | The OpenStreetMap website's Rails schema (it needs PostGIS, so it runs on that image) | `openstreetmap/openstreetmap-website` |

The files are other projects' work, under their own licences, so they are **not** in this repository. `fetch.sh` downloads them into `tools/diffqa/work/realworld/rw-data`
(not tracked). Each source is pinned to a commit or tag and each file is checked against its SHA-256, so a later run tests the same bytes. Three files are made from the
downloads and loaded in place of them, because the originals do not load into the test database:

- `chinook_fixed.sql`: the script does `DROP/CREATE DATABASE chinook` and `\c chinook`, so it loads into a database of its own and leaves the test database empty.
- `musicbrainz2.sql`: adds the `cube` and `earthdistance` extensions and the collation its installer creates outside SQL, and drops its `BEGIN`/`COMMIT` and schema names.
- `supabase2.sql`: without the `supabase_vault` extension, which the pgvector image lacks.

## Running it

You need Docker, `curl`, Python 3, Git Bash or another POSIX shell, and a built CLI (`dotnet build src/PgProj.Cli`; every command runs through the engine, `PgProj.Engine`, so that is where a defect found here usually
is). Use ports 55490 to 55498, never 55432.

```sh
tools/diffqa/realworld/runall.sh        # fetches, starts the containers, runs everything, removes the containers (KEEP=1 keeps them)
```

Run only one at a time: every run deletes `work/realworld/rw-out`. For one schema on a server you started yourself:

```sh
tools/diffqa/realworld/fetch.sh synapse
tools/diffqa/realworld/rw.sh <name> <file> <container> <port> [written]
```

For each schema `rw.sh` loads the file into a database and prints one line per step:

- **`source objects`:** how many tables, views, and the like the load made. If it is 0, the script loaded into some other database or failed, and the rest of the run proves
  nothing. Read it first.
- **init path:** `init --report`, `build`, `publish --execute` into an empty database, `diff` against the source and the published database, and `pg_dump --schema-only` of both
  compared.
- **written path** (with `written`): the file itself as a project, with `build`, `publish`, `diff`, and the dump compared. Some files fail here for reasons of their own: a
  `SET search_path` between schemas, which pgproj ignores, an extension the image lacks, or an installer step outside the file.

`runall.sh` adds, per schema, a second `publish` (it must propose nothing), `diff --exit-code` (0), and `document` (no errors); GitLab on a server with
`max_locks_per_transaction=256` (the default table refuses it, G-280); and the Synapse v54 to v72 upgrade with `--concurrently` and `--lock-timeout`.

## Reading the result

A clean init path is: build 0 errors, `diff` 0/0/0 both ways, 0 dump differences, second `publish` nothing, `diff --exit-code` 0. A GitLab dump differs in partition index names
(the G-237 limit), and Synapse's upgrade differs from a fresh v72 in one column's position; both are known. Anything else is a candidate gap: confirm it, then open it as a gap
issue. Output is in `work/realworld/rw-out/<name>/` (`init.log`, `build.log`, `publish.log`, `init.diff`, `written.diff`, the scaffolded project, and the docs).
Not yet run: the Synapse upgrade with millions of rows in `events`.
