**Looking for testers: pgproj, a compiler for PostgreSQL schemas**

If you've used SQL Server Data Tools, you know the model: your schema lives in source control as plain `CREATE` statements, and the tool works out how
to bring a database up to date. When I moved to PostgreSQL, I couldn't find an equivalent, so I built one.

pgproj 0.2.0 is out today. It's free and open source (Apache 2.0).

**It's a compiler, not just a diff tool.** pgproj treats your schema as source code. It parses every file with PostgreSQL's own parser, the server's
actual grammar compiled into the tool rather than a third-party SQL parser, builds a model of the whole schema, and resolves every reference between
objects. A missing table, a misspelled type, or a broken function call is reported at its file, line, and column before anything touches a database.
Only then does it compare and plan a script, in dependency order.

- `pgproj init` turns an existing database into a project, one SQL file per object.
- `pgproj build` compiles the project and reports its errors and warnings, like any other build.
- `pgproj diff` shows what differs between your project and a live database.
- `pgproj publish` writes the script to bring the database up to date, and can run it in a single transaction. It refuses anything that would lose
  data unless you explicitly allow it.
- `pgproj document` writes a data dictionary and an entity relationship diagram as HTML, Markdown, and JSON.

**It's a focused tool.** pgproj does one job: keeping a PostgreSQL schema in source control and deploying it. It isn't a migration framework, a
database admin console, a data-migration tool, or an ORM, and it isn't trying to become one. If your schema belongs in files that get reviewed like
code, it's for you. If you need something else, there are good tools for that already.

**Even if you never change how you deploy,** `document` is worth a look. Point it at any PostgreSQL database and you get browsable documentation of
its tables, columns, views, and functions, plus a diagram. The descriptions come from the `COMMENT ON` text already in your database, and optional tags
like `@pii`, `@owner`, and `@classification` turn those comments into a lightweight data catalog that lives with the schema.

**Who I'm looking for:**

- .NET developers and teams on PostgreSQL, especially if you're coming from SSDT or Visual Studio database projects.
- Teams who want their schema reviewed and built like code, rather than tracked as a growing pile of migration scripts.
- Anyone who needs to document a PostgreSQL database and keep that documentation current.

**How to help:**

1. Install it with `dotnet tool install --global pgproj`. There are also installers for Windows, macOS, and Linux (apt/dnf) that need no .NET at all.
2. Point `pgproj init` at a copy of a real database (please not production), then run `build` and `diff`. Or just run
   `pgproj document --source-connection "..."` and see what you get.
3. Found something? Open a GitHub issue and paste the output of `pgproj about`: it shows your version, platform, and PostgreSQL version, and never
   prints a password. Questions and ideas are welcome in Discussions.

👉 https://github.com/CoryGM/pgproj

#PostgreSQL #dotnet #DevOps #OpenSource
