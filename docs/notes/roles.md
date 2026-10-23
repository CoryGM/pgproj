# Roles

Roles: a role is cluster-wide, so pgproj reports roles and does not model them. `CREATE ROLE`, `ALTER ROLE`, and `DROP ROLE` in a
project are reported as not modeled and never run (role memberships, `GRANT role TO member`, are modeled). `publish --execute`
first asks the server for its roles and refuses, before running anything, when a grant, a role membership, a default privilege
(`FOR ROLE`), or an owner (`OWNER TO`) names one it does not have, listing each role and what names it; `--dry-run` and `--output` print the script with a
warning. `PUBLIC` is not a role. A role that the project's pre-deploy script creates is not reported: the script has to contain a
`CREATE ROLE`, `CREATE USER`, or `CREATE GROUP` and name the role (a guarded `DO` block, for example
`DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'app') THEN CREATE ROLE app; END IF; END $$;`; comments do not count). Otherwise create the role on the server first.

---

[All notes](README.md) · [Commands](../commands.md) · [README](../../README.md)
