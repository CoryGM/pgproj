# How `schemas` scopes `diff` and `publish`

`diff` and `publish` also use `schemas`, for a different purpose: when it is populated, only those schemas (plus, automatically, every schema this project's own SQL declares an object in) are read from the connected database and compared at all — an object in a schema outside that set belongs to another project sharing the same database and is invisible to `diff`/`publish` entirely, never modeled and never proposed for removal. An extension the project declares is the exception: it is compared wherever it is, since an extension is named by its name alone ([Extensions](extensions.md)). When `schemas` is empty or omitted, every schema the database exposes is read, unchanged from every earlier version of pgproj. `pgproj init` always reads the whole database regardless, since a brand-new project has no `schemas` list yet.

In [source mode](promoting-environments.md) (`--source-connection` on `diff` or `publish`), `schemas` scopes **both** databases: the same schemas are read from the source and from the target, so a database shared with other projects is left alone at either end.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
