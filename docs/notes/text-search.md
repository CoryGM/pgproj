# Text search

Text search dictionaries and configurations are modeled:

```sql
CREATE TEXT SEARCH DICTIONARY name (TEMPLATE = template, option = value, ...);
CREATE TEXT SEARCH CONFIGURATION name (PARSER = parser | COPY = configuration);
ALTER TEXT SEARCH CONFIGURATION name ADD | ALTER | DROP MAPPING FOR token_types [WITH dictionaries];
ALTER TEXT SEARCH DICTIONARY name (option = value, option);   -- an option written without a value is removed
```

## Order

They are deployed in order, whatever order the files are in: dictionaries, then the configurations that map them, then whatever names a configuration by
a string (`to_tsvector('app.my_cfg', body)`, `'app.my_cfg'::regconfig`) in an index, a default or generated column, a `CHECK`, a view, a routine body,
a trigger condition, or a policy. That last link is found by looking for a string literal equal to the name of a configuration the project declares.

## Comparing and changing

They are read back from `pg_ts_dict`, `pg_ts_config`, and `pg_ts_config_map` and compared: a dictionary by its template and options, a configuration by
its parser and, for each token type, the dictionaries tried in order.

- A dictionary's options and a configuration's mappings change in place.
- A changed template or parser is a drop and a create, which PostgreSQL refuses while anything uses the object, so `publish --execute` stops and names
  it. Create it under a new name, move what uses it, and drop the old one.
- A configuration made with `COPY` starts with the source's mappings, which are not known without the server, so only the token types the project
  states are compared (the rest of the copy is not checked). A token type the project drops from a copy is written `DROP MAPPING IF EXISTS`.
- A removed dictionary or configuration is dropped; that is not a destructive change.
- The owner (`ALTER TEXT SEARCH DICTIONARY | CONFIGURATION ... OWNER TO`) and the comment (`COMMENT ON TEXT SEARCH DICTIONARY | CONFIGURATION`) are
  modeled, and written by `init` beside the object.

## `init`

`init` writes each one to `Schema/<schema>/TextSearchDictionary/<name>.sql` and `Schema/<schema>/TextSearchConfiguration/<name>.sql`, a configuration
as its parser and one mapping per token type (never as a copy). The objects PostgreSQL ships and those an extension owns are left alone; when `schemas`
is set, only the ones in one of those schemas are managed.

## Not modeled

Text search parsers and templates (compiled code), and the `ALTER TEXT SEARCH CONFIGURATION ... REPLACE` forms, are warned about and ignored.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
