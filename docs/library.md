# Using pgproj as a library

Everything `pgproj` does is in a .NET library, `PgProj.Engine`. The command line is one program that calls it; an editor extension, a language server, or a web
service can call it the same way and get the same results, as objects, without parsing printed text. This page says how.

The engine never writes to the console or to a file, never parses a command line, and returns no exit code. It reads a project, runs the pipeline, and returns a
result: what `build`, `diff`, `publish`, `init`, and `document` would print or write is data in that result, and the caller decides what to show and where to put
it. Problems a user can cause (a missing project, a project with errors, a refused publish, a database that cannot be reached) are a `Status` on the result,
never an exception; an exception means a defect in pgproj, or a cancelled run.

To put a new front end over the engine (an editor extension, a language server, a web API), read [Adding a shell](shells.md) after this page.

Every C# example on this page is compiled and run by a test (`LibraryExamplesTests` in `src/PgProj.Engine.Tests`), which fails if the page and the code part
ways.

## Referencing it

The engine is the NuGet package `PgProj.Engine` on nuget.org, published with every release from the first one, with the same version as the command line. It needs .NET 10:

```sh
dotnet add package PgProj.Engine
```

The package holds the engine and the assemblies it is built on (Core, Parser, Semantics, Dependencies, Diff, Deploy; only the engine's API is promised, see
[What is promised](#what-is-promised)), and PostgreSQL's parser, `libpg_query`, for the platforms it is built for: **win-x64, win-arm64, linux-x64,
linux-arm64 (glibc), and osx-arm64**. A build for no runtime in particular carries all five and runs on any of them; a `dotnet publish -r <rid>` keeps that
runtime's. A build or publish for any other runtime (an Intel Mac, 32-bit Windows, a musl Linux such as Alpine) fails with error `PGPROJ001`, rather than when
the program first parses. Its symbols are on nuget.org's symbol server, with Source Link to this repository.

To use code that is not released yet, clone the repository and add a project reference instead:

```xml
<ItemGroup>
  <ProjectReference Include="..\pgproj\src\PgProj.Engine\PgProj.Engine.csproj" />
</ItemGroup>
```

The engine's references to its own projects are private (they are packed inside its package), so reference the ones whose types your code names, such as
`src/PgProj.Core/PgProj.Core.csproj` for the file sources, beside it.

The examples use these namespaces:

```text
using PgProj.Core;     // the file sources, ProjectSettings
using PgProj.Engine;   // PgProjEngine, the requests and the results
```

## Building a project

`BuildAsync` is what `pgproj build` does: read the project, parse every SQL file with PostgreSQL's own parser, build the model, and report what is wrong.

```csharp
var engine = new PgProjEngine();
var result = await engine.BuildAsync(new BuildRequest { ProjectFilePath = projectFile }, cancellationToken);

if (result.Status is BuildStatus.ProjectNotFound or BuildStatus.ProjectInvalid)
{
    Console.WriteLine(result.Message);
}

foreach (var diagnostic in result.Diagnostics)
{
    Console.WriteLine($"{diagnostic.File}:{diagnostic.Line}:{diagnostic.Column}-{diagnostic.EndLine}:{diagnostic.EndColumn}: [{diagnostic.Severity}] {diagnostic.Message}");
}
```

`PgProjEngine` holds no state between calls and may be called from several threads at once; one instance can serve a whole program. A call parses the
project's files in parallel, up to one per processor, each on a thread of its own; the results are in the files' order, as a parse of one file after another
gives them.

A `BuildResult` has the `Status` (`Succeeded`, `Failed` when the model has errors, `ProjectNotFound`, `ProjectInvalid`, and for a database, below, `Refused`
and `ConnectionFailed`), a `Message` when the project could not be read, the project's name and source folder, the `Documents` read (each path with its version,
below), the `Diagnostics`, the counts by severity, and the `Model` (the semantic model, in memory; it is large and not a promised shape, so it is never in the
JSON).

**A diagnostic's place.** `File` is a full path. `Line` and `Column` are where the problem starts and `EndLine` and `EndColumn` where it ends, all 1-based and
counted in characters; the end is just past the last character, as an editor's range has it, so a one-character problem at 3:30 ends at 3:31. Any of them can
be null: a problem about the whole project (how many objects state no owner) has no place, and one with a start but no known end has no end. A problem found once
the project is read (a missing table, a duplicate, a cycle) is placed at the name or statement it is about.

## Where the files come from

The engine reads a project's files only through `IProjectFileSource`, so the caller decides where they are. Three sources come with it, all in `PgProj.Core`.
Paths are always full paths in the source's own space.

**From disk** (`DiskFileSource`, the default): what the command line uses. Nothing to set up; the `BuildRequest` above read from disk.

**From memory** (`InMemoryFileSource`): for a web service that receives a project in a request, or a test. The paths are whatever rooted paths you choose.

```csharp
var files = new InMemoryFileSource()
    .Set("/project/pgproj.json", """{ "name": "shop", "version": "1.0", "source": "./sql" }""")
    .Set("/project/sql/customer.sql", "CREATE TABLE customer (id bigint PRIMARY KEY, email text NOT NULL);");

var fromMemory = await new PgProjEngine().BuildAsync(new BuildRequest { Files = files, ProjectFilePath = "/project/pgproj.json" }, cancellationToken);
```

A caller with no `pgproj.json` can pass its properties as `ProjectSettings`, with the folder its relative paths are resolved against. They are checked exactly
as the file is, with the same messages:

```csharp
var settings = new ProjectSettings { Name = "shop", Version = "1.0", Source = "./sql" };
var fromSettings = await new PgProjEngine().BuildAsync(new BuildRequest { Files = files, Settings = settings, RootDirectory = "/project" }, cancellationToken);
```

**Unsaved buffers over disk** (`OverlayFileSource`): for an editor. Open documents are laid over the files on disk; a buffer replaces the file of its path, and a
buffer for a file not saved yet is found too. Give each buffer the editor's version, and the result says which version it read, so a diagnostic can be matched to
the state of the buffer it describes.

```csharp
var buffers = new OverlayFileSource(DiskFileSource.Instance);
buffers.Set(openDocumentPath, unsavedText, version: 7);

var fromBuffers = await new PgProjEngine().BuildAsync(new BuildRequest { Files = buffers, ProjectFilePath = projectFile }, cancellationToken);
var document = fromBuffers.Documents.Single(d => d.Path == Path.GetFullPath(openDocumentPath));
Console.WriteLine($"Built version {document.Version} of {document.Path}");
```

`Remove` takes a buffer away when the editor closes it, and the file on disk is seen again. A run reads every file it needs once, at its start, so a buffer
changed while it runs does not mix two states into one result.

**Building again after an edit** (`ParseCache`): keep one per open project and pass it on every build of that project, and a file whose text has not changed
since the last build is not parsed again; on a project of thousands of files, a build after an edit to one of them parses that one. The result is what a build
without the cache gives. A file is parsed again when its text changes, and every file when the first schema of `searchPath` changes (the schema an unqualified
name is created in). A build leaves the cache holding its own files, so a file that is gone is not kept; `Clear` empties it, and `Count` says how many parses it
holds. It is safe from several threads. The semantic checks still run over the whole project on every build.

```csharp
var parseCache = new ParseCache();
var request = new BuildRequest { Files = buffers, ProjectFilePath = projectFile, ParseCache = parseCache };

var first = await new PgProjEngine().BuildAsync(request, cancellationToken);
buffers.Set(openDocumentPath, editedText, version: 8);
var afterTheEdit = await new PgProjEngine().BuildAsync(request, cancellationToken);
Console.WriteLine($"{parseCache.Count} files' parses held");
```

**Checking one document as it is typed** (`CheckDocumentAsync`): the diagnostics of one file without a whole build, in milliseconds on a project of any size,
since only `pgproj.json` and the deploy scripts are read. A schema or template file gets the parser's diagnostics a build gives for it (syntax errors with
their ranges, statements and clauses pgproj does not model, a statement too deep, a near-miss `@renamed-from`), parsed as the build parses it; a deploy script
or a file it includes gets the build's check of it. A missing table, a duplicate, or a cycle needs the whole project and comes only from `BuildAsync`, so keep
the two sets apart and replace each when its own kind arrives. `Kind` says what the file is to the project (`Schema`, `TenantTemplate`, `DeployScript`), and a
file the project does not read is `NotInProject`. With the project's `ParseCache` the check's parse is held, and the build that follows does not parse that
text again. The include list of a deploy script is the one on disk (or in the file source): a `:r` line added in the buffer is followed by the next build.

```csharp
var check = await new PgProjEngine().CheckDocumentAsync(
    new DocumentCheckRequest { Files = buffers, ProjectFilePath = projectFile, DocumentPath = openDocumentPath, Text = typedText, Version = 9, ParseCache = parseCache },
    cancellationToken);
if (check.Status == DocumentCheckStatus.Succeeded)
{
    Console.WriteLine($"{check.Kind} version {check.Version}: {check.Diagnostics.Count} syntax diagnostics");
}
```

**From a database** (`BuildRequest.SourceConnectionString`): what `pgproj build --source-connection` does. The database is read in a read-only transaction and
scaffolded in memory, as `pgproj init` would write it, and that is built in place of the project's files, so the model and the diagnostics describe the database
and say whether what `init` writes builds. Nothing is written. The files are named `source:/...`, and `Source` says which database was read. A project, when the
request names one, still supplies `schemas`, `searchPath`, and the `exclude*` settings; it is optional, and a project with tenants is `Refused`, as is a malformed
connection string. A server that cannot be reached is `ConnectionFailed`. A build never connects to a target.

```csharp
var fromDatabase = await new PgProjEngine().BuildAsync(new BuildRequest { SourceConnectionString = connectionString }, cancellationToken);

if (fromDatabase.Status is BuildStatus.Refused or BuildStatus.ConnectionFailed)
{
    Console.WriteLine($"{fromDatabase.Status}: {fromDatabase.Message}");
}
else
{
    Console.WriteLine($"{fromDatabase.Source}: {fromDatabase.Documents.Count} files, {fromDatabase.ErrorCount} errors");
}
```

## Comparing with a database

`CompareAsync` is what `pgproj diff` does. The request names the target's connection string as you resolved it (the command line takes `--target-connection`,
then `PGPROJ_TARGET_CONNECTION`); when it is null, the project's `targetConnectionString` is used.

```csharp
var comparison = await new PgProjEngine().CompareAsync(
    new CompareRequest { ProjectFilePath = projectFile, TargetConnectionString = connectionString },
    cancellationToken);

if (comparison.Status == CompareStatus.Succeeded)
{
    Console.WriteLine($"{comparison.AddedCount} added, {comparison.RemovedCount} removed, {comparison.ModifiedCount} modified");
    foreach (var change in comparison.Changes.Modified)
    {
        Console.WriteLine($"~ {change.NodeId}");
    }
}
else
{
    Console.WriteLine($"{comparison.Status}: {comparison.Message}");
}
```

A `CompareResult` has the build part (as a `BuildResult` has it), then the change set (`Changes`: `Added`, `Removed`, `Modified`, each change with the
properties that differ), the counts, the notes about PL/pgSQL bodies, the schemas a publish would leave behind, and for a project with tenants the tenants'
side (`Tenants`). Its statuses add `Refused` (no target, a malformed connection string, the source being the target, ...) and `ConnectionFailed`.
`SourceConnectionString` makes a live database the desired state in place of the project's files, as `--source-connection` does.

`AfterBuild` is a hook called once the project is built and before any database is touched, with the build part: the command line prints its header and
diagnostics there, so they appear before a slow live read.

## Publishing

`PublishAsync` is what `pgproj publish` does, in one of two modes. `PublishMode.Plan` (the default) builds, compares, writes the script, and runs every check,
and changes nothing: what `--dry-run` and `--output` do. `PublishMode.Execute` does the same and then runs the script, as `--execute` does.

```csharp
var engine = new PgProjEngine();
var plan = await engine.PublishAsync(
    new PublishRequest { ProjectFilePath = projectFile, TargetConnectionString = connectionString, Mode = PublishMode.Plan },
    cancellationToken);

if (plan.Status == PublishStatus.Planned)
{
    Console.WriteLine(plan.Script);
    foreach (var warning in plan.Warnings)
    {
        Console.WriteLine($"{warning.Kind}: {warning.Text}");
    }
}

// Later, once someone has read the script:
var run = await engine.PublishAsync(
    new PublishRequest { ProjectFilePath = projectFile, TargetConnectionString = connectionString, Mode = PublishMode.Execute },
    cancellationToken);

if (run.Status != PublishStatus.Published)
{
    Console.WriteLine($"{run.Status}: {run.Message}");
    foreach (var refusal in run.Refusals)
    {
        Console.WriteLine(refusal.Text);
    }
}
```

**`Execute` plans again.** It does not run the script an earlier `Plan` returned: it compares the database again on its own connection and runs what it planned
then. A script is never run against a database that changed after it was shown; if the database changed, what runs is what the project needs now, which can
differ from what was shown.

**Consents.** What the command line asks an option for, the request asks a property for, and without it `Execute` refuses before anything runs:
`AllowDestructive` (a change that can lose data or access), `AllowSuperuserStatements`, `AllowLockLimit`. `CommentsOnly`, `Concurrently`, `LockTimeout`, and
`Tenants` are the other options of `publish`.

A `PublishResult` has the compare part (`Compare`), one `PublishPart` per transaction (the whole project, or the shared schemas and each tenant), the `Script` as
`publish` prints it, the `Warnings` and `Refusals` (each a `PublishNotice` with a `Kind`, such as `Destructive`, `Superuser`, `LockRewrite`, or
`MissingRole`, the objects it names as `Items`, and the `Text` the command line prints), and after `Execute`, `Execution`: the parts published, the part that
failed, the failed statement with PostgreSQL's code, message, detail, and hint, and what was not started. Its statuses are `Planned`, `Published`, `Failed`,
`ProjectNotFound`, `ProjectInvalid`, `Refused`, `ConnectionFailed`, and `ExecutionFailed`. `AfterPlan` is a hook called once the plan is made and before anything
runs: the command line prints the script and the warnings there.

## Scaffolding and documenting

`ScaffoldAsync` is what `pgproj init` does, and `DocumentAsync` what `pgproj document` does. Both return the files' text and write nothing.

```csharp
var scaffold = await new PgProjEngine().ScaffoldAsync(
    new ScaffoldRequest { SourceConnectionString = connectionString, ProjectFilePath = "/new/pgproj.json", Files = new InMemoryFileSource() },
    cancellationToken);

if (scaffold.Status == InitStatus.Succeeded)
{
    Console.WriteLine(scaffold.ProjectFileText);
    foreach (var file in scaffold.Files)
    {
        Console.WriteLine($"{file.RelativePath}: {file.Content.Length} characters");
    }
}
```

An `InitResult` has the SQL files (paths relative to `OutputDirectory`), the `pgproj.json` text, the tenant file and the report when asked for, the diagram,
and what could not be read. `AfterScaffold` is a hook called with the result before the report is made: the command line writes the files there and returns
where the diagram went, so the report can link to it. `Files` is where the new project is read back from once it is scaffolded (the new files are laid over it
in memory) and says whether file names differ by case.

```csharp
var documentation = await new PgProjEngine().DocumentAsync(
    new DocumentRequest { ProjectFilePath = projectFile, Formats = [DocFormat.Markdown, DocFormat.Json] },
    cancellationToken);

foreach (var file in documentation.Files)
{
    Console.WriteLine($"{file.FileName}: {file.Text.Length} characters");
}

foreach (var problem in documentation.Problems)
{
    Console.WriteLine($"{problem.Severity} on {problem.Object}: {problem.Message}");
}
```

## Describing the environment

`AboutAsync` is what `pgproj about` prints: the engine's version, the operating system and runtime, whether PostgreSQL's parser loads, the project as it is
read (not built), and the target and source databases with where each connection string came from, the server's version, and the role's privileges. Nothing
it cannot read is an error: a missing project or a database it cannot reach within `ConnectTimeout` is in the result. It changes nothing, and no password
is in the result. A shell that shows a support or diagnostics page uses it.

```csharp
var about = await new PgProjEngine().AboutAsync(
    new AboutRequest { ProjectFilePath = projectFile, TargetConnectionString = connectionString, TargetConnectionOrigin = "settings" },
    cancellationToken);

Console.WriteLine($"pgproj {about.EngineVersion} on {about.OperatingSystem} ({about.Runtime}); parser loads: {about.ParserLoaded}");
if (about.Target is { } target)
{
    Console.WriteLine(target.Status == AboutConnectionStatus.Connected
        ? $"{target.Host}: PostgreSQL {target.ServerVersion}, superuser: {target.IsSuperuser}"
        : $"{target.Host}: {target.Message}");
}
```

`Connect = false` describes each connection string without connecting. A connection the request leaves null is taken from the project's `pgproj.json`, and
its `Origin` says so.

## Progress and cancellation

Every request takes an `IProgress<EngineEvent>`. The engine reports as it goes: the project read, each file, the model built, each database connected to and
read, each step of a publish. An event has a `Kind`, a `Level` (`Detail`, `Information`, `Warning`), and usually a `Message` in the words the command line
prints (its `[verbose]` lines are these events). It is called on the thread that runs the request, in order; do not block it for long.

Every call takes a `CancellationToken`. It is checked between files while parsing and passed to every database call; a cancelled run ends with an
`OperationCanceledException` (a cancelled task), not a status.

**Passwords are masked by the engine.** Every message a result holds or an event carries is masked before it reaches you: a status's `Message`, a diagnostic's
`Message`, an event's `Message`, a `PublishNotice`'s `Text` and its items, a failed statement's report, and what `init` and `document` say. A password in
`password=...`, `PASSWORD '...'`, or `scheme://user:password@host` becomes `*****`, as the command line has always shown it. It is pattern matching on those
shapes, not a proof that no secret remains. **The deployment SQL is never masked** (`PublishResult.Script`, each part's script and steps, what `Execute` runs):
a build may put a secret into the SQL on purpose for that deployment, and masking it would change what is deployed. Show the script with that in mind.

```csharp
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var progress = new Progress<EngineEvent>(e => Console.WriteLine($"[{e.Level}] {e.Kind} {e.Message ?? e.Path}"));

var withProgress = await new PgProjEngine().BuildAsync(new BuildRequest { ProjectFilePath = projectFile, Progress = progress }, cancellation.Token);
```

## The JSON document

`BuildResult.ToJson()` and `CompareResult.ToJson()` write a result as the engine's JSON document: the document `pgproj build --format json` and
`pgproj diff --format json` print, so a program in any language can read the same thing through the command line.

```csharp
var json = result.ToJson();
var forDisplay = result.ToJson(path => Path.GetRelativePath(Environment.CurrentDirectory, path));
```

Every command has the same envelope: `schemaVersion`, `status` (camelCase: `succeeded`, `projectNotFound`, ...), `message` when there is one, then the build part
(`project` with its `name`, `file`, and `sourcePath`, `documents` with their `path` and `version`, `templateDocuments` for a project with tenants, `diagnostics`
with `severity`, `message`, `file`, `line`, `column`, `endLine`, and `endColumn` when known, and `counts` of `errors`, `warnings`, and `information`). A build
of a database adds `source` (`database`, `host`, `port`). A comparison follows the build part with its own (`source` and `target` in source mode, `added`,
`removed`, `modified`, the tenant blocks, `bodyNotes`, `undroppedSchemas`, `summary`), but only when it compared.
The optional function rewrites each path; the command line makes them relative to the current folder.

`schemaVersion` is 1. It changes only when a property is removed or changes meaning; a new property does not change it, so a reader should ignore properties it
does not know. Publish, scaffold, and documentation results have no JSON of their own yet (the documentation's JSON format is a file `DocumentAsync` returns).

## What is promised

The supported API is the engine's public surface: `PgProjEngine`, its requests and results and the types they carry, and `IProjectFileSource` with its three
sources and `ProjectSettings`. A test (`PublicApiTests`, against `src/PgProj.Engine.Tests/PublicApi.txt`) fails when that surface changes, so no change is made
by accident.

- **Before 1.0** the API can change between releases, and every change is listed in [`CHANGELOG.md`](../CHANGELOG.md).
- **From 1.0** it follows semantic versioning: within a major version, members and JSON properties are only added, never removed or changed in meaning.
- **The lower projects are not promised.** Core, Parser, Semantics, Dependencies, Diff, and Deploy are public C# because the command line and the tests use them,
  and the results carry some of their types (the change set, the model). Their shapes can change in any release; read what the engine's results give you rather
  than calling into them.
