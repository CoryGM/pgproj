# GitHub Copilot Deployment Instructions for pgproj

These instructions tell GitHub Copilot how to safely modify SQL generation
without breaking deployment semantics.

---

## 1. SQL Generation Invariants (Do Not Break)

SQL generation lives in:

- `SqlOperation` — the base type for a single generated statement
- `CreateOperation` / `AlterOperation` / `DropOperation` — one per operation kind
- `OperationBatch` — an ordered set of operations, plus destructive-operation classification (G-35/G-80)
- `ScriptGenerator` — turns a diff + dependency graph into an `OperationBatch`
- `ScriptWriter` — renders the final script text

The script `publish` runs is composed in `PgProj.Engine` (G-309c): `PgProjEngine.PublishAsync` plans it from `PublishUnit` and `ScriptComposition`, checks it
(warnings and refusals are typed `PublishNotice`s whose texts are what `pgproj publish` prints), and with `PublishMode.Execute` runs it through
`ScriptExecution`; the engine never writes a file, so `--output` is the CLI writing `PublishResult.Script`. A change to what is checked, or its words, is a
change to `publish`'s output (see `copilot-cli.md`) and to `PublicApi.txt` only if a public member moves.

See `copilot-instructions.md` §1 (Deployment) for the do-not-break rule (SQL semantics, operation
ordering, dependency ordering) that applies to all of the above. Copilot may improve formatting,
whitespace, indentation, readability, error messages, and logging without changing behavior.

---

## 2. Execution Semantics

Copilot must preserve `--dry-run`, `--execute`, and `--output`-to-file behavior exactly as `copilot-cli.md`'s
Option Contract describes them.

---

# End of Copilot Deployment Instructions
