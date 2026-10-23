# Contributing to pgproj

> **pgproj is not accepting contributions yet.** Pull requests from outside the project will be closed without review until this
> notice is removed. The rest of this document describes how contributions will work once they are accepted.

Thank you for your interest in contributing to pgproj!  
This project implements a full PostgreSQL schema compiler and migration engine.
Because the architecture is carefully layered, contributions must follow the
guidelines below to ensure stability, correctness, and long-term maintainability.

Please read this document fully before submitting a pull request.

---

## 1. Repository Governance

This repository uses a set of Copilot governance files, in [`docs/ai/`](docs/ai/), to ensure safe automated
assistance and consistent architectural behavior:

- copilot-instructions.md (the index)
- copilot-architecture.md
- copilot-cli.md
- copilot-deploy.md
- copilot-diff.md
- copilot-docs.md
- copilot-refactoring.md
- copilot-semantic.md
- copilot-tests.md
- CLAUDE.md, in the repository root (applies the same rules to Claude Code sessions)

All contributors (human or AI) must follow these rules.

---

## 2. Architectural Overview

pgproj is composed of seven major subsystems:

1. Parser (`PgProj.Parser`)
2. Semantic Model & Passes (`PgProj.Semantics`)
3. Dependency Graph (`PgProj.Dependencies`)
4. Diff Engine (`PgProj.Diff`)
5. Deployment / SQL Generation (`PgProj.Deploy`)
6. Engine, the pipeline as a library (`PgProj.Engine`)
7. CLI (`PgProj.Cli`)

Each subsystem has strict invariants.  
Do not modify cross-layer behavior without explicit approval.

The engine is the pipeline as a library, and the CLI is one front end (a "shell") over it. A change to what pgproj does belongs in the engine
or below it, never only in the CLI; a new front end follows [`docs/shells.md`](docs/shells.md). How a program calls the engine is
[`docs/library.md`](docs/library.md).

---

## 3. Contribution Workflow

### Step 1 — Open an Issue
Before submitting a PR, open an issue describing:

- The problem
- The proposed solution
- The affected subsystem(s)
- Any architectural implications

### Step 2 — Fork and Branch
Create a feature branch:

```sh
git checkout -b feature/my-change
```

### Step 3 — Follow Coding Guidelines
- Use clear naming
- Prefer small, focused changes
- Preserve deterministic behavior
- Do not introduce new dependencies without approval
- Follow the Copilot governance files

### Step 4 — Add or Update Tests
All changes must include tests unless trivial. Run the whole suite before you open the pull request: `dotnet test` (Docker must be running for the
integration tests, which start a disposable PostgreSQL). [`DEVELOPING.md`](DEVELOPING.md) says how to build, test against every supported PostgreSQL
version, and run the CLI from source.

### Step 5 — Submit a Pull Request
Include:

- A clear description of the change
- A summary of affected subsystems
- Test results
- Any documentation updates

### Step 6 — Sign the Contributor License Agreement
pgproj is licensed under the [Apache License 2.0](LICENSE). Every contributor must agree to the
[Contributor License Agreement](CLA.md) before a pull request can be merged. The first time you open a pull
request, a bot comments with the agreement; reply with the sentence it gives
(`I have read the CLA Document and I hereby sign the CLA`) and the check passes. You sign once, for all of
your contributions. If you contribute on behalf of an employer, read section 6 of the agreement first.

---

## 4. What You May Change

- Bug fixes
- Performance improvements
- Documentation
- Help text
- Diagnostics
- Test coverage
- Internal refactoring (private methods only)
- New front ends over the engine (see `docs/shells.md`)

---

## 5. What You Must Not Change

Unless explicitly approved:

- Public API shapes, above all the engine's: `PgProjEngine`, its requests and results, `IProjectFileSource` and its sources (`PublicApiTests` fails on a change),
  and the JSON document `build --format json` and `diff --format json` write
- Semantic model types
- Diff output structure
- SQL generation semantics
- CLI command names or option names
- pgproj.json field names or meanings
- Parser behavior or PgQuery integration

---

## 6. Testing Requirements

All tests must:

- Be deterministic: a timed test is allowed only to compare two measurements of the same run with a wide margin (category `Performance`;
  `docs/ai/copilot-tests.md` §3)
- Use real SQL snippets
- Validate actual behavior
- Avoid mocking PgQuery unless necessary

---

## 7. Documentation Requirements

Documentation must:

- Reflect actual code behavior
- Avoid speculation
- Include examples
- Follow Markdown best practices

---

## 8. Code Style

- Current C# conventions (the projects target .NET 10), matching the code around the change
- Use `var` when type is obvious
- Prefer expression-bodied members when clear
- Avoid excessive LINQ chaining
- Keep methods small and focused

---

## 9. Communication

If you are unsure whether a change is allowed, open an issue or ask before coding.

---

Thank you for contributing to pgproj!

