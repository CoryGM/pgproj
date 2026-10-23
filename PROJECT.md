# pgproj — A PostgreSQL Schema Compiler, Diff Engine, and Deployment Tool  
### (A PostgreSQL equivalent to SSDT for SQL Server)

pgproj is a full PostgreSQL schema compiler designed to give Postgres developers
the same capabilities SQL Server developers get from SSDT (SQL Server Data Tools)
in Visual Studio — but using plain SQL files, modern compiler techniques, and a
cross‑platform CLI instead of proprietary project formats.

The goal of pgproj is to make PostgreSQL schema management deterministic,
repeatable, and compiler‑accurate, with a workflow similar to SSDT:

- Author schema objects in source‑controlled files  
- Build a validated semantic model  
- Compare project vs live database  
- Generate safe, deterministic migration scripts  
- Deploy changes with confidence  

pgproj brings this experience to PostgreSQL.

---

## Why pgproj exists

PostgreSQL has no SSDT equivalent.  
Developers typically rely on:

- handwritten migration scripts  
- ORMs that generate partial SQL  
- tools that diff live databases without understanding SQL deeply  
- ad‑hoc deployment processes  

None of these provide the guarantees SSDT provides for SQL Server.

pgproj fills that gap by treating SQL as a **compilable language** and database
schemas as **semantic programs**.

---

## Core Objectives

- Parse PostgreSQL SQL using PgQuery  
- Build a fully bound semantic model of the database  
- Extract deep dependencies between all objects  
- Compare two semantic models (project vs live database)  
- Produce precise added/removed/modified change sets  
- Generate deterministic SQL migration scripts  
- Run all of it as a library (`PgProj.Engine`) that any front end can call and get structured results from  
- Provide a CLI (`init`, `build`, `diff`, `publish`, `document`) for scaffolding, validation, comparison, and deployment, as the first front end over that library  

This mirrors the SSDT workflow, but for PostgreSQL.

---

## High-Level Architecture

pgproj is built as a multi-stage compiler pipeline:

1. **Parser**  
   Converts SQL into an AST using PgQuery.

2. **Semantic Model**  
   Builds a typed, validated representation of tables, columns, constraints,
   indexes, functions, views, types, and expressions.

3. **Semantic Passes**  
   Bind identifiers, resolve types, normalize expressions, and extract dependencies.

4. **Dependency Graph**  
   Orders objects and detects cycles to ensure safe deployment.

5. **Diff Engine**  
   Compares two semantic models and produces structured change sets.

6. **Deployment Engine**  
   Converts changes into SQL operations and generates migration scripts.

7. **Engine**  
   Runs the pipeline as a library (`PgProj.Engine`): reads a project's files from disk, memory, or an editor's unsaved buffers, and builds,
   compares, publishes, scaffolds, and documents. It returns structured results (statuses, diagnostics with their ranges, change sets, typed
   warnings and refusals, scripts, files' text) and never prints, writes a file, or exits.

8. **Shells**  
   The front ends over the engine. The CLI is the first and the reference: `pgproj init`, `pgproj build`, `pgproj diff`, `pgproj publish`, and
   `pgproj document` turn options into requests and results into text, files, and exit codes. An editor extension, a language server, and a web
   front end are the intended others; each calls the same engine and gets the same results ([`docs/shells.md`](docs/shells.md)).

---

## How pgproj relates to SSDT

pgproj provides PostgreSQL developers with the same benefits SSDT provides for SQL Server:

- **Schema-as-code**  
  The schema lives in source control as SQL files.

- **Compiler validation**  
  SQL is parsed and semantically validated before deployment.

- **Model comparison**  
  Project schema vs live database schema.

- **Deterministic diffs**  
  Added, removed, and modified objects are identified precisely.

- **Script generation**  
  Migration scripts are generated automatically and safely.

- **Repeatable deployments**  
  The same input always produces the same output.

The difference is that pgproj is:

- cross-platform  
- editor‑agnostic  
- built on open standards  
- designed for CI/CD  
- not tied to Visual Studio or proprietary project formats  

---

## End Goal

The end goal of pgproj is to be a **first-class PostgreSQL schema compiler** that:

- fully understands a database schema  
- detects changes with compiler-level precision  
- generates safe, deterministic migration scripts  
- integrates cleanly into CI/CD pipelines  
- can be built into editors and services, not only run from a terminal  
- provides an SSDT-like experience for PostgreSQL  

pgproj makes PostgreSQL schema management as reliable as managing source code.

