# Table inheritance

Table inheritance: `CREATE TABLE child (...) INHERITS (parent [, ...])` is deployed with its `INHERITS` clause, after its parents, and read back from `pg_inherits`. A child has its parents' columns as well as its own, as PostgreSQL gives them: the parents' first, in the order the parents are named, a name more than one parent has once. A column the child declares with a parent's name is PostgreSQL's merge of the two: the child's declaration, with the parent's default when it states none, and `NOT NULL` when either has it. A parent column's comment and identity are not the child's. `CREATE TABLE` writes only the child's own columns, and `init` scaffolds a child the same way, so a column the child only inherits is written once, in its parent's file.

The parents are compared as a set; the order they are named in is not a difference. A parent added is `ALTER TABLE child INHERIT parent`, after the child is given the parent's columns (with `ADD COLUMN`); a parent removed is `ALTER TABLE child NO INHERIT parent`, after which the columns the child no longer states are dropped, which `publish --execute` treats as destructive. A column added to, dropped from, or changed on a parent is changed on the parent alone: PostgreSQL carries it to every child, so the child gets no statement of its own. A chain of parents (a grandchild) is followed.

A `CHECK` constraint reaches the children as PostgreSQL gives it, and is the parent's (only a child's own constraints are its). `CHECK (...) NO INHERIT` keeps a check on the table alone: it is deployed, read back, and compared, and a change to it drops and adds the constraint. A parent named with a dot in its schema or table name is not supported. A partition (`PARTITION OF`) is not inheritance here; see [Storage, partitioning, and comments](storage-partitioning-and-comments.md).

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)

A child that lists a generated column it inherits, without an expression of its own (`CREATE TABLE child (a int, b int) INHERITS (parent)`, how `pg_dump` writes it), has the parent's generation. It is not written again for the child, which PostgreSQL 14 and 15 refuse (`42611: child column "b" specifies generation expression`), and `init` reads such a column back the same way when its expression is its parent's. A child that states its own expression (PostgreSQL 16 and later) keeps it as written.
