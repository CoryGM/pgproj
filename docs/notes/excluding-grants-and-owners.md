# Excluding grants, role memberships, and owners

`pgproj.json`'s `excludeGrants`/`excludeRoleMemberships` (overridable per run with `--exclude-grants`/`--exclude-role-memberships`; `excludeOwners` and `--exclude-owners` do the same for owners) let a project opt out of modeling grants and/or role memberships entirely — for a role that doesn't exist yet in a fresh/CI database, or a live database's grants owned by a separate access-control process. An excluded category is invisible on both the project side and the live side, so it is never a phantom "Removed" and never something `--allow-destructive` needs to gate.

In [source mode](promoting-environments.md) the options and the project file settings apply to **both** databases (the source and the target), which is why a promotion between environments with different roles usually passes `--exclude-owners`, `--exclude-grants`, and `--exclude-role-memberships`.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
