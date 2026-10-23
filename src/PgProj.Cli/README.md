# pgproj

Your PostgreSQL schema as plain SQL files in source control: checked, compared with a live database, and deployed. pgproj does for PostgreSQL what SQL
Server Data Tools (SSDT) does for SQL Server: you write the schema as it should be, and pgproj works out the `CREATE`, `ALTER`, and `DROP` statements that
bring a database there.

```sh
dotnet tool install --global pgproj
pgproj init --source-connection "Host=localhost;Database=shop;Username=postgres"   # a project from a database you have, in this folder
pgproj build                                                                        # check every file
pgproj diff --target-connection "Host=localhost;Database=shop_test;Username=postgres"
pgproj publish --target-connection "Host=localhost;Database=shop_test;Username=postgres" --dry-run
```

- `pgproj build` parses every file with PostgreSQL's own parser and checks the references between objects, reporting problems as `file:line:column`.
- `pgproj diff` lists what differs from a database; `pgproj publish` writes the script, in dependency order, and runs it in one transaction with `--execute`.
- `pgproj init` turns an existing database into a project, one file per object; `pgproj document` writes a data dictionary and diagram.

**Platforms:** this package needs the .NET 10 runtime, on win-x64, win-arm64, linux-x64, linux-arm64 (glibc), or osx-arm64, the platforms its PostgreSQL
parser (libpg_query, PostgreSQL 18's grammar) is built for. PostgreSQL servers 14 to 18. Without .NET, use the self-contained builds, the installers, or the
apt and dnf repositories ([Installing](https://github.com/CoryGM/pgproj#installing)).

[pgproj](https://github.com/CoryGM/pgproj) · [Commands](https://github.com/CoryGM/pgproj/blob/main/docs/commands.md) ·
[Changelog](https://github.com/CoryGM/pgproj/blob/main/CHANGELOG.md) · Apache-2.0
