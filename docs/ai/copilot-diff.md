# GitHub Copilot Diff Engine Instructions for pgproj

These instructions tell GitHub Copilot how to safely modify the diff engine
without breaking diff semantics or output formats.

---

## 1. Diff Semantics (Do Not Break)

Copilot must preserve the meaning of:

- AddedChange
- RemovedChange
- ModifiedChange
- ModificationDetail
- DiffResult
- ChangeSet

Copilot must not:
- rename change types
- remove change types
- change meaning of "added", "removed", "modified"
- change output structure

---

## 2. Expression Comparison

Copilot must preserve:
- CompareExpressions behavior
- normalized expression comparison
- null-handling semantics

Copilot may:
- improve comparison accuracy
- improve normalization

### Phantom-diff prevention is part of the definition of done

When a new field becomes visible to the diff engine, Copilot must add regression tests proving the
field cannot produce a spurious change:

1. Diff a model against itself and assert the change set is empty.
2. Diff two inputs that differ **only** cosmetically — whitespace, identifier casing, or ordering
   within an order-insensitive collection — and assert no change is reported.
3. Where the field is order-sensitive (index key columns, primary key column order, function
   parameter order), assert the opposite: a reordering **is** reported as a change.
4. Where the field is normalized lossily, assert the opposite direction as well: a **meaningful**
   difference that the normalizer would discard (for example `varchar(10)` → `varchar(20)`) must
   still be reported as a change.

Both failure directions matter, and they are not equally dangerous. A false positive (phantom diff)
is noisy but safe. A false negative — the diff missing a real change because normalization discarded
it — causes a silent under-migration, where `publish` reports success and leaves the target schema
wrong. Test for both.

A change that lets the diff report differences between semantically identical schemas is a defect,
not an acceptable approximation. So is a change that lets the diff miss a genuine difference.

Copilot must not:
- change comparison semantics

---

## 3. Object Comparison

Copilot must preserve:
- table comparison rules
- column comparison rules
- constraint comparison rules
- index comparison rules
- function comparison rules
- view comparison rules
- type comparison rules

Copilot may:
- add missing cases
- improve diagnostics

Copilot must not:
- change meaning of equality

---

## 4. Diff Output Stability

Copilot must preserve:
- deterministic ordering
- deterministic output
- stable formatting

Copilot may:
- improve readability
- improve grouping

---

# End of Copilot Diff Instructions
