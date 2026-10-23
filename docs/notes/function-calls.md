# Function calls

Function calls (in a column default, `CHECK`, an index, or a view) are resolved against the project's own functions and PostgreSQL's built-in functions; a call that resolves to neither is reported as "references missing function". When several objects call the same missing function and it is a warning, `build`, `diff`, and `publish` write one warning for it, with how many objects call it and the first three (`4 objects reference missing function 'uuid_generate_v1': Column 'public.t.a', ...`), instead of one per object. A function an extension provides (`uuid_generate_v4()`, `crypt()`, and so on) is not individually known — there is no per-extension member list — so a call to one is still unresolved: it is an error, refusing `build`, when the project declares no extension at all (so a genuine typo in a project function's own name is caught); it is a warning, and `build` succeeds, when the project declares any extension (`CREATE EXTENSION ...`), because that call may well be one of the extension's own functions. The tradeoff of the warning case is that a real typo in a project function's own name is no longer caught once any extension is declared, since pgproj cannot tell the two apart.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
