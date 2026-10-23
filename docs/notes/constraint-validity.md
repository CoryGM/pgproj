# NOT VALID constraints

A `CHECK` or `FOREIGN KEY` constraint can be added `NOT VALID`: PostgreSQL enforces it for new and changed rows, and does not check the rows already in the table until `VALIDATE CONSTRAINT`. It is how a constraint is added to a large table, or to one whose old rows break it.

- **A table's `CHECK` or `FOREIGN KEY`** written `NOT VALID` (`ALTER TABLE t ADD CONSTRAINT c CHECK (...) NOT VALID`, or in `CREATE TABLE`) is added `NOT VALID`. pgproj adds every constraint with its own `ALTER TABLE ... ADD CONSTRAINT`, so this holds in `CREATE TABLE` too, where PostgreSQL itself would validate it against the empty table. A `NOT ENFORCED` constraint (PostgreSQL 18) is never `NOT VALID`.
- **A domain's `CHECK`** can only be written `NOT VALID` with `ALTER DOMAIN d ADD CONSTRAINT c CHECK (...) NOT VALID`, since `CREATE DOMAIN` has no such clause. pgproj reads that statement (it is the one form of `ALTER DOMAIN` it models; the others are reported and ignored), and deploys the check as `ALTER DOMAIN ... ADD CONSTRAINT ... NOT VALID` after the domain. A valid `ALTER DOMAIN ... ADD CONSTRAINT ... CHECK` is the same as a check written in `CREATE DOMAIN`.
- **`init`** reads `convalidated` and writes a `NOT VALID` table constraint as it is, and a domain's as `ALTER DOMAIN ... ADD CONSTRAINT ... NOT VALID` after the `CREATE DOMAIN`.

## Comparing

The comparison goes one way. A constraint the database has not validated that the project states valid is a change, and `publish` runs `VALIDATE CONSTRAINT` in place, without dropping it; if the rows still break it, the database refuses and the transaction rolls back. A constraint the database has validated that the project states `NOT VALID` is **not** a change: validation cannot be undone, and the project's statement is the step that adds it. A project can keep `NOT VALID` in its files, and a database that was validated by hand converges.

A changed expression is a drop and an add, as for any constraint, and the new one keeps what the project states (`NOT VALID` or not). A `CHECK` put back around the drop of a function it uses is added `NOT VALID` and then validated, unless the project states it `NOT VALID`.

Not modeled: `COMMENT ON CONSTRAINT ... ON DOMAIN` (a domain constraint's comment).
