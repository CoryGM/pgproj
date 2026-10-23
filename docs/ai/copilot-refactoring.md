# GitHub Copilot Refactoring Instructions for pgproj

See `copilot-instructions.md` §4 (Refactoring Rules) for what Copilot may and must not change
when refactoring (public signatures, record shapes, semantic/diff/SQL-output types, and the CLI
command structure are off-limits; extracting helpers, reducing duplication, improving naming and
performance without changing behavior are fine).

Additionally, for performance work specifically: Copilot may optimize LINQ queries, reduce
allocations, and improve dictionary lookups or expression-tree traversal, but must not change
ordering, diff, or SQL generation semantics while doing so — a performance fix that changes output
is a behavior change, not a performance improvement, and needs the same review as any other one.

---

# End of Copilot Refactoring Instructions
