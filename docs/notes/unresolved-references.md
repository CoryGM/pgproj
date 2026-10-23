# References build cannot resolve

A table/type/function/column reference `build` cannot resolve anywhere in the project ("references missing table/type/function/column") is an error, refusing `build`, unless it is schema-qualified to a schema outside the project's `schemas` list (see [`pgproj.json.md`](../../pgproj.json.md)) — that case is a warning instead, since the reference most likely belongs to a schema another project owns rather than a typo. When `schemas` is empty or omitted (the default), there is no explicit boundary to check a reference's schema against at all, so *every* otherwise-unresolvable reference is a warning; populating `schemas` makes checking strict again for the schemas actually listed.

A table's `LIKE` source is one of these references. Since the table's columns come from it, `diff` and `publish` read such a source from the database, so the table is compared with the columns PostgreSQL gave it ([tables written with `LIKE`](table-like.md)).

A name in a PL/pgSQL body that the project does not define is only ever a warning: [PL/pgSQL bodies](plpgsql-bodies.md).

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
