# pgproj

**Your PostgreSQL schema as plain SQL files in source control: checked, compared with a live database, and deployed.** pgproj does for PostgreSQL what
SQL Server Data Tools (SSDT) does for SQL Server: you write the schema as it should be, and pgproj works out the `CREATE`, `ALTER`, and `DROP`
statements that bring a database there.

- **No migration files.** Tables, views, functions, types, grants, and the rest live in ordinary `.sql` files, as you would write them by hand. To change
  the schema, change the files.
- **Checked like code.** `pgproj build` parses every file with PostgreSQL's own parser and checks the references between objects, reporting problems as
  `file:line:column` so your editor jumps to them.
- **A script you can read.** `pgproj diff` lists what differs from a database; `pgproj publish` writes the script, in dependency order, and runs it in one
  transaction when you ask.
- **Careful by default.** A change that would lose data is refused unless you allow it, changes that lock or rewrite a large table are named before
  anything runs, and objects in the way of a change (a view on a retyped column) are dropped and recreated around it.
- **Start from what you have.** `pgproj init` turns an existing database into a project, one file per object; `pgproj document` writes a data dictionary
  and diagram from your comments.

It works with PostgreSQL 14, 15, 16, 17, and 18, runs on Windows, Linux, and macOS, and is also a .NET library, `PgProj.Engine`, for building it into
your own tools. Choosing between pgproj, EF Core migrations, Flyway, Atlas, or pgCodeKeeper? [Why pgproj?](docs/why-pgproj.md) compares them and
says which fits which situation.

## Contents

- [Installing](#installing)
- [A first project in five minutes](#a-first-project-in-five-minutes)
- [What the output looks like](#what-the-output-looks-like)
- [Three ways to use it](#three-ways-to-use-it)
- [Connections](#connections)
- [The project file](#the-project-file)
- [Commands](#commands)
- [Using pgproj as a library](#using-pgproj-as-a-library)
- [Learn more](#learn-more)

## Installing

The builds are self-contained: **no .NET installation is needed** to run them. If you have .NET 10, the .NET tool is the quickest way.

**Debian, Ubuntu, Fedora, RHEL:** install from pgproj's signed package repositories, so updates come with the system's own (amd64/x86_64 and
arm64/aarch64; [all the details](https://corygm.github.io/pgproj/)):

```sh
# Debian, Ubuntu
sudo install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://corygm.github.io/pgproj/pgproj.asc | sudo tee /etc/apt/keyrings/pgproj.asc >/dev/null
echo "deb [signed-by=/etc/apt/keyrings/pgproj.asc] https://corygm.github.io/pgproj/apt stable main" | sudo tee /etc/apt/sources.list.d/pgproj.list
sudo apt update && sudo apt install pgproj

# Fedora, RHEL
sudo curl -fsSL -o /etc/yum.repos.d/pgproj.repo https://corygm.github.io/pgproj/pgproj.repo
sudo dnf install pgproj
```

**With .NET 10** (any of the platforms below): install the [.NET tool](https://www.nuget.org/packages/pgproj) from nuget.org, and update it with
`dotnet tool update --global pgproj`. Unlike the other builds it needs the .NET 10 runtime installed.

```sh
dotnet tool install --global pgproj
```

**Everything else**, and the same packages as files: the [releases page](https://github.com/CoryGM/pgproj/releases).

| Platform | Download | Install |
|---|---|---|
| Windows (x64, Arm64) | `pgproj-<version>-win-x64.msi` | Run the installer; it puts `pgproj` on the PATH. |
| Windows, portable | `pgproj-<version>-win-x64.zip` | Unpack it and run `pgproj.exe` from the folder. |
| Debian, Ubuntu | `pgproj_<version>_amd64.deb` | `sudo apt install ./pgproj_<version>_amd64.deb` |
| Fedora, RHEL, SUSE | `pgproj-<version>-1.x86_64.rpm` | `sudo dnf install ./pgproj-<version>-1.x86_64.rpm` |
| Any Linux | `pgproj-<version>-linux-x64.tar.gz` | Unpack it and run `pgproj` from the folder. |
| macOS (Apple silicon) | `pgproj-<version>-osx-arm64.tar.gz` | Unpack it and run `pgproj` from the folder (see the note below). |

Arm64 files are named the same way (`win-arm64`, `_arm64.deb`, `.aarch64.rpm`, `linux-arm64`). Then check it works:

```sh
pgproj --version
```

- **Windows:** releases after 0.2.0 are signed (publisher Cory Maass); 0.2.0 and earlier are not. SmartScreen may still warn the first time, while
  the signature is new to it; choose **More info**, then **Run anyway**.
- **macOS:** the build is not notarized, so macOS blocks a file downloaded with a browser. After unpacking, clear the flag once:
  `xattr -dr com.apple.quarantine pgproj-<version>-osx-arm64`. Intel Macs are not supported.
- **Linux:** pgproj needs OpenSSL 3 and ICU (`libicu`), and uses Kerberos (`libgssapi-krb5`) when it is there; the `.deb` and `.rpm` packages install
  all three.
- **Checking a download:** every release has a `SHA256SUMS.txt` (`sha256sum --check --ignore-missing SHA256SUMS.txt`), signed in
  `SHA256SUMS.txt.asc` with the [Linux signing key](packaging/linux/pgproj-signing-key.asc) (fingerprint `2154 E140 C291 AF3C 079E  B30A C954 6AE5 6857 FE31`; `gpg --import pgproj-signing-key.asc`, then
  `gpg --verify SHA256SUMS.txt.asc SHA256SUMS.txt`), build provenance (`gh attestation verify <file> --repo CoryGM/pgproj`), and a software bill of
  materials. The `.rpm` packages are signed with the same key; apt checks the `.deb` packages through the signed repository.

To use pgproj from .NET code instead, add the [library](#using-pgproj-as-a-library). To build it from source you need the .NET 10 SDK; see
[`DEVELOPING.md`](DEVELOPING.md).

## A first project in five minutes

You need pgproj and a PostgreSQL server you can create tables in. If you have Docker, this starts a throwaway one:

```sh
docker run --name pgproj-try -e POSTGRES_PASSWORD=secret -p 5433:5432 -d postgres:18
```

**1. Write the project.** A project is a `pgproj.json` file and a folder of SQL files. Make a folder with these three files:

`pgproj.json`

```json
{
  "name": "shop",
  "version": "1.0",
  "source": "./sql"
}
```

`sql/customer.sql`

```sql
CREATE TABLE customer (
    id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email text NOT NULL UNIQUE,
    name  text NOT NULL
);
```

`sql/orders.sql`

```sql
CREATE TABLE orders (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id bigint NOT NULL REFERENCES customer (id),
    placed_at   timestamptz NOT NULL DEFAULT now()
);
```

**2. Check it.** Run the commands in that folder:

```sh
pgproj build
```

```text
Loaded project: shop
Source path: /home/you/shop/sql
SQL files: 2
No diagnostics.
```

**3. Point it at the database.** The target is the database pgproj compares with and changes. Set it once for the session (or pass
`--target-connection` to each command):

```sh
export PGPROJ_TARGET_CONNECTION="Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=secret"   # PowerShell: $env:PGPROJ_TARGET_CONNECTION = "..."
```

**4. See what would change, read the script, and run it:**

```sh
pgproj diff
pgproj publish --dry-run
pgproj publish --execute
```

`diff` lists the two tables, their columns, keys, and identity sequences as added; `publish --dry-run` prints the `CREATE TABLE` and
`ALTER TABLE ... ADD CONSTRAINT` statements; `publish --execute` runs them in one transaction. Run `pgproj diff` again and it finds nothing to do.

**5. Change the schema.** Add a column and an index to `sql/customer.sql`:

```sql
CREATE TABLE customer (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      text NOT NULL UNIQUE,
    name       text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX customer_name_idx ON customer (name);
```

```sh
pgproj publish --dry-run --quiet
```

```sql
ALTER TABLE "public"."customer" ADD COLUMN "created_at" timestamptz NOT NULL DEFAULT now();

CREATE INDEX "customer_name_idx" ON "public"."customer" USING btree ("name");
```

pgproj wrote the `ALTER` for you. `pgproj publish --execute` applies it. If you now delete the `name` column from the file, `publish --execute` refuses,
because the column's data would be lost:

```text
Publish failed: 1 operation(s) can discard data and were not executed.
  [Table] public.customer: column "name" removed
Use --output/--dry-run to review the generated script, and pass --allow-destructive to run it anyway.
```

That is the whole loop: change the files, `build`, `diff`, `publish`. Commit the project to source control, and the same files deploy to every
environment; only the target changes. When you are done, `docker rm -f pgproj-try` removes the test server.

The [samples](samples/README.md) are two complete projects to explore: an online shop over seven schemas, and a database with a schema per tenant.

## What the output looks like

- **`diff` and `publish`:** the [first project](#a-first-project-in-five-minutes) above shows both, as pgproj prints them.
- **`document`:** the data dictionary of the schema-per-tenant sample, written from its `COMMENT ON` text, as
  [Markdown](samples/SchemaPerTenant/docs/database-documentation.md) (GitHub renders it, diagram included) and as a single
  [HTML page](samples/SchemaPerTenant/docs/database-documentation.html) (download it and open it in a browser).
- **`--erd`:** the [entity relationship diagram](samples/MultiSchema/docs/erd.md) of the online shop's 25 tables, drawn from its SQL files.

## Three ways to use it

| You have | You want | Use |
|---|---|---|
| SQL files in source control | each environment to match them | [A project in source control](#1-a-project-in-source-control): `build`, `diff`, `publish` |
| a database and no files | to put its schema under source control | [Start from a database](#2-start-from-a-database-you-already-have): `init`, then as above |
| one database that is right (dev, QA) | another to match it | [Promote one database to another](#3-promote-one-database-to-another): `diff` and `publish` with a source |

The first is the normal workflow. The other two are ways in to it, and an edge case beside it.

### 1. A project in source control

The [first project](#a-first-project-in-five-minutes) above is this workflow. In a pipeline, check the project, review the script, and run it:

```sh
pgproj build
pgproj diff    --target-connection "Host=db;Database=shop_test;Username=deploy;Password=..."
pgproj publish --target-connection "Host=db;Database=shop_test;Username=deploy;Password=..." --dry-run
pgproj publish --target-connection "Host=db;Database=shop_test;Username=deploy;Password=..." --execute
```

A second `publish` with no changes proposes nothing. `diff --exit-code` exits `2` when the database differs from the project, for a drift check in CI.

### 2. Start from a database you already have

```sh
pgproj init  --source-connection "Host=localhost;Database=shop;Username=postgres;Password=secret"
pgproj build
```

`init` reads the database (the **source**) and writes `pgproj.json` and one SQL file per object, in folders by schema and kind. It records where the
project came from, as `sourceConnectionString`, and **never** names a database to deploy to: scaffolding production does not make production the target.
From here it is workflow 1; name a target for `diff` and `publish`. `init` lists the few kinds of object it cannot read
([command reference](docs/commands.md#init)).

### 3. Promote one database to another

When a database is the reference for another, use it as the desired state, in place of SQL files:

```sh
pgproj diff    --source-connection "Host=qa;Database=shop;..." --target-connection "Host=prod;Database=shop;..." --exit-code
pgproj publish --source-connection "Host=qa;Database=shop;..." --target-connection "Host=prod;Database=shop;..." --dry-run
```

The first line of output says which way it goes (`Source qa -> Target prod`), only the target is changed, and a database cannot be its own source. The full
guide, with a dev, QA, and production walk-through and a CI parity check, is [Promoting between environments](docs/notes/promoting-environments.md).

## Connections

A connection has a **direction**. The **source** is a database a command reads; the **target** is the database a command compares with and changes.

| | Commands | Option | Environment variable | `pgproj.json` |
|---|---|---|---|---|
| **Source** | `init` (required), `document`, and `build`, `diff`, `publish` in [source mode](#3-promote-one-database-to-another) | `--source-connection` | `PGPROJ_SOURCE_CONNECTION` | `sourceConnectionString` |
| **Target** | `diff`, `publish` (required) | `--target-connection` | `PGPROJ_TARGET_CONNECTION` | `targetConnectionString` |

- **Where a value comes from:** the option, then the environment variable, then `pgproj.json`; the first that is not blank wins. In CI, set the variables
  and keep passwords off the command line. Passwords are masked (`Password=*****`) in everything pgproj prints.
- **`init`** takes the source from the option or the variable, and writes it into the new `pgproj.json` as `sourceConnectionString`, with the password
  replaced by `<password>`. That property is a record of where the project came from; only `document --source-connection` with no value reads it.
- **`targetConnectionString`** is written by you, never by a tool.
- **A source is read only when `--source-connection` is on the command line** (`init` always reads one). The variable and the file supply its value; they
  never switch `build`, `diff`, `publish`, or `document` away from the project's SQL files. `build` never has a target.

## The project file

`init` writes `pgproj.json` for you; [`pgproj.sample.json`](pgproj.sample.json) is a starting point if you write your own. A fuller example:

```json
{
  "name": "shop",
  "version": "1.0",
  "source": "./sql",
  "schemas": ["public"],
  "predeploy": "./scripts/pre.sql",
  "postdeploy": "./scripts/post.sql"
}
```

| Property | | What it is |
|---|---|---|
| `name` | required | The project's name. |
| `version` | required | The project's version. |
| `source` | required | The folder of SQL files. Every `*.sql` file under it is read, in any subfolder, except the `Scripts` folder directly under it. |
| `schemas` | optional | The schemas the project covers. When set, `diff` and `publish` read and compare only these (and any schema the project's SQL uses), so other projects can share the database ([details](docs/notes/schema-scoping.md)). When empty or left out, the whole database is compared. |
| `targetConnectionString` | optional | The database `diff` and `publish` compare with and change, when neither `--target-connection` nor `PGPROJ_TARGET_CONNECTION` is given ([Connections](#connections)). |
| `sourceConnectionString` | optional | The database `init` scaffolded the project from, with the password replaced by `<password>`. It is never a target ([Connections](#connections)). |
| `predeploy` | optional | A SQL script run before the changes, for data and anything pgproj does not model. A line `:r <path>` includes another file. |
| `postdeploy` | optional | A SQL script run after the changes, with the same `:r` includes. Keep the files it includes in the `Scripts` folder under `source`, which is never read as schema. A file that starts with `-- pgproj: run when=changed` runs only when there are changes ([details](pgproj.json.md#running-a-file-only-when-something-changed)). |
| `excludeGrants` | optional | `true` to leave object, schema, and column privileges out of the project and the comparison (default `false`). |
| `excludeRoleMemberships` | optional | `true` to leave role memberships (`GRANT role TO grantee`) out (default `false`). |
| `excludeOwners` | optional | `true` to leave owners (`ALTER ... OWNER TO`) out; the database's owners are then not read either (default `false`). |
| `searchPath` | optional | The schemas an unqualified name is looked up in, in order, like PostgreSQL's `search_path` (default `["public"]`). |
| `tenants` | optional | For a database with a schema per tenant: a template every tenant's schema holds, the prefix of the tenants' schema names, where the list of tenants comes from, and scripts run for each tenant ([Schema-per-tenant databases](docs/notes/tenants.md), and the [`SchemaPerTenant` sample](samples/SchemaPerTenant/README.md)). |

Paths are relative to the folder `pgproj.json` is in. [`pgproj.json.md`](pgproj.json.md) describes every property in full.

## Commands

| Command | What it does |
|---|---|
| [`pgproj init`](docs/commands.md#init) | Scaffolds a new project from a live database: `pgproj.json` and one SQL file per object. |
| [`pgproj build`](docs/commands.md#build) | Parses and checks the project, and reports each problem with its file, line, and column. |
| [`pgproj diff`](docs/commands.md#diff) | Compares the project with a live database and lists what is added, removed, and changed. |
| [`pgproj publish`](docs/commands.md#publish) | Writes the script that brings a database up to date, and runs it in one transaction with `--execute`. |
| [`pgproj document`](docs/commands.md#document) | Writes a data dictionary and a diagram of the project or a database, as HTML, Markdown, and JSON. |
| [`pgproj about`](docs/commands.md#about) | Prints pgproj's version and environment, the project it finds, and its databases: paste it into a bug report. |

`pgproj --help` lists them, and `pgproj <command> --help` shows a command's options with examples. The [command reference](docs/commands.md) has every
option in full, including `--verbose` and `--quiet`, `--format json` for editors and CI, and `--erd`, which draws the project's tables as a diagram.

## Using pgproj as a library

Every command runs through `PgProj.Engine`, a .NET library, and your code can call it the same way:

```sh
dotnet add package PgProj.Engine
```

```csharp
using PgProj.Engine;

var result = await new PgProjEngine().BuildAsync(new BuildRequest { ProjectFilePath = "pgproj.json" });
foreach (var diagnostic in result.Diagnostics)
{
    Console.WriteLine($"{diagnostic.File}:{diagnostic.Line}:{diagnostic.Column}: [{diagnostic.Severity}] {diagnostic.Message}");
}
```

`BuildAsync`, `CompareAsync`, `PublishAsync`, `ScaffoldAsync`, and `DocumentAsync` do what `build`, `diff`, `publish`, `init`, and `document` do, and return
the results as objects (statuses, diagnostics with their ranges, change sets, typed warnings and refusals, the script) instead of printing them. The
project's files can come from disk, from memory, or from an editor's unsaved buffers. The package needs .NET 10 and runs on win-x64, win-arm64,
linux-x64, linux-arm64, and osx-arm64; it is released with the command line, with the same version.

- [Using pgproj as a library](docs/library.md): referencing it, the calls, their results, the JSON document, and what is promised.
- [Adding a shell](docs/shells.md): for putting a new front end (an editor extension, a language server, a web API) over the engine.
- A program that is not .NET can run `pgproj build --format json` and `pgproj diff --format json`, which print the engine's results as JSON.

## Learn more

- [Why pgproj?](docs/why-pgproj.md): migration-based and state-based tools, pgproj side by side with EF Core migrations, Flyway, Atlas, and pgCodeKeeper, and which fits which situation.
- [Command reference](docs/commands.md): every command and option.
- [CI/CD](docs/ci.md): checking every pull request, catching drift, and deploying with a review, with GitHub Actions workflows to copy.
- [Troubleshooting](docs/troubleshooting.md): what a message means and what to do about it.
- [The project file](pgproj.json.md): every property of `pgproj.json`, and how each command reads it.
- [Notes](docs/notes/README.md): how pgproj treats each part of a schema (what it models, how it compares and deploys it, and its limits), from
  [supported objects](docs/notes/supported-objects.md) and [PostgreSQL versions](docs/notes/postgresql-versions.md) to
  [destructive changes](docs/notes/destructive-changes.md), [locks on large tables](docs/notes/locks.md), and
  [renaming a column](docs/renaming-columns.md).
- [Documenting a database](documentation.md): `pgproj document` and the `@tag` lines in comments.
- [Samples](samples/README.md): complete projects to build and publish.
- [Changelog](CHANGELOG.md): what each release contains.

## Contributing and license

pgproj is licensed under the [Apache License, Version 2.0](LICENSE); see also [`NOTICE`](NOTICE) and
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt). pgproj is not accepting contributions yet: pull requests from outside the project are
closed without review. [`CONTRIBUTING.md`](CONTRIBUTING.md) and [`DEVELOPING.md`](DEVELOPING.md) describe how contributions will work once they
are, including the [Contributor License Agreement](CLA.md) every contributor signs.

Found a bug? Open an [issue](https://github.com/CoryGM/pgproj/issues/new/choose) with the output of `pgproj about`. A question or an idea goes in
[Discussions](https://github.com/CoryGM/pgproj/discussions). A security problem is reported privately: see [`SECURITY.md`](SECURITY.md).
