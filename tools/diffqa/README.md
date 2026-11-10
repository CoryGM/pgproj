# Differential QA harness

This finds where pgproj and PostgreSQL disagree. It loads PostgreSQL's own regression SQL into a database, then checks two round trips against `pg_dump --schema-only`:

- **init path:** `pgproj init` from the database, `publish` that project into an empty database, and compare the two dumps. A failure here is pgproj failing on its own output.
- **written path:** use the database's `pg_dump` as a pgproj project, `publish` it into an empty database, and compare the two dumps.

Any `build` error, failed `init` or `publish`, or dump difference is a candidate gap. Each still has to be confirmed before it's opened as one. The regression SQL is test data: it's only ever run with `psql` against a throwaway container.

The same idea over real projects' schemas (GitLab, Synapse, MusicBrainz, and others) is [`realworld/`](realworld/README.md).

## Run it

You need Docker, the .NET SDK, `gh`, Python 3, and Git Bash or another POSIX shell. The harness runs the `pgproj` command, and every command runs through
the engine (`PgProj.Engine`), so a defect it finds is in the engine or the projects below it unless only the printed text is wrong.

No server publishes a host port. Each version's server (`diffqa<version>`) and pgproj share a Docker network of their own (`diffqa`): pgproj is a
self-contained `linux-x64` build of this checkout (`work/pgproj-linux`), run in a container (`mcr.microsoft.com/dotnet/runtime-deps:10.0`) with `work` mounted
at `/work`, and reaches the server by its container name. `psql` and `pg_dump` run in the server's container.

1. **Make the network, a server per version under test, and the build** (the build is made once and shared; `setup.sh build` makes it again after a change):

   ```sh
   DIFFQA_VERSION=18 tools/diffqa/setup.sh
   ```

2. **Fetch that version's regression SQL** into `tools/diffqa/regress/<branch>`:

   ```sh
   DIFFQA_VERSION=18 tools/diffqa/fetch.sh
   ```

3. **Run every file, or the ones named:**

   ```sh
   DIFFQA_VERSION=18 tools/diffqa/runall.sh
   DIFFQA_VERSION=18 tools/diffqa/runall.sh create_table rules
   ```

   This writes one directory per file under `tools/diffqa/work/pg18`, and a report at `tools/diffqa/work/pg18/triage.md`. The report has:
   - the results per file;
   - every failure, grouped by message;
   - what each path lost, by kind of statement;
   - the not-modeled warnings, by message.

4. **Remove the server** when done (the network goes with the last one): `DIFFQA_VERSION=18 tools/diffqa/setup.sh down`.

Each run directory holds:
- `input.sql`, `psql.out`;
- the dumps, raw (`a_raw.sql`, `b_raw.sql`, `c_raw.sql`) and normalized (`.norm`);
- `written.diff` and `init.diff`;
- the output of each pgproj command.

Settings, overridable from the environment, are in `config.sh`: version, container, network, password, branch, timeout, the Linux build's folder, and the image it runs in.

## Reading the results

- **The regression files leave noise.** Top-level `DROP` statements are removed so the objects stay. Some files still depend on modules the test build loads (`regress.so`, test extensions), so their statements fail in `psql`, and those objects are simply absent.
- **A kind pgproj reports as not modeled** shows up as both a warning and a dump difference. That's expected; whether such a kind should be modeled is a separate decision.
- **The written path tests how pg_dump spells things.** pg_dump writes many features as `ALTER` statements (identity, serial defaults, attached partitions). The init path tests pgproj's own reading and writing of a database.
