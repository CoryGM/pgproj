# Order

Order: a publish emits what it creates in the order the project declares it: a table's columns, a composite type's fields, a `UNIQUE` constraint's columns (which PostgreSQL also uses as its index's column order), a table's constraints and indexes (where nothing else decides their order, such as a foreign key waiting for the key it references), a grant's privileges and columns, and storage options. An order-only difference is not a change, so reordering these in the source publishes nothing; order that is itself the meaning (index key columns, routine parameters, enum labels) is compared as a change.

## What an object is created after

Beyond the order the project declares, an object is created after what PostgreSQL looks up when it is created, whatever order the files are in:

- a **view** after the types its body names (a cast, a `JSON_TABLE` column), the sequences it reads as a table (`SELECT last_value FROM seq`), and, when it has a `GROUP BY`, the primary keys of the tables it reads (PostgreSQL checks that the other columns depend functionally on them);
- a **domain** after the sequence its default names and the project function it calls;
- a **PL/pgSQL routine** after the types and tables its `DECLARE` section names (`v mytype`, `r tbl%ROWTYPE`, `c tbl.col%TYPE`);
  (What the rest of its body names is not a dependency: [PL/pgSQL bodies](plpgsql-bodies.md).)
- a **partitioned table** after the project functions its `PARTITION BY` expressions call;
- a **foreign key to a partition** after the key the partition inherits from its parent;
- a **foreign key to a partitioned table** after the foreign key of the same table and the same name stem with the next lower number (`fk_a_fkey`, then `fk_a_fkey4`, then `fk_a_fkey12`): on PostgreSQL 16 and 17 the foreign key also makes one constraint per partition, named with the first free number, so a scaffolded project's numbered names only fit if they are created in the order of their numbers;
- a **publication** `FOR TABLES IN SCHEMA s` after `s`, which is created (`CREATE SCHEMA IF NOT EXISTS`) even if nothing else of the project is in it.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
