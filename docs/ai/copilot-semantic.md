# GitHub Copilot Semantic Model Instructions for pgproj

These instructions tell GitHub Copilot how to safely modify semantic passes
without breaking the meaning of the semantic model or the compiler pipeline.

---

## 1. Semantic Model Invariants (Do Not Break)

Copilot must preserve the meaning of:

- DatabaseModel
- TableDefinition
- ColumnDefinition
- ConstraintDefinition
- IndexDefinition
- FunctionDefinition
- ViewDefinition
- TypeDefinition
- ExpressionNode
- SemanticDiagnostic

Copilot must not:
- rename fields
- remove fields
- change field types
- change semantic meaning

---

## 2. Semantic Passes

Copilot must preserve the behavior of:

- DeclarationPass
- ColumnPass
- ConstraintPass
- ExpressionMappingPass
- DeepDependencyPass
- TypeResolutionPass
- SemanticBinder
- ExpressionNormalizationPass
- ExpressionSemanticBindingPass
- ViewSemanticPass

Copilot may:
- improve performance
- improve readability
- add diagnostics
- fix bugs

Copilot must not:
- change pass ordering
- remove passes
- merge passes unless explicitly instructed

New passes, and steps that run on the syntax trees before the passes (as `SearchPathQualifier`, `AlterTableColumnApplier`,
`ConstraintNameAssigner`, and `TypedTableExpansion` do), may be added when a gap needs one, provided no existing pass changes
its behavior or its position relative to the others, and none is removed or merged. (Confirmed by the user, 2026-09-30.)

---

## 3. Expression Semantics

Copilot must preserve:
- normalized expression behavior
- identifier binding behavior
- expression comparison behavior

Copilot may:
- improve normalization
- improve binding diagnostics

### Normalization is part of the definition of done

Whenever Copilot adds a new field to a definition record (`ColumnDefinition`, `ViewDefinition`,
`ConstraintDefinition`, `IndexDefinition`, `FunctionDefinition`, and similar), the work is **not
complete** until that field is canonicalized for comparison in the same change. Deferring this to
later cleanup is not permitted.

For each new field, make an explicit decision:

- **Order-insensitive collection** (constraint column sets, referenced columns, privileges) — sort
  with a stable `StringComparer.Ordinal` key.
- **Order-sensitive collection** (index key columns, primary key column order, function parameter
  order) — do **not** sort. The order carries meaning. Preserve it and assert so in a test.
- **Identifier-valued** — case-canonicalize consistently with existing identifier handling.
- **Expression-valued** (view bodies, CHECK text, defaults) — route through
  `ExpressionNormalizationPass.NormalizeExpression` and compare the normalized form, never raw text.

The goal is that two semantically identical schemas never produce a diff because of formatting,
identifier casing, or ordering within an order-insensitive collection.

### Lossy normalization must preserve the raw value

Normalization serves two different goals that pull in opposite directions. *Resolution* wants to be
aggressive (`varchar(10)` and `varchar(20)` both resolve to `varchar`). *Comparison* wants to be
conservative (`varchar(10)` → `varchar(20)` is a real change). A single normalized string cannot
serve both.

When canonicalization discards semantically meaningful detail — type modifiers, precision, scale,
collation — Copilot must **keep the raw value and add the canonical form alongside it**, never
overwrite it. The established pattern in this codebase is
`ColumnDefinition.RawDefaultExpression` paired with `ColumnDefinition.NormalizedDefaultExpression`.
Follow that shape.

Where a flattened canonical string would be lossy, prefer a structured decomposition (for types:
base type, modifiers, array-ness) so that equality can be evaluated per component.

A normalizer written for resolution must not be reused as a comparison key without checking what it
discards. Specifically, `TypeResolutionPass.NormalizeTypeReference` strips array suffixes, type
modifiers, and the `pg_catalog.` prefix; it is correct for lookup and unsafe for diffing.

This requirement does not license changing pass ordering or existing normalization semantics; it
means extending the existing passes to cover newly added data.

Copilot must not:
- change semantic meaning of expressions

---

## 4. Dependency Extraction

Copilot must preserve:
- ReferencedTables
- ReferencedColumns
- ReferencedTypes
- ReferencedFunctions

Copilot may:
- improve extraction logic
- add missing cases

Copilot must not:
- change dependency semantics

---

# End of Copilot Semantic Instructions
