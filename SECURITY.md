# Security

## Reporting a vulnerability

Please report a security problem privately, not in a public issue: use **[Report a vulnerability](https://github.com/CoryGM/pgproj/security/advisories/new)**
on the repository's Security tab (GitHub's private vulnerability reporting). Only the maintainer sees the report, and the fix can be prepared and
released before the problem is made public.

Include what you can of:

- what an attacker could do, and what they would need (access to a project's files, a connection string, a database);
- the steps or the smallest project that shows it;
- the output of `pgproj about`, which prints no password (remove host names and paths if they are private).

**Never send a real password, connection string, or data from a real database.** Use a throwaway database and made-up values.

pgproj is maintained by one person. You will hear back on the report, what is planned, and when a fix ships. A fix goes into a new release; there are no backports to older versions.

## Supported versions

Only the latest release is supported. pgproj is before 1.0, so a fix ships in the next release, and you upgrade to get it
(`dotnet tool update --global pgproj`, `apt upgrade`, `dnf upgrade`, or a new download).

## What counts

pgproj reads SQL files, connects to databases with the credentials it is given, and, with `publish --execute`, runs DDL. A vulnerability is pgproj
doing something you did not ask for, for example:

- printing, writing, or sending a password or connection string it was given (pgproj masks them in everything it prints and returns, except the
  deployment script);
- running SQL on a database that was not in the script it showed, or that a `--dry-run` or `diff` would not have run;
- building SQL from a name in a project or a database in a way that lets that name run as SQL of its own;
- a release file, package, or repository that does not match what was built from this repository (each release has signed checksums and
  build provenance; [`README.md`](README.md#installing) says how to check them).

By design, and not a vulnerability: `publish --execute` runs the deployment script and the project's pre- and post-deployment scripts on the target
database, as written, with the role in the connection string. Treat a project's files, and anyone who can change them, with the same trust as that
role. The deployment script itself is never masked, because masking it would change what is deployed.
