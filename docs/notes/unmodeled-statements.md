# Statements that are not modeled

A statement pgproj does not model is reported by `build` (and `diff` and `publish`) as a warning and ignored, quoted with its file, line, and column and where it ends, for
example `sql/search.sql:3:1-4:30: [Warning] CREATE TEXT SEARCH TEMPLATE is not modeled; the statement is ignored: CREATE TEXT SEARCH TEMPLATE ...`. Nothing it
says is deployed, and the database's objects of those kinds are not read, compared, or scaffolded by `init`.

A password in the quoted statement is masked as `*****`: `password=...` in a connection string, `PASSWORD '...'` and `password '...'` in a role,
server, or user mapping, and the password of a `scheme://user:password@host` URL. This is pattern matching on the common shapes, so a secret written some
other way (a value in an `INSERT`, a function body) is still shown. A syntax error in such a statement masks the token the parser stopped at when it follows
a name like `PASSWORD`.

## What is reported

Each warning names the statement as the first column says.

| Statements | Why |
|---|---|
| `CREATE ROLE`, `ALTER ROLE`, `ALTER ROLE ... SET`, `DROP ROLE`, `REASSIGN OWNED`, `DROP OWNED`, `CREATE TABLESPACE`, `CREATE DATABASE`, `ALTER DATABASE`, `ALTER DATABASE ... SET`, `ALTER SYSTEM`, `CREATE SUBSCRIPTION`, `ALTER SUBSCRIPTION` | Cluster-wide, or settings of the database or server rather than objects in it. A grant, membership, or owner naming a role, and a table's `TABLESPACE`, are modeled; `publish` checks that the role or tablespace exists ([roles](roles.md), [tablespaces](tablespaces.md)). |
| `CREATE FOREIGN DATA WRAPPER`, `ALTER FOREIGN DATA WRAPPER`, `CREATE SERVER`, `ALTER SERVER`, `CREATE USER MAPPING`, `ALTER USER MAPPING`, `CREATE FOREIGN TABLE`, `IMPORT FOREIGN SCHEMA` | Foreign data is not modeled (deferred). |
| `CREATE OPERATOR CLASS`, `CREATE OPERATOR FAMILY`, `ALTER OPERATOR FAMILY`, `CREATE ACCESS METHOD`, `CREATE TRANSFORM`, `CREATE LANGUAGE`, `CREATE CONVERSION`, `CREATE TEXT SEARCH PARSER`, `CREATE TEXT SEARCH TEMPLATE`, `SECURITY LABEL` | Kinds of object pgproj does not model. An index or key may name an operator class; a routine may use any language that exists. |
| `CREATE TYPE (base type)`, `CREATE TYPE (shell type)` | A base type needs C functions and a superuser; it is left out with the functions it names ([base types](base-types.md)). |
| `CREATE COLLATION ... FROM` | Its attributes come from another collation, often the server's; a collation made with options is modeled ([collations](collations.md)). |
| `ALTER SEQUENCE` | Except `OWNED BY`: state a sequence's options in `CREATE SEQUENCE`. |
| `ALTER DOMAIN` | Except `ADD CONSTRAINT ... CHECK [NOT VALID]`: state the domain in `CREATE DOMAIN`. |
| `ALTER VIEW`, `ALTER MATERIALIZED VIEW`, `ALTER INDEX`, and `ALTER TABLE` naming another kind | Except an owner, a view column's `SET`/`DROP DEFAULT`, and `ALTER INDEX ... ATTACH PARTITION`. An `ALTER TABLE` on a table is read per action ([`ALTER TABLE`](alter-table.md)). |
| `ALTER TYPE ... VALUE`, `ALTER TYPE ... SET`, `ALTER FUNCTION`, `ALTER PROCEDURE`, `ALTER POLICY`, `ALTER OPERATOR`, `ALTER COLLATION`, `ALTER STATISTICS`, `ALTER EXTENSION`, `ALTER EXTENSION ... ADD/DROP`, `ALTER TEXT SEARCH CONFIGURATION ... REPLACE` | A project states each object's final form in its `CREATE`; pgproj writes the `ALTER` itself when the two differ. `ALTER PUBLICATION`, `ALTER EVENT TRIGGER`, `ALTER TEXT SEARCH DICTIONARY`, and the mapping forms of `ALTER TEXT SEARCH CONFIGURATION` are modeled. |
| `ALTER ... RENAME`, `ALTER ... SET SCHEMA`, `ALTER ... DEPENDS ON EXTENSION` | A rename is written as the new name; a column rename is declared with `-- @renamed-from` ([renaming columns](../renaming-columns.md)). |
| `ALTER ... OWNER TO` | For a kind whose owner is not modeled (a foreign data wrapper, a language, an index, which follows its table), or an owner that is not a named role (`CURRENT_USER`); see [owners](owners.md). |
| `CREATE SCHEMA ... AUTHORIZATION` | Only when the owner is not a named role (`CURRENT_USER`); the schema is still created, without that owner. |
| `COMMENT ON` an event trigger, a rule, an operator, an aggregate, a cast, a publication, a domain's constraint (`CONSTRAINT ... ON DOMAIN`), or another kind not listed under [comments](storage-partitioning-and-comments.md) | Those comments are not modeled. |
| `CREATE TEMPORARY TABLE`, `VIEW`, and `SEQUENCE` | A temporary object lasts only for the session that creates it. |
| `SET search_path`, `RESET search_path` | Unqualified names follow the project's `searchPath`, not the file. |
| `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `TRUNCATE`, `COPY`, `SELECT`, `SELECT INTO`, `CREATE TABLE AS`, `DROP`, `DO`, `CALL`, `REFRESH MATERIALIZED VIEW` | They change data or run code, which `publish` never does from a schema file; the warning says to put them in the `predeploy` or `postdeploy` script. |
| `VACUUM/ANALYZE`, `CLUSTER`, `REINDEX`, `LOCK`, `NOTIFY`, `LISTEN`, `UNLISTEN`, `CHECKPOINT`, `DISCARD`, `PREPARE`, `EXECUTE`, `DEALLOCATE`, `EXPLAIN`, `SHOW`, `LOAD` | Maintenance and session statements: nothing to deploy. |

Accepted without a warning: transaction control (`BEGIN`, `COMMIT`), `SET` and `RESET` of anything but `search_path`, and `CREATE SCHEMA` (which
declares the schema, [schemas](schemas.md)).

`init` reads none of these kinds from a database either; it lists them in its help and after it runs (see the [command reference](../commands.md#init)).

## A clause of a statement that is modeled

A statement pgproj models can still carry a clause it does not read. `build` warns about it, with the statement's file, line, and column and where it ends, instead of dropping it without a word:

```
sql/schema.sql:2:1-2:45: [Warning] CREATE TABLE: the ON COMMIT clause is not modeled; it is ignored. CREATE TABLE t (a int) ON COMMIT DROP
```

The check covers the statements and clauses pgproj models for tables and their columns, constraints, indexes, views, triggers, routines, sequences, types, policies, statistics, rules, schemas, publications, event triggers, extensions, and casts. It is a list of the clauses pgproj reads and of those it ignores on purpose (for example `IF NOT EXISTS`, which makes no difference to the object a project declares); a clause on neither list is reported, which includes a clause a newer PostgreSQL version adds. Each warning is a clause whose loss `publish` and `diff` cannot show, because the database matches what was deployed. The list is in `ClauseCoverage.cs`, with the reason beside each clause ignored.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
