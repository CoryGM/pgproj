# GitHub Copilot Architecture Instructions for pgproj

This file describes the architectural boundaries of the pgproj system. Copilot
must respect these boundaries when modifying code, fixing bugs, or refactoring.

---

## 1. Pipeline Overview (Do Not Break)

The pgproj pipeline consists of:

1. Parser (PgProj.Parser)
2. Semantic Passes (PgProj.Semantics)
3. Dependency Graph (PgProj.Dependencies)
4. Diff Engine (PgProj.Diff)
5. Deployment (PgProj.Deploy)
6. Engine (PgProj.Engine): the pipeline as a library (G-309). It reads a project's files only through `IProjectFileSource` and returns
   results; it has no console, no `System.CommandLine`, and no exit codes. A front end (the CLI, an editor, a web API) calls the engine
   and renders what it returns.
7. CLI (PgProj.Cli), the reference shell over the engine (§3)

Copilot must preserve this structure and the Parser → Semantic → Dependencies → Diff → Deploy →
Engine → CLI flow between stages. The engine's public surface (`PgProjEngine`, its requests and results, `IProjectFileSource` and its
sources) is a public shape: it changes only on purpose, through `PublicApiTests` and a `CHANGELOG.md` entry.

---

## 2. Architectural Rules

See `copilot-instructions.md` §1 (Architectural Invariants) and §2 (`pgproj.json` Contract) for
the full do-not-break list (SQL parsing, the semantic model types, dependency resolution, diff
meaning, SQL generation semantics, the CLI contract, and the `pgproj.json` contract) — it applies
to every layer above. Within those bounds, Copilot may improve performance, readability, and
diagnostics, and fix bugs, following `copilot-refactoring.md`'s safety rules.

---

## 3. Shells

A shell is a front end over the engine: the CLI, and any other (an editor extension, a language server, a web API). Copilot must follow
[`docs/shells.md`](../../shells.md) when adding or changing one, and must put no pipeline logic in a shell: a shell chooses the file source,
resolves settings and connections, calls the engine, and renders and writes what the engine returns. Whatever a shell needs that the engine does
not give it is added to the engine (on purpose, through `PublicApiTests` and a `CHANGELOG.md` entry), so every shell has it. (The user's
decision of 2026-10-20 for G-309g, #220, confirmed 2026-10-21.)

---

# End of Copilot Architecture Instructions
