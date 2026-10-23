# GitHub Copilot Instructions for pgproj

These instructions tell GitHub Copilot how to safely assist with this repository
without breaking the intended architecture, semantics, or CLI behavior.

Copilot should follow these rules when generating code, fixing bugs, or refactoring.

---

## 0. Binding Repository Guidance

**At the start of every session, before any other work, read these in full:** `CLAUDE.md` and this file. Read a file too large for one read in parts
until you reach its end. A session resumed after an interruption, or continued from a summary, is a new session and reads them again.

**Then read, in full, before the work they cover:**

| Before you... | Read |
|---|---|
| change code that crosses projects, or the engine's public API | `copilot-architecture.md` |
| change a command, an option, help text, or an error message | `copilot-cli.md` |
| change what `publish` generates or how it runs | `copilot-deploy.md` |
| change what `diff` compares or prints | `copilot-diff.md` |
| change or write any documentation | `copilot-docs.md` |
| refactor, or change code for performance | `copilot-refactoring.md` |
| change the semantic model, a pass, an expression, or a dependency | `copilot-semantic.md` |
| add or change a test | `copilot-tests.md` |
| change or rely on what a user-facing document says | that document: `README.md`, `docs/commands.md`, `pgproj.json.md`, `documentation.md`, `PROJECT.md`, `CHANGELOG.md`, `docs/notes/`, `docs/library.md`, `docs/shells.md` |
| contribute, build, test across PostgreSQL versions, or release | `CONTRIBUTING.md`, `DEVELOPING.md` (and `CLA.md` for licensing) |

Other directories (`samples/`, `tools/`) are read when the work needs them.

The maintainer's process rules (gaps, commits, the project's own documents) are kept outside this repository. When they are available to a session (for Claude
Code, through `CLAUDE.local.md`), they bind too, and say what else to read at the start of a session.

### 0a. Session brief

- **Layout:** `src/PgProj.*` (Parser, Semantics, Dependencies, Diff, Deploy, Core, Engine, Cli, each with its `.Tests`, and
  `PgProj.Integration.Tests`, the Docker suite); the rules in `docs/ai/`; user documents in the root and `docs/`; `samples/`; `tools/`
  (`test-all-versions`, `diffqa`, `engine-parity`).
- **Gaps** are GitHub issues labelled `gap`, titled `G-nn — <title>` (`gh issue list --label gap`). Refer to a gap by its `G-nn`. Before saying an
  item is still open, check GitHub.
- **Ask first:** before changing what `publish` generates or what `diff` counts as a change; before crossing an invariant of §1;
  before starting a gap labelled `deferred`; before a test change in a refactor beyond moving it or an immaterial edit; before
  modeling a cluster-wide object (roles, tablespaces, databases: report them, the user decides).
- **What is deployed is what the user wrote:** normalized forms are for comparison only.

### 0b. The rule files

All in `docs/ai/` beside this one, binding whenever their subject applies (§0's table says when to read each):
`copilot-architecture.md`, `copilot-cli.md`, `copilot-deploy.md`, `copilot-diff.md`, `copilot-docs.md`, `copilot-refactoring.md`,
`copilot-semantic.md`, `copilot-tests.md`.

When a change spans multiple areas, Copilot must apply all relevant files. These
files supplement this document; they do not override it, the `.github/copilot-instructions.md`
file, or the user's explicit request. `CONTRIBUTING.md` and `DEVELOPING.md` are
also repository guidance and should be followed for contribution workflow,
development, testing, and coding conventions when applicable.

Copilot must read the applicable guidance files before modifying code, tests,
configuration, or documentation.

---

## 1. Architectural Invariants (Do Not Break)

Copilot must preserve the following architectural rules:

- **SQL parsing:** only through PgQuery (`PgQueryParser`, `PgQueryInterop`); never another or a hand-written SQL parser.
- **Semantic model** (`copilot-semantic.md`): the meaning of the `*Definition` types and `ExpressionNode` does not change; fields are
  only added (as `init` properties).
- **Dependency graph:** `DependencyAnalyzer` and `DependencyGraph` define ordering; their resolution semantics do not change.
- **Diff** (`copilot-diff.md`): the meaning of added, removed, and modified, and `ModificationDetail`'s shape, do not change.
- **Deployment** (`copilot-deploy.md`): `ScriptGenerator` and the `SqlOperation` classes define the SQL; its semantics do not change
  (formatting may).

### Engine (`PgProj.Engine`, G-309)
- The engine is the pipeline as a library. Copilot must not give it a console, command-line parsing, or exit codes, and must not make it
  read a project's file any other way than through `IProjectFileSource`.
- Its public API (`PgProjEngine`, the request and result types, `IProjectFileSource`) must not change shape without explicit
  instruction: `PgProj.Engine.Tests/PublicApiTests` fails on a change, and `PublicApi.txt` is replaced only on purpose.
- Its JSON document (`BuildResult.ToJson`, `CompareResult.ToJson`; what `build --format json` and `diff --format json` print) is a public
  shape too: properties are only added; `schemaVersion` changes only when one is removed or changes meaning, which needs explicit instruction.
- The CLI is a shell over the engine (`copilot-architecture.md` §3, `docs/shells.md`): what pgproj does is decided in the engine or below it,
  never only in a shell. `docs/library.md` describes the public API; `LibraryExamplesTests` runs its examples.

### CLI
- The CLI contract must remain stable:
  - `pgproj init`
  - `pgproj build`
  - `pgproj diff`
  - `pgproj publish`
  - `pgproj document`
- Copilot must not rename commands or options.
- Copilot must not change required/optional flags without explicit instruction.

### Live Database Loader / Scaffold (`pgproj init`)
- `LiveDatabaseModelLoader` (`PgProj.Core`) is read by two consumers, both through the engine: `diff`/`publish` (via
  `LoadAsync`, called by the engine's `LiveComparison`, for comparison) and `init` (via `LoadForScaffoldAsync`/`BuildScaffoldFiles`
  in `LiveDatabaseModelLoader.Scaffold.cs`, called by `PgProjEngine.ScaffoldAsync`, for writing a new project's SQL files). A change to what the live loader can read — a new object kind, or new
  structural data on an existing one — is not finished until both consumers reflect it.
- Whenever Copilot adds parser/semantic/diff/deploy support for a PostgreSQL feature that a live database can
  contain, Copilot must also update `LiveDatabaseModelLoader` to read it back, and update
  `LiveDatabaseModelLoader.Scaffold.cs`/`BuildScaffoldFiles` so `pgproj init` scaffolds it into its own file (or
  alongside the object it belongs to — a constraint, index, and trigger already live inside their table's file;
  a new per-table object kind such as a row-level-security policy should follow the same pattern) rather than
  silently omitting it.
- If a feature genuinely cannot be read from a live database (it only affects `diff`/`deploy` logic, with nothing
  for the live loader or `init` to read or scaffold), Copilot must say so explicitly in the gap's issue
  rather than leaving `init` silently out of date. A gap that skips it is not done.

---

## 2. pgproj.json Contract (Do Not Break)

Copilot must preserve the meaning of:

- `name`
- `version`
- `source`
- `schemas`
- `targetConnectionString` and `sourceConnectionString` (which replaced `connectionString` in G-304a, at the user's explicit instruction of 2026-10-15; `connectionString` is no longer read and a file that has it is refused)
- `predeploy`
- `postdeploy`

Copilot must not:
- remove fields
- rename fields
- change field types
- change how pgproj.json is loaded

Copilot may:
- add optional fields if they do not break existing behavior
- improve validation logic

---

## 3. Bug Fixing Rules

When fixing bugs, Copilot must:

### Allowed
- Fix null reference exceptions
- Fix incorrect LINQ queries
- Fix incorrect dictionary lookups
- Fix incorrect string comparisons
- Fix incorrect expression comparisons
- Fix incorrect dependency graph traversal
- Fix incorrect SQL generation formatting
- Improve diagnostics
- Improve help text
- Improve error messages

### Not Allowed
- Change public API shapes
- Change semantic meaning of any model type
- Change diff output structure
- Change SQL generation behavior
- Change CLI command names or option names
- Change pgproj.json field names or meanings
- Introduce new dependencies without explicit instruction

---

## 4. Refactoring Rules

Copilot may:
- extract helper methods
- improve readability
- reduce duplication
- reorganize private methods
- improve naming
- add comments
- improve performance without changing behavior

Copilot must not:
- change public method signatures
- change record shapes
- change semantic model types
- change diff result types
- change SQL output types
- change CLI command structure

---

## 5. Help Text and Documentation

Copilot may:
- improve help text
- improve examples
- improve error messages
- generate Markdown documentation
- generate pgproj.json documentation

Copilot must not:
- change CLI behavior while improving help text
- introduce new required options
- remove existing options

---

## 6. Testing and Validation

Copilot should:
- preserve existing test behavior
- add tests when appropriate
- not remove tests unless explicitly instructed, except scratch/diagnostic tests,
  which may be deleted without approval as defined in `copilot-tests.md` section 6
- never delete or weaken a genuine failing test to make the suite pass; investigate
  and fix the underlying cause instead

---

## 7. General Guidance

Copilot should:
- prefer minimal changes when fixing bugs
- preserve backward compatibility
- preserve deterministic behavior
- preserve stable output formats
- avoid speculative architectural changes
- ask for clarification in comments if a change is ambiguous
- if a requested or needed change would cross one of the invariants in this document, stop and ask the user
  first, explaining which rule is in the way, rather than working around it

---

## 8. Project Context

Copilot should:
- Read `PROJECT.md` to get context on the end goal for the project.
- Read `README.md` and the command reference, `docs/commands.md`, for CLI usage that must be preserved unless new or breaking abilities are added that need to be surfaced in the command line instructions.
- Read the open gap issues (`gh issue list --label gap`; each is titled `G-nn — <title>`) before starting new work, and work
  gaps in dependency order.

---

## 9. Development Commands

- Build: `dotnet build` (picks up `pgproj.slnx` automatically).
- Run all tests: `dotnet test` — `src/PgProj.Integration.Tests` spins up a disposable PostgreSQL container via
  Testcontainers, so Docker must be running or those tests fail with "Failed to connect to Docker endpoint"
  (other test projects are unaffected); run it after any change to `LiveDatabaseModelLoader` or deployment SQL
  generation.
- PostgreSQL versions: pgproj supports PostgreSQL 14, 15, 16, 17, and 18 (14 and 15 since G-269). The integration tests use
  `postgres:18-alpine`, the newest supported release, unless `PGPROJ_TEST_POSTGRES_IMAGE` names another image; 18 is the
  baseline the test counts are measured on (user's policy, 2026-10-13). A change to `LiveDatabaseModelLoader` or deployment
  SQL must pass the whole suite on every supported version (user requirement, 2026-09-25; G-120): run
  `tools/test-all-versions.sh` (or `.ps1`), which runs them in parallel, three at a time (user's decision, 2026-10-13); CI
  runs all five on every pull request. A test for a version range is marked `[PostgreSqlVersion(Minimum = n)]` or
  `[PostgreSqlVersion(Maximum = n)]`; a construct a supported version lacks goes in `ServerFeatureRequirements`. A real-server
  check in a gap entry must say which versions it covered. When a new major version is supported (19), it becomes the
  default and the baseline the same way. See `DEVELOPING.md` §7.
- Run a single test: `dotnet test --filter "FullyQualifiedName~ClassName.MethodName"` (tests use MSTest, not
  xUnit/NUnit).
- Run the CLI from source: `dotnet run --project src/PgProj.Cli -- build --project ./pgproj.json` (swap `build`
  for `diff`/`publish`/`init` and their options).
- Each `src/<Project>.Tests` project tests only `<Project>`; `PgProj.Integration.Tests` is the only one that
  talks to a real (containerized) PostgreSQL server, exercising the full publish/rollback/re-diff workflow.
  `PgProj.Engine.Tests` tests the engine's calls and results without a server, guards its public surface
  (`PublicApi.txt`), and runs the examples of `docs/library.md`; `PgProj.Cli.Tests` tests the CLI as a shell
  (options, help, what it prints and exits with).
- Output parity of the CLI with the build before the engine: `tools/engine-parity/parity.sh` (its header says how).

---

# End of Copilot Instructions
