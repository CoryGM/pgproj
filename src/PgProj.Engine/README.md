# PgProj.Engine

pgproj as a .NET library: the pipeline behind the `pgproj` command line. Build a PostgreSQL schema project from plain SQL files, compare it with a live
database, plan and publish the difference, scaffold a project from a database, and write its documentation. It returns results; it never prints,
writes a file, or exits.

```csharp
using PgProj.Engine;

var result = await new PgProjEngine().BuildAsync(new BuildRequest { ProjectFilePath = "pgproj.json" });

foreach (var diagnostic in result.Diagnostics)
{
    Console.WriteLine($"{diagnostic.File}:{diagnostic.Line}:{diagnostic.Column}: [{diagnostic.Severity}] {diagnostic.Message}");
}
```

- `BuildAsync`, `CompareAsync`, `PublishAsync`, `ScaffoldAsync`, `DocumentAsync`: what `pgproj build`, `diff`, `publish`, `init`, and `document` do.
- `CheckDocumentAsync` and `ParseCache`: an editor's syntax check as the person types, and builds that parse only the files that changed.
- The project's files come from disk, from memory, or from an editor's unsaved buffers (`IProjectFileSource`).

**Platforms:** .NET 10 on win-x64, win-arm64, linux-x64, linux-arm64 (glibc), and osx-arm64, the platforms its PostgreSQL parser (libpg_query, PostgreSQL
18's grammar) is built for; a build for another runtime fails with error PGPROJ001. PostgreSQL servers 14 to 18.

**Before 1.0** the API may change; each change is listed in the [changelog](https://github.com/CoryGM/pgproj/blob/main/CHANGELOG.md).

[Using pgproj as a library](https://github.com/CoryGM/pgproj/blob/main/docs/library.md) · [Adding a front end](https://github.com/CoryGM/pgproj/blob/main/docs/shells.md) ·
[pgproj](https://github.com/CoryGM/pgproj) · Apache-2.0
