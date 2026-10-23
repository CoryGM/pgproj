# GitHub Copilot Test-Writing Instructions for pgproj

These instructions tell GitHub Copilot how to safely generate or modify tests in
this repository without breaking architectural invariants or changing behavior.

---

## 1. General Principles

Copilot must:
- Preserve existing test behavior.
- Add tests only when they validate existing functionality.
- Avoid rewriting large portions of the test suite unless explicitly instructed.
- Prefer small, focused tests over broad integration rewrites.

Copilot must not:
- Change public APIs to “make tests pass.”
- Change semantic model behavior to satisfy tests.
- Change diff engine behavior to satisfy tests.
- Change SQL generation behavior to satisfy tests.

---

## 2. Test Coverage Priorities

Copilot should prioritize tests for:
- SQL parsing (PgProj.Parser)
- Semantic model construction (PgProj.Semantics)
- Dependency graph correctness (PgProj.Dependencies)
- DiffEngine correctness (PgProj.Diff)
- SQL generation (PgProj.Deploy)
- The engine's calls and results (PgProj.Engine: statuses, typed warnings and refusals, file sources, the public surface in `PublicApi.txt`, the library page's examples)
- CLI behavior (PgProj.Cli: options, help, what it prints and exits with)

---

## 3. Test Style Rules

Copilot should:
- Use Arrange/Act/Assert structure.
- Prefer deterministic inputs and outputs.
- Avoid mocking PgQuery unless absolutely necessary.
- Use real SQL snippets for parser tests.
- Use small semantic models for diff tests.
- Use explicit expected SQL for deployment tests.

Copilot must not:
- Introduce flaky tests.
- Introduce timing-based tests, except as below.
- Introduce network-dependent tests.

Timed tests are allowed for two purposes (the user's decision of 2026-10-21): to keep a **directional history** of performance (how a measured step
moves from change to change), and to **compare performance characteristics before and after a change** (for example, that doubling an input no
longer quadruples a step's time). A timed test must still not be flaky. It compares measurements taken in the same run (one size against another,
one way against another) rather than an absolute number of milliseconds, and it allows a generous margin, so a slow or busy machine does not fail it.
Where a deterministic check exists (a counter of the work done, as G-310's `TextPassCounter` and G-314's `ColumnKeyLookup` count), prefer it, or use
both. A timed test carries `[TestCategory("Performance")]` and `[DoNotParallelize]`, times with its project's `TimedRuns` (after a warm-up; the
parser's takes the fastest run, G-327), and is checked to fail against the code before the change it guards where that code can be run (G-321).

---

## 4. Adding New Tests

When adding tests, Copilot must:
- Base test behavior strictly on existing code.
- Validate real behavior, not hypothetical behavior.
- Avoid speculative architectural changes.

---

## 5. Modifying Existing Tests

Copilot may:
- Fix broken assertions.
- Improve readability.
- Reduce duplication.
- Add missing edge cases.
- Delete scratch/diagnostic tests (see section 6) without explicit approval.

Copilot must not:
- Change test intent.
- Remove tests unless explicitly instructed, except scratch tests as defined in section 6.
- Change expected outputs unless the code has changed.

---

## 6. Scratch and Diagnostic Tests

A "scratch test" is a temporary test written to inspect behavior (for example, dumping an
AST) rather than to validate it. Copilot may delete scratch tests **without asking for
approval**.

A test qualifies as a scratch test only if **all** of the following are true:

- It performs no real validation. It either calls `Assert.Fail(...)` unconditionally to
  print diagnostic output, or asserts nothing meaningful about behavior.
- It fails (or is ignored) by design, not because of a regression.
- Removing it does not reduce coverage of any production behavior.

Common indicators: names such as `Scratch*`, `Dump*`, `Debug*`, `Temp*`, or `Explore*`;
`Assert.Fail` used to emit serialized state; bodies that only print or format data.

Rules:

- Copilot must confirm a test is genuinely a scratch test by reading its body before
  deleting it. When in doubt, ask.
- A failing test is **not** a scratch test merely because it fails. A genuine test that
  fails indicates a bug or regression and must be investigated and fixed, never deleted.
- Copilot must never reclassify a real test as a scratch test in order to make the suite
  pass. This is expressly forbidden by section 1.
- Deletions of scratch tests must be named in the change that makes them (its commit message).

All other test removals still require explicit instruction from the user.

---

# End of Copilot Test Instructions
