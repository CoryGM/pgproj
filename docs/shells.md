# Adding a shell

A **shell** is a front end over pgproj's engine: the command line, an editor extension, a language server, a web API. The engine (`PgProj.Engine`) does the
work: it reads the project, parses it, compares it, plans and runs a publish, scaffolds, and documents. A shell decides where the project's files come from, asks
the engine, and shows or stores what it returns. This page is for a developer adding one. How to call the engine, with examples, is
[Using pgproj as a library](library.md); read it first.

The rule, which the AI rules make binding (`docs/ai/copilot-architecture.md`): **a shell holds no pipeline logic.** If a shell needs something the engine
does not give it, the engine gains it (see the last section), and every shell then has it.

## What a shell owns

- **Where the files come from.** It picks an `IProjectFileSource`: the disk (`DiskFileSource`), files it received (`InMemoryFileSource`), or an editor's
  unsaved buffers over the disk (`OverlayFileSource`). It names the project by its `pgproj.json` in that source, or by `ProjectSettings` and a root folder.
- **Settings and connections, and where secrets come from.** It resolves what the request carries: the target and source connection strings (the command line:
  option, then environment variable; the engine then falls back to `pgproj.json`), the `exclude*` overrides, the tenants, and the consents of a publish
  (`AllowDestructive`, `AllowSuperuserStatements`, `AllowLockLimit`). Where a password is kept (a variable, a secret store, a prompt) is the shell's decision,
  and so is who may supply one.
- **Calling the engine**, one request per action, with a `CancellationToken` and, if it shows progress, an `IProgress<EngineEvent>`.
- **Showing the result.** Statuses, diagnostics, change sets, warnings, refusals, and scripts are data; the shell lays them out. The engine's words are in the
  results where a message is meant to be shown as it is (a `Message`, a diagnostic's `Message`, a `PublishNotice.Text`, an event's `Message`).
- **Writing what is returned.** The engine writes no file: the script for `--output`, a scaffold's files, the documentation, a diagram's text, the dumped
  model. Where they go, and what happens to a file already there, is the shell's (the command line's backup-or-keep rules are its own).
- **Mapping statuses** to what its world uses: exit codes, HTTP statuses, an editor's problem list.
- **Text of its own.** The engine masks every message it returns: statuses' messages, diagnostics, events, the texts of a publish's warnings and
  refusals and their items, a failed statement's report, and what `init` and `document` say, so a shell shows them as they are and needs no masker for them.
  What a shell writes itself (a connection string it echoes, an exception's message) is its own to keep free of passwords. **The deployment SQL is never
  masked** (`PublishResult.Script`, each part's script and steps): a build may put a secret into the SQL on purpose for that deployment, and masking it would
  change what is deployed. The command line prints and writes the script as it is.

## What a shell must never do

- **Read the project's files itself**, or parse `pgproj.json`, to decide something the engine decides. Everything goes through the file source it hands the
  engine, so what it shows is what was built.
- **Re-implement a check.** Whether a change is destructive, needs a superuser, rewrites a table, or passes the server's lock table is in the engine's
  `PublishResult` (`Warnings`, `Refusals`, each `PublishNotice` with its `Kind` and `Items`). A shell shows them; it does not decide them again.
- **Run a publish without a person's consent where the command line asks for one.** `PublishMode.Execute` with `AllowDestructive` (or the other consents) set
  is the shell saying a person agreed. A shell sets them only from a person's explicit choice for that run, never by default.
- **Show an unmasked connection string or password** in a message, a log, or an error page (the deployment script, which is shown and written as it will run,
  aside).
- **Hold pipeline state between calls.** The engine is stateless; a shell that caches a model across requests owns its staleness. A new request is the way to
  get a fresh result.

## The command line, the reference shell

`src/PgProj.Cli` is the shell every other one can copy from. Its output is fixed by `tools/engine-parity/parity.sh`, which compares it with a build from before
the engine existed.

| File | What it does |
|---|---|
| `Program.cs` | The root command, the six commands, `-h` as `--help`, `CommandLineAliases` (`--excludeSchemas`), and `HelpLayout`. |
| `BuildCommand.cs`, `DiffCommand.cs`, `PublishCommand.cs`, `InitCommand.cs`, `DocumentCommand.cs`, `AboutCommand.cs` | One per command: its options and help, the request built from them, the call to the engine, and the result printed, written, and turned into an exit code. The hooks (`AfterBuild`, `AfterPlan`, `AfterScaffold`) are where a command prints the header and the diagnostics before a slow database read, the script and the warnings before anything runs, and writes `init`'s files before the report is made. |
| `ConnectionSettings.cs` | The connection strings from the options and the environment variables (`PGPROJ_SOURCE_CONNECTION`, `PGPROJ_TARGET_CONNECTION`). |
| `CliLog.cs`, `LogProgress.cs` | Verbosity (`--verbose`, `--quiet`), standard output and standard error, password masking on every line it writes (the engine's messages arrive masked; this also covers the command's own lines, such as an unexpected error's message), and the engine's events written as the `[verbose]` and progress lines. |
| `DiffOutputWriter.cs`, `TenantDiffOutput.cs` | The text of a change set and of a tenants comparison; `--format json` writes `CompareResult.ToJson` and `BuildResult.ToJson` instead. |
| `ErdFiles.cs`, `ErdOptions.cs`, `FilePlacement.cs`, `Documentation/DocumentationFiles.cs` | Where a diagram and the documentation go, and a file already there (`--erd-existing`, `--existing`). |
| `HelpLayout.cs` | `--help`. |
| `InstallKind.cs` | How this copy was installed (a .NET tool, a package, the installer, an archive), which `about` prints: it describes the shell, so it is the shell's. |
| `SourceProjectOptions.cs` | The `--source-connection` option of `build`, `diff`, and `publish`. |
| `CliContext.cs` | The diagnostics' text form (`file:line:column-endLine:endColumn: [Severity] message`), paths shown relative to the current folder, and `pgproj.json` read alone (for `document`'s connection). Everything else is the engine's: a command calls it, and so does a test of what the engine decides. |

The command line is allowed to see the engine's internals (`InternalsVisibleTo("pgproj")`), and it still uses a few: the text of some messages and the option
names of two consents (a shell lays out results itself, from the public data), and the parser's password masker for the lines it writes itself. Nothing a shell
needs is behind them any more: `build --source-connection` is `BuildRequest.SourceConnectionString`, and the engine masks every message it returns, so a
shell outside this repository does both through the public API.

## Sketches

### An editor or a language server

- **Files:** an `OverlayFileSource` over the disk. Set a buffer when a document is opened or changed, with the editor's version; remove it when it is closed.
- **When to build:** on open and on save, or on change after a short pause. Cancel the build in flight when a newer one starts (a `CancellationTokenSource`
  per build). One `PgProjEngine` serves every build. Keep one `ParseCache` per open project and pass it on every build of it (`BuildRequest.ParseCache`):
  a build after an edit parses only the files that changed. The semantic checks still run over the whole project, which on a very large one (thousands of
  files) takes a second or more, so build there after typing stops or on save. As the person types, `CheckDocumentAsync` gives the document's own syntax
  diagnostics in milliseconds (pass the same `ParseCache`); show them, and replace them with the build's when it arrives.
- **Diagnostics:** `BuildResult.Diagnostics`. Match each to its document by `File` (a full path) and to the buffer's state by the document's `Version` in
  `BuildResult.Documents`: drop the result if the buffer has moved on. Positions are 1-based and count characters (Unicode code points), with the end just past
  the last one; the Language Server Protocol's are 0-based and count UTF-16 code units by default, so subtract one from each, and add one to a column for each
  character before it on the line that is outside the Basic Multilingual Plane. A diagnostic with a `File` but no `Line` belongs to the whole file; one with no
  `File` to the project.
- **Diff and publish:** run `CompareAsync` or `PublishAsync` in `Plan` mode from a command, show the change set or the script with its warnings, and run
  `Execute` only from a separate, explicit action, with the consents the person ticked. `Execute` plans again, so tell the person that what runs is what the
  database needs at that moment.
- **Connection strings:** from the editor's settings or secret storage, never from the project's files unless the person put `targetConnectionString` there.

### A web API

- **Files:** an `InMemoryFileSource` per request, filled from the request, or `ProjectSettings` with a root of your choosing. Never a path on the server's disk
  taken from a request.
- **Who may do what:** reading a project and building it touches nothing; `CompareAsync` and `PublishAsync` connect to databases. Which callers may name which
  database, and who may run `Execute` (and with which consents), is the service's authorization, decided before the request reaches the engine.
  Connection strings come from the server's configuration, keyed by something the caller may name, not from the request body.
- **Time:** give each request a `CancellationTokenSource` with a timeout, and cancel it when the client disconnects. A cancelled run ends with an
  `OperationCanceledException`, never a half result.
- **One engine for all requests:** `PgProjEngine` is stateless and safe from several threads. A file source is read by one run at a time and must not change
  while a run reads it.
- **Responses:** `BuildResult.ToJson()` and `CompareResult.ToJson()` are the documents the command line prints; the other results have no JSON of their own, so
  pick the properties you return. Map `ProjectNotFound`, `ProjectInvalid`, and `Refused` to a client error, `ConnectionFailed` to a gateway error,
  `Failed` (a project with errors) to a successful response that carries the diagnostics, and an exception to a server error.

### A program that is not .NET

The engine is a .NET library, so a VS Code extension in TypeScript or a page in a browser cannot load it. It needs a .NET process to talk to:

- **The command line**, where it has JSON: `pgproj build --format json` and `pgproj diff --format json` write the engine's document on standard output alone
  ([the JSON document](library.md#the-json-document)). `publish`, `init`, and `document` have no JSON result.
- **A host to write**: a language server over standard input and output for editors, or an HTTP service for a web front end, built as a shell on this page.
  Neither is in this repository.

## Checklist for a new shell

1. It names the project through a file source and never reads the project's files itself.
2. Every action is one engine call; every check it shows comes from a result.
3. It maps every status of every call it makes, and treats an exception as a defect (or a cancellation).
4. It shows the engine's messages as they are (they are masked already), and puts no password into text of its own.
5. `Execute` and its consents come only from a person's explicit choice.
6. It honors cancellation and passes progress on, or ignores it on purpose.
7. **Tests:** a contract test that runs the same request through the engine directly and through the shell, and checks the shell shows what the result holds
   (the command line's is `tools/engine-parity/parity.sh` against a baseline, and `BuildJsonTests` and `DiffJsonStatusTests` for its JSON); the shell's own
   tests for its mapping of statuses and its rendering; nothing that re-tests the pipeline, which the engine's and the lower projects' tests cover.
8. Its documentation says which commands it offers and how they map to the engine's calls.

## When a shell needs more from the engine

The engine's public surface is guarded: `PublicApiTests` compares it with `src/PgProj.Engine.Tests/PublicApi.txt` and fails on any difference.

1. **Ask first.** The public API is a public shape (`docs/ai/copilot-instructions.md` §1): a change is made on purpose, with the owner's agreement.
2. **Add, do not change.** A new `init` property on a request or result, a new status value at the end, a new entry point. Never a positional parameter on
   an existing record, and never a member removed or its meaning changed (from 1.0 that needs a new major version).
3. **Update the listing on purpose.** The test writes `PublicApi.actual.txt` beside its output; copy it over `PublicApi.txt` when the change is meant.
4. **JSON:** a property added to the document is written by the engine's writer (`EnvelopeJson` for the head, the result's own writer for the rest), so the
   command line and every shell get it at once. `schemaVersion` changes only when a property is removed or changes meaning.
5. **Record it:** an entry in `CHANGELOG.md`, the library page (and its examples' test, `LibraryExamplesTests`), and this page if a shell's duties change.
