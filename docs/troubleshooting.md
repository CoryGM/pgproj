# Troubleshooting

What a message means and what to do about it. Each section starts with the message as pgproj prints it. When nothing here fits, run the command again
with `--verbose`, which writes each step it takes to standard error (passwords stay masked), and open an issue with that output and the output of
[`pgproj about`](commands.md#about), run in the same folder with the same options: it shows the version, the environment, the project pgproj found, and
where each connection came from.

- [Installing and running](#installing-and-running)
- [The project file](#the-project-file)
- [Connecting to a database](#connecting-to-a-database)
- [`build` warnings and errors](#build-warnings-and-errors)
- [`publish` refused to run](#publish-refused-to-run)
- [`diff` keeps showing a change](#diff-keeps-showing-a-change)

## Installing and running

### Windows protected your PC

SmartScreen shows this the first time an unsigned program from the internet runs, and sometimes for a signed one it has seen little of. pgproj's
Windows builds are signed from the release after 0.2.0 (publisher Cory Maass); 0.2.0 and earlier are not. Choose **More info**, then **Run anyway**.
The `.msi` asks the same once, when it installs.

### macOS: "pgproj" cannot be opened, or is damaged

macOS quarantines a file downloaded with a browser, and pgproj is not notarized by Apple. Two files in the download are blocked: `pgproj` and the
SQL parser library it loads, `runtimes/osx-arm64/native/libpg_query.dylib`. Clear the flag once, on the whole unpacked folder (`-r` reaches the library):

```sh
xattr -dr com.apple.quarantine pgproj-<version>-osx-arm64
```

If you cleared it only on `pgproj`, `pgproj` starts, but macOS shows the same message for `libpg_query.dylib` when pgproj loads it. Clear the
flag on the library too, from the unpacked folder:

```sh
xattr -d com.apple.quarantine runtimes/osx-arm64/native/libpg_query.dylib
```

pgproj runs on Apple silicon only; there is no Intel (x64) Mac build.

### Unable to load libpg_query

```text
[Error] Unable to load libpg_query: ...
```

pgproj parses SQL with PostgreSQL's own parser, a native library it carries for Windows (x64, Arm64), Linux (x64, Arm64, glibc), and macOS (Apple
silicon). On another platform (an Intel Mac, 32-bit Windows, a musl Linux such as Alpine) the library is not there, and every file reports this error;
the .NET tool (`dotnet tool install --global pgproj`) installs on such a platform, and then reports it too.
Run pgproj on a supported platform, or in a Debian or Ubuntu based container on one that is not. A .NET program that uses the
[`PgProj.Engine` package](library.md#referencing-it) and builds for an unsupported runtime (`-r osx-x64`, `-r linux-musl-x64`) gets error `PGPROJ001`
at build time instead; built for no runtime in particular, it gets this error when it runs on one.

### Couldn't find a valid ICU package installed on the system

.NET needs ICU on Linux. Install it: on Debian and Ubuntu the `libicu` package of your release (`sudo apt install libicu74` on Ubuntu 24.04,
`libicu72` on Debian 12), or `sudo dnf install libicu` on Fedora and RHEL. The `.deb` and `.rpm`
packages install it for you; the `.tar.gz` does not.

### Cannot load library libgssapi_krb5.so.2

```text
Cannot load library libgssapi_krb5.so.2
Error: libgssapi_krb5.so.2: cannot open shared object file: No such file or directory
```

.NET prints this when a connection tries GSSAPI (Kerberos) on a Linux system without the Kerberos library. pgproj does not try GSSAPI where the
library is missing, and the `.deb` and `.rpm` packages install it, so you see this only when the connection string asks for GSSAPI
(`GSS Encryption Mode=Require`). Install the library: `sudo apt install libgssapi-krb5-2` (Debian, Ubuntu) or `sudo dnf install krb5-libs` (Fedora,
RHEL).

## The project file

### project file was not found

```text
Build failed: project file was not found.
Looked for: /home/you/shop/pgproj.json
Use --project <path> to specify a pgproj.json file, or create one based on pgproj.sample.json.
```

pgproj looks for `pgproj.json` in the current folder only; it does not search parent folders. Run the command from the project's folder, or pass
`--project path/to/pgproj.json`. To start a project, write one ([the project file](../README.md#the-project-file)) or scaffold one from a database with
`pgproj init`.

### has a 'connectionString' property, which is no longer read

```text
Build failed: Project file '.../pgproj.json' has a 'connectionString' property, which is no longer read. Use 'targetConnectionString' for the database
diff and publish compare and change, and 'sourceConnectionString' for the database init scaffolded the project from.
```

The property was renamed before the first release. Rename it to `targetConnectionString` (or `sourceConnectionString` if it names the database the
project was scaffolded from); see [Connections](../README.md#connections).

Other problems with the file (invalid JSON, a missing `name`, `version`, or `source`, a folder or script that does not exist) are listed with their
messages in [`pgproj.json.md`](../pgproj.json.md#errors).

## Connecting to a database

### A PostgreSQL target connection string is required

```text
Diff failed: A PostgreSQL target connection string is required. Specify --target-connection, set PGPROJ_TARGET_CONNECTION, or add targetConnectionString to pgproj.json.
```

`diff` and `publish` need to know which database to compare with. Give it in any of the three ways the message names ([Connections](../README.md#connections)).
`init` needs a source instead: `--source-connection` or `PGPROJ_SOURCE_CONNECTION`.

### The connection string is not valid

```text
Diff failed: The connection string is not valid: the value of 'port' is not one it takes: The input string 'abc' was not in a correct format.
```

The connection string is checked before anything connects. It is a list of `key=value` settings separated by semicolons, such as
`Host=localhost;Port=5432;Database=shop;Username=postgres;Password=secret`; the message names the setting that is wrong.

### Failed to connect to 127.0.0.1:5432

Nothing accepted a connection at that host and port: the server is not running, listens on another port, or a firewall is in the way. Check with
`pg_isready -h <host> -p <port>`. A server in Docker needs its port published (`-p 5432:5432`).

### 28P01: password authentication failed for user "postgres"

The server refused the user name or password. The code at the start is PostgreSQL's own (`28P01`); any `NNNNN:` message comes from the server, and
PostgreSQL's documentation lists the codes.

## `build` warnings and errors

Each problem starts with where it is, `file:line:column-endLine:endColumn`, so an editor or terminal can jump to it. `[Error]` stops `diff` and `publish`;
`[Warning]` does not.

### ... is not modeled; the statement is ignored

```text
sql/a.sql:2:1-2:55: [Warning] CREATE SERVER is not modeled; the statement is ignored: CREATE SERVER remote FOREIGN DATA WRAPPER postgres_fdw
```

pgproj does not model that kind of statement, so `publish` will never deploy it. If the database needs it, move it to a `predeploy` or `postdeploy`
script, written so it can run more than once (`CREATE ... IF NOT EXISTS`, or a `DO` block that checks first). The kinds pgproj does not model are listed in
[Statements that are not modeled](notes/unmodeled-statements.md) and [What is supported](notes/supported-objects.md).

### references missing table

```text
sql/a.sql:1:69-1:77: [Warning] Constraint 'public.orders.orders_customer_id_fkey' references missing foreign key table 'public.customer'.
```

The project names something it does not define: a table, a type, a function, or a column. Usually it is a typo, or a file that is missing. When `schemas` is left out of `pgproj.json`, such a
reference is a warning, since it may belong to a schema another project owns; when `schemas` is set, it is an error unless it names a schema outside the
list ([References build cannot resolve](notes/unresolved-references.md)).

### syntax error at or near ...

```text
sql/Schema/app/Table/b.sql:3:30-3:31: [Error] syntax error at or near ","
```

PostgreSQL's own parser refused the statement; the position is the token it stopped at. pgproj reads SQL with PostgreSQL 18's grammar, so a
statement PostgreSQL 18 accepts, pgproj accepts.

## `publish` refused to run

When `publish --execute` refuses, nothing has run. `--dry-run` prints the script so you can see what it would do.

### can discard data and were not executed

```text
Publish failed: 1 operation(s) can discard data and were not executed.
  [Table] public.customer: column "name" removed
Use --output/--dry-run to review the generated script, and pass --allow-destructive to run it anyway.
```

The script drops something that holds data, or narrows a column's type. If that is what you meant, run it again with `--allow-destructive`. If a column
was renamed, not dropped, say so with a `-- @renamed-from: old_name` comment above it, and `publish` renames it instead
([Renaming columns](renaming-columns.md), [Destructive changes](notes/destructive-changes.md)).

### name roles the server does not have

```text
Publish failed: the project's grants and owners name roles the server does not have, so the script was not run:
  role "reporting", named by table:public.orders
pgproj does not create roles (they are cluster-wide): create them on the server first, or create them in the pre-deploy script.
```

Roles belong to the whole server, not to one database, so pgproj does not create them ([Roles](notes/roles.md)). Create them first, or in the `predeploy`
script. A test database that should ignore permissions can be compared with `--exclude-grants --exclude-role-memberships --exclude-owners`.

### that only a superuser can run

```text
Publish failed: the script has 1 statement(s) that only a superuser can run, so it was not run:
  [EventTrigger] event trigger:et: an event trigger is created, changed, or dropped only by a superuser
Use --output/--dry-run to review the generated script, and pass --allow-superuser-statements to run it as a superuser.
```

Run it with `--allow-superuser-statements`, connected as a superuser ([Statements only a superuser can run](notes/superuser-statements.md)).

### uses features the server does not have

```text
Publish failed: the project uses features the server (PostgreSQL 16) does not have, so the script was not run:
  ... (needs PostgreSQL 18)
```

The project uses something only a newer PostgreSQL accepts, and the target server is older. Deploy to a newer server, or remove the feature
([PostgreSQL versions](notes/postgresql-versions.md)).

### more than the server's lock table holds

The script changes so many tables and indexes at once that PostgreSQL would run out of lock space partway and roll back. The message gives the
`max_locks_per_transaction` value that would fit; raise it (it takes a server restart), publish in smaller steps, or pass `--allow-lock-limit` if you know
the server has room ([Locks and large tables](notes/locks.md)).

### The server refused a statement

```text
Publish failed: 42501: must be owner of table orders
  The statement that failed: ALTER TABLE "public"."orders" ADD COLUMN "note" text
  Nothing was applied: the script was rolled back.
```

PostgreSQL refused one statement, so the whole script was rolled back. The message is the server's, with its code; here, the role that connected does
not own the table. Connect as the owner, or as a member of the owning role. pgproj names the statement so you can see which object it was.

## `diff` keeps showing a change

After a successful `publish --execute`, `pgproj diff` against the same database should report nothing. If it keeps reporting the same change:

- **The role cannot create temporary objects.** pgproj compares views, defaults, and other definitions PostgreSQL rewrites by having the server render the
  project's version in a transaction it rolls back. Without that right it compares the text instead, and a difference in spelling shows as a change. Run
  `diff` as a role that owns the objects ([Views, and other definitions PostgreSQL rewrites](notes/views.md)).
- **Something outside the project changes it again**: an extension, a trigger that runs DDL, or another deployment process.
- **Otherwise it is a bug.** Open an issue with the object's SQL from the project and the `diff` output.

---

[README](../README.md) · [Command reference](commands.md) · [CI/CD](ci.md) · [All notes](notes/README.md)
