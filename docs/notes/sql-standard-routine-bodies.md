# SQL-standard routine bodies

SQL-standard routine bodies: a function or procedure written with `BEGIN ATOMIC ... END` or `RETURN expression` (PostgreSQL 14+) is created with its body as written. The semicolons inside a `BEGIN ATOMIC` body don't end the statement. PostgreSQL stores such a body parsed and prints it back qualified, with casts and aliases added. So one that already exists in the database is compared the same way as a view: it is created as a temporary routine of the same name and parameters, and PostgreSQL's rendering of the two is compared. Unlike a string body, PostgreSQL records what such a body uses. A column change or routine rebuild it is in the way of therefore drops and recreates it, like a view.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
