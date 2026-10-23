# Documenting a database with `pgproj document`

`pgproj document` writes the documentation of a database: a **data dictionary** (schemas, tables with their columns, keys, constraints, and indexes, views,
functions and procedures, types, and sequences) and an **entity relationship diagram**, as HTML, Markdown, and JSON. It reads either the project's SQL files or
a live database (`--source-connection`), and the result is the same: the documentation lives in the database's own comments, which both sources have.

## Writing documentation: `COMMENT ON`

A schema's own comment (`COMMENT ON SCHEMA`) is the description of the schema: it appears under the schema's heading, and the tags apply to it like any other object
(`@owner: Finance`, `@business-name: Billing`). A schema that has a comment, or that the project declares with `CREATE SCHEMA`, is documented even if it holds no objects. A table constraint's comment (`COMMENT ON CONSTRAINT name ON table`) is the description of the constraint, shown in the table's constraints, and a trigger's comment (`COMMENT ON TRIGGER name ON table`) is the description of
the trigger, shown in the table's triggers (with when it fires and what it runs); a policy's comment (`COMMENT ON POLICY name ON table`) is the description of the policy, shown in the table's row-level security
policies (with what it applies to, its roles, and its `USING` and `WITH CHECK` conditions).

Document an object with `COMMENT ON`. The text is the description. Lines that start with `@tag: value` add structure. Every tag is optional, and a plain comment
works as it is.

```sql
COMMENT ON COLUMN app.customer.email IS $$
The address receipts and password resets are sent to.

@business-name: Customer Email
@pii: true
@classification: confidential
@retention-months: 84
@example: jane@example.com
@see-also: app.customer
$$;

COMMENT ON TABLE app.customer IS $$
A person or company that buys from us.

@business-name: Customer
@owner: Sales Operations
@since-version: 2.0.0
@since-date: 2024-03-01
@see-also: app.orders
@see-also: https://wiki.example.com/customers
@team: growth
$$;

COMMENT ON TABLE app.legacy_contact IS 'Old contact details.
@deprecated: use app.customer.email';

COMMENT ON COLUMN app.audit_event.payload IS $$
@retention-date: 2031-12-31
$$;
```

Because the documentation is the comment, it is in the database after `pgproj publish`, and `psql \d+`, pgAdmin, and DBeaver show it too. Changing a comment is a
schema change like any other: it shows in `pgproj diff` and is applied by `pgproj publish`. To publish only the comments, without any other change to the database, use
`pgproj publish --comments-only` (a comment on a table or other object the database does not have yet is skipped with a warning; see [`docs/notes/comments-only.md`](docs/notes/comments-only.md)).

### The rules

- **Where a tag starts.** A tag is a line whose first non-space character is `@`, then a name, then (optionally) a colon and a value. An `@` anywhere else in a
  line is ordinary text (`help@example.com`).
- **The description** is the text before the first tag. A line of text after the tags that is not indented is added to the description too: nothing is dropped.
  An `@description:` tag is also allowed; if the comment has both, the text comes first and the tag's value follows it.
- **A value** is the rest of its line, and it continues on the indented lines after it:

  ```
  @note: first line
      second line
  ```
- **Names** are not case sensitive (`@PII`, `@pii`). `_` and `-` are the same (`@since_date` is `@since-date`).
- **Repeats.** `@example` and `@see-also` may be given several times. Any other tag given twice uses the last value and is reported as a warning.
- **Custom tags.** A tag that is not in the table below is kept and shown with the object as an extra attribute (JSON: `custom`), so teams can add their own
  (`@team: growth`, `@system-of-record: crm`). A custom tag may be repeated. A bare custom tag (`@flag`) is shown as `yes`.

### The starter set

| Tag | Value | Repeatable | JSON property |
|---|---|---|---|
| `@description` | Free text (Markdown passes through in the Markdown output) | no | `description` |
| `@business-name` | The name business users know the object by | no | `businessName` |
| `@owner` | The steward: who is responsible for the meaning of the data (not the database owner) | no | `owner` |
| `@classification` | Text, in whatever data classification scheme you use | no | `classification` |
| `@pii` | `true` or `false` (`yes` and `no` work too) | no | `pii` |
| `@deprecated` | Alone, or with the reason or the replacement. `@deprecated: false` is the same as leaving it out | no | `deprecated` (`{ "flag": true, "reason": "..." }`) |
| `@since-version` | Text, such as `2.4.0` | no | `sinceVersion` |
| `@since-date` | An ISO 8601 date (`2026-10-02`) or date and time (`2026-10-02T14:03:00Z`, any offset) | no | `sinceDate` |
| `@retention-months` | How many months the data is kept: a whole number above zero | no | `retentionMonths` |
| `@retention-date` | The UTC date (`2030-01-01`, read as that day in UTC) or time ending in `Z` (or `+00:00`) the data is kept until | no | `retentionDate` |
| `@example` | A sample value or usage | yes | `examples` (array) |
| `@see-also` | An object (`app.orders`, `app.customer.email`) or a URL. An object that is in the document becomes a link | yes | `seeAlso` (array of `{ "value", "target" }`) |

`@retention-months` and `@retention-date` are exclusive: an object cannot have both.

### Which tags apply to which objects

Some tags make no sense on some objects, so the starter set is not the same for every kind. `classification`, `pii`, and retention describe data, so they are for the objects that hold or group it;
an `@example` is for something that has a value or a use. The descriptive tags apply to everything.

| Tag | Schema | Table / view | Column | Constraint | Trigger | Policy | Index | Function / procedure | Type | Sequence |
|---|---|---|---|---|---|---|---|---|---|---|
| `description`, `business-name`, `owner`, `see-also`, `deprecated`, `since-version`, `since-date` | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● |
| `classification` | ● | ● | ● | — | — | — | — | — | — | — |
| `pii` | — | ● | ● | — | — | — | — | — | — | — |
| `retention-months`, `retention-date` | ● | ● | ● | — | — | — | — | — | — | — |
| `example` | — | — | ● | ● | — | — | — | ● | ● | — |

A starter-set tag on an object it does not apply to is reported as a **warning** (`@pii does not apply to a constraint ...`), listed with the other problems, and still shown as written. A warning does not
change the exit code. Custom tags are never checked, so a team can use any tag it likes on any object.

### Mistakes in a tag

A tag whose value cannot be read does not stop the document. The object shows what was written, and the problem is listed in a summary (and in the
`problems` array of the JSON):

- a `@since-date` or `@retention-date` that is not ISO 8601, or a `@retention-date` with an offset other than UTC;
- a `@retention-months` that is not a whole number above zero;
- a `@pii` that is not true, false, yes, or no;
- both `@retention-months` and `@retention-date` (both are kept as written and neither is read);

These are **errors**: the files are written, the errors are listed, and the command exits with **1**. Pass `--no-fail-on-doc-errors` to exit with 0 anyway (the errors
are still listed). A tag given twice, or a tag on an object it does not apply to (see above), is only a **warning**.

## Running it

```
pgproj document                                    # the project's files; html and json into docs/
pgproj document --format html,markdown,json        # any combination of the three
pgproj document --source-connection "Host=localhost;Database=app" --output ./docs
pgproj document --schemas billing --output ./docs/billing
pgproj document --existing overwrite --no-fail-on-doc-errors
```

| Option | |
|---|---|
| `--project <path>` | The project to document (default `pgproj.json`). Not read with `--source-connection`. |
| `--source-connection [<string>]` | Document a live database instead (the source). With no value it reads `PGPROJ_SOURCE_CONNECTION`, then `sourceConnectionString` from `pgproj.json`; the variable and the file alone do not switch `document` to a database. Grants, role memberships, and owners are not read. |
| `--format <list>` | `html`, `markdown` (or `md`), `json`, as a comma-separated list or the option repeated. Default: html and json. |
| `--output <dir>` | The directory for the files. Relative to the directory of `pgproj.json` (to the current directory with `--source-connection`). Default `docs`. |
| `--existing overwrite\|backup\|keep` | What to do with a file already there; see below. |
| `--schemas <list>` / `--exclude-schemas <list>` | Document only, or all but, these schemas (the diagram follows). Not together; `public` is only included if named; an unknown name is an error. For a document of each schema, run the command once per schema. |
| `--no-fail-on-doc-errors` | Exit with 0 even when tags have errors. |
| `--verbose` / `--quiet` | Also write each step, or only errors. |

The files are `database-documentation.html`, `database-documentation.md`, and `database-documentation.json`.

### Files that are already there

A file `document` wrote carries a `Generated by pgproj document` line near its top (an HTML comment in the `.html` and `.md`, the `generator` property in the JSON).
By default such a file is **backed up** to `database-documentation.<yyyy-MM-dd-HH-mm-ss>.<ext>` (UTC) and the new one takes its place; a file **without** that line (one a
person wrote) is **kept**, the documentation is written to the stamped name instead, and a line says so even with `--quiet`. All the files are handled together, with one stamp.
`--existing overwrite` replaces them, `backup` backs them up whatever they are, and `keep` writes the stamped files and leaves the existing ones alone. Pruning old backups is up to
you.

## The formats

- **HTML** is one page: a contents list beside the data dictionary, the summary, the problems, and the diagram. It carries its own styles and follows the light or dark setting of the
  browser. The diagram is drawn by Mermaid, which the page loads from a CDN, so the page needs internet access to show it; everything else works offline. Everything in a comment is
  HTML-encoded.
- **Markdown** is one file with a table of contents, the summary, the problems, the diagram in a `mermaid` block (GitHub, GitLab, and VS Code's preview draw it), and the dictionary.
- **JSON** is the canonical form: `schemaVersion` (currently 1), `generator`, `generatedUtc`, `source`, `counts`, `schemas`, `erd` (the Mermaid text), and `problems`. Each object has `name`,
  `qualifiedName`, `description`, its structure, and the tags it was given (tags not written are left out).

## What is documented, and what is not

Documented: tables (and partitions), their columns (type, nullability, default, generated and identity columns, key markers, what a foreign key points at), constraints, triggers (when they fire and what they run), row-level security policies (what they apply to and their conditions), indexes,
views and materialized views (columns, each with its comment, and definition), functions and procedures (signature, return type, language, volatility), types (enum labels, composite fields with their comments, domain base types),
and sequences that are not owned by an identity column. In a project with tenants, the tenant template is documented once, under its stand-in schema
(the prefix; [Schema-per-tenant databases](docs/notes/tenants.md)). Not documented in this version:

- a view's columns from the project's files, when they cannot be worked out from its query: a database lists a view's columns from its catalog, but a project has no server, so they are derived from the query (a column's name, an alias, the name PostgreSQL gives a call or a cast, `*` expanded from the project's own tables and views, the first leg of a `UNION`, a `CREATE VIEW v (a, b)` list). A `*` over a table the project does not define, or over a subquery or function, leaves the list empty rather than wrong. A column's type is shown only for a plain column of a project table or view, and as the project wrote it (`pg_catalog.int4`), where a database gives the catalog's (`integer`);
- comments on **event triggers**, **rules**, and a **domain's** constraints (pgproj does not model those comments yet), so they are neither shown nor read for tags;
- cluster-wide objects (roles, tablespaces, subscriptions), event triggers, rules, and extensions;
- views in the diagram (it draws tables, as `--erd` does);
- documentation held outside the comments (a separate file for an overview or a glossary).
