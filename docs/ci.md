# pgproj in CI/CD

How to run pgproj in a pipeline: check every change, catch drift in a database, and deploy with a review. The examples are GitHub Actions; every step
is a shell command, so they translate directly to GitLab CI, Azure Pipelines, or any other runner.

Three exit codes matter in a pipeline:

| Command | `0` | `1` | `2` |
|---|---|---|---|
| `pgproj build` | the project has no errors | it has errors, or could not be read | |
| `pgproj diff --exit-code` | the database matches the project | an error | the database differs from the project |
| `pgproj publish --execute` | the script ran (or there was nothing to do) | an error, or a refusal; nothing was changed | |

Warnings never fail a step. Connection strings come from `PGPROJ_TARGET_CONNECTION` and `PGPROJ_SOURCE_CONNECTION`, so keep them in your secrets, never on
the command line; pgproj masks passwords in everything it prints.

## Installing pgproj in a job

The release builds are self-contained, so a job needs no .NET. Pin the version, check the download against the release's checksums, and put the folder on
the `PATH`:

```yaml
env:
  PGPROJ_VERSION: 0.1.0

steps:
  - name: Install pgproj
    run: |
      base="https://github.com/CoryGM/pgproj/releases/download/v${PGPROJ_VERSION}"
      archive="pgproj-${PGPROJ_VERSION}-linux-x64.tar.gz"
      curl -fsSLO "$base/$archive"
      curl -fsSLO "$base/SHA256SUMS.txt"
      sha256sum --check --ignore-missing SHA256SUMS.txt
      tar -xzf "$archive"
      echo "$PWD/pgproj-${PGPROJ_VERSION}-linux-x64" >> "$GITHUB_PATH"
  - run: pgproj --version
```

On an Arm64 runner use `linux-arm64`; on a Windows runner download the `win-x64.zip`. GitHub's Ubuntu runners have what pgproj needs (OpenSSL 3,
ICU); a minimal container image needs ICU and OpenSSL 3 installed ([troubleshooting](troubleshooting.md#installing-and-running)).

A job that has .NET 10 already (it ran `actions/setup-dotnet`) can install the .NET tool instead:
`dotnet tool install --global pgproj --version "$PGPROJ_VERSION"`.

## Check every pull request

Build the project, deploy it to an empty PostgreSQL started for the job, and check that the database then matches the project. This catches a syntax
error, a broken reference, a script PostgreSQL refuses, and a definition that does not converge, before the change is merged.

```yaml
name: Database schema

on:
  pull_request:
  push:
    branches: [main]

jobs:
  schema:
    runs-on: ubuntu-latest
    services:
      postgres:
        image: postgres:18              # the major version your databases run
        env:
          POSTGRES_PASSWORD: postgres
        ports: ["5432:5432"]
        options: >-
          --health-cmd pg_isready --health-interval 5s --health-timeout 5s --health-retries 10
    env:
      PGPROJ_VERSION: 0.1.0
      PGPROJ_TARGET_CONNECTION: Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres
    steps:
      - uses: actions/checkout@v4

      - name: Install pgproj
        run: |
          base="https://github.com/CoryGM/pgproj/releases/download/v${PGPROJ_VERSION}"
          archive="pgproj-${PGPROJ_VERSION}-linux-x64.tar.gz"
          curl -fsSLO "$base/$archive"
          curl -fsSLO "$base/SHA256SUMS.txt"
          sha256sum --check --ignore-missing SHA256SUMS.txt
          tar -xzf "$archive"
          echo "$PWD/pgproj-${PGPROJ_VERSION}-linux-x64" >> "$GITHUB_PATH"

      - name: Build
        run: pgproj build

      - name: Deploy to an empty database
        run: pgproj publish --execute

      - name: The database now matches the project
        run: pgproj diff --exit-code
```

The scratch database starts empty, so it needs what the project assumes exists: create the roles that grants and owners name in a `predeploy` script
(or run with `--exclude-grants --exclude-role-memberships --exclude-owners`), and use an image that has the extensions the project declares (the
`postgres` image has the contrib extensions; PostGIS needs `postgis/postgis`). If the project file has `targetConnectionString`, the variable above
takes its place.

## Catch drift in an environment

Run `diff --exit-code` on a schedule against a real database: exit code `2` means someone changed it outside the project. `--format json` keeps a
record of what differed.

```yaml
name: Schema drift

on:
  schedule:
    - cron: "0 6 * * 1-5"   # weekdays, 06:00 UTC
  workflow_dispatch:

jobs:
  drift:
    runs-on: ubuntu-latest
    env:
      PGPROJ_VERSION: 0.1.0
      PGPROJ_TARGET_CONNECTION: ${{ secrets.PRODUCTION_DATABASE }}
    steps:
      - uses: actions/checkout@v4
      - name: Install pgproj
        run: |
          base="https://github.com/CoryGM/pgproj/releases/download/v${PGPROJ_VERSION}"
          archive="pgproj-${PGPROJ_VERSION}-linux-x64.tar.gz"
          curl -fsSLO "$base/$archive"
          curl -fsSLO "$base/SHA256SUMS.txt"
          sha256sum --check --ignore-missing SHA256SUMS.txt
          tar -xzf "$archive"
          echo "$PWD/pgproj-${PGPROJ_VERSION}-linux-x64" >> "$GITHUB_PATH"
      - name: Compare the database with the project
        run: pgproj diff --exit-code --format json > drift.json
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: drift
          path: drift.json
```

`diff` never changes the target. It does compare views, defaults, and other definitions PostgreSQL rewrites by having the server render the project's
versions in a transaction it rolls back, so give the role in the secret the right to create temporary objects; without it, `diff` compares their text
instead, which can report a difference that is only a matter of spelling ([views](notes/views.md)). A database that should match another database rather than the
project (QA promoted to production) uses `--source-connection` too; see [Promoting between environments](notes/promoting-environments.md).

## Deploy with a review

Write the script first, keep it as an artifact for someone to read, and run it only after an approval. A GitHub environment with required reviewers
does the approving.

```yaml
name: Deploy schema

on:
  workflow_dispatch:

jobs:
  plan:
    runs-on: ubuntu-latest
    env:
      PGPROJ_VERSION: 0.1.0
      PGPROJ_TARGET_CONNECTION: ${{ secrets.PRODUCTION_DATABASE }}
    steps:
      - uses: actions/checkout@v4
      - name: Install pgproj
        run: |
          base="https://github.com/CoryGM/pgproj/releases/download/v${PGPROJ_VERSION}"
          archive="pgproj-${PGPROJ_VERSION}-linux-x64.tar.gz"
          curl -fsSLO "$base/$archive"
          curl -fsSLO "$base/SHA256SUMS.txt"
          sha256sum --check --ignore-missing SHA256SUMS.txt
          tar -xzf "$archive"
          echo "$PWD/pgproj-${PGPROJ_VERSION}-linux-x64" >> "$GITHUB_PATH"
      - name: Write the script
        run: pgproj publish --output deploy.sql
      - uses: actions/upload-artifact@v4
        with:
          name: deploy-script
          path: deploy.sql

  deploy:
    needs: plan
    runs-on: ubuntu-latest
    environment: production   # Settings > Environments: add required reviewers
    env:
      PGPROJ_VERSION: 0.1.0
      PGPROJ_TARGET_CONNECTION: ${{ secrets.PRODUCTION_DATABASE }}
    steps:
      - uses: actions/checkout@v4
      - name: Install pgproj
        run: |
          base="https://github.com/CoryGM/pgproj/releases/download/v${PGPROJ_VERSION}"
          archive="pgproj-${PGPROJ_VERSION}-linux-x64.tar.gz"
          curl -fsSLO "$base/$archive"
          curl -fsSLO "$base/SHA256SUMS.txt"
          sha256sum --check --ignore-missing SHA256SUMS.txt
          tar -xzf "$archive"
          echo "$PWD/pgproj-${PGPROJ_VERSION}-linux-x64" >> "$GITHUB_PATH"
      - name: Deploy
        run: pgproj publish --execute --lock-timeout 5s
```

- **What runs is what the database needs at that moment.** `--execute` plans again when it runs, so if the database changed between the two jobs the
  script can differ from the one reviewed. For a busy database, keep the gap short.
- **Data loss needs a decision.** A script that drops a table or a column, or narrows a type, is refused unless you add `--allow-destructive`; add it
  for that one run, after reading the plan, rather than to the workflow for good ([destructive changes](notes/destructive-changes.md)).
- **Large tables:** `--lock-timeout` stops a deploy from queueing behind a long transaction, and `--concurrently` builds new indexes without blocking
  writes ([locks](notes/locks.md)).
- **The role** needs to own what it changes (or be a member of the owning role); statements only a superuser can run need
  `--allow-superuser-statements` and a superuser ([superuser statements](notes/superuser-statements.md)).

---

[README](../README.md) · [Command reference](commands.md) · [Troubleshooting](troubleshooting.md)
