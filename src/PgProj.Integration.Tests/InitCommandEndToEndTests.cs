using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-82: runs the real <c>pgproj init</c> command against a disposable database with a representative mix of
/// object kinds (tables with constraints/indexes/triggers, a view, a function, a sequence, an enum type, an
/// extension, and grants), then proves the scaffolded project is not just files on disk but a faithful,
/// pgproj-buildable, pgproj-diffable representation of that same database.
/// </summary>
[TestClass]
public sealed class InitCommandEndToEndTests
{
    private const string Sql = """
        CREATE EXTENSION citext SCHEMA public;

        CREATE TYPE public.mood AS ENUM ('happy', 'sad');

        CREATE TABLE public.customers (
            id bigint PRIMARY KEY,
            email public.citext NOT NULL UNIQUE,
            mood public.mood
        );

        CREATE INDEX customers_mood_idx ON public.customers (mood);

        CREATE VIEW public.happy_customers AS SELECT id, email FROM public.customers WHERE mood = 'happy';

        CREATE FUNCTION public.customer_count() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM public.customers; $$;

        CREATE SEQUENCE public.order_numbers START 1000;

        CREATE TABLE public.orders (
            id bigint PRIMARY KEY DEFAULT nextval('public.order_numbers'),
            customer_id bigint NOT NULL REFERENCES public.customers (id),
            total numeric(10,2) NOT NULL
        );

        CREATE POLICY orders_owner_only ON public.orders USING (customer_id = current_setting('app.customer_id')::bigint);
        ALTER TABLE public.orders ENABLE ROW LEVEL SECURITY;

        GRANT SELECT ON public.customers TO PUBLIC;
        """;

    [TestMethod]
    public async Task Init_ScaffoldsABuildableProjectThatDiffsEmptyAgainstTheSameDatabase()
    {
        var databaseName = $"g82_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g82-" + Guid.NewGuid().ToString("N"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(Sql, connection);
                await command.ExecuteNonQueryAsync();
            }

            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var initResult = await RunAsync("init", "--project", projectFile, "--output", Path.Combine(projectDirectory, "sql"), "--source-connection", connectionString);
            Assert.AreEqual(0, initResult.ExitCode, "init failed.\n" + initResult.Output);

            // A run says what it cannot read, as the help does (G-152): roles and foreign data wrappers stand for the kinds that were once left out unmentioned.
            StringAssert.Contains(initResult.Output, "Not read by pgproj, so absent from the scaffolded project");
            StringAssert.Contains(initResult.Output, "roles and their settings (role memberships are scaffolded)");
            StringAssert.Contains(initResult.Output, "foreign data wrappers");

            // The scaffolded files land where the layout promises: one file per object, grouped like samples/MultiSchema.
            Assert.IsTrue(File.Exists(Path.Combine(projectDirectory, "sql", "Schema", "public", "public.sql")));
            Assert.IsTrue(File.Exists(Path.Combine(projectDirectory, "sql", "Schema", "public", "Table", "customers.sql")));
            Assert.IsTrue(File.Exists(Path.Combine(projectDirectory, "sql", "Schema", "public", "Table", "orders.sql")));
            Assert.IsTrue(File.Exists(Path.Combine(projectDirectory, "sql", "Schema", "public", "View", "happy_customers.sql")));
            Assert.IsTrue(File.Exists(Path.Combine(projectDirectory, "sql", "Schema", "public", "Function", "customer_count.sql")));
            Assert.IsTrue(File.Exists(Path.Combine(projectDirectory, "sql", "Schema", "public", "Sequence", "order_numbers.sql")));
            Assert.IsTrue(File.Exists(Path.Combine(projectDirectory, "sql", "Schema", "public", "Type", "mood.sql")));
            Assert.IsTrue(File.Exists(Path.Combine(projectDirectory, "sql", "Extension", "citext.sql")));

            var customersFile = await File.ReadAllTextAsync(Path.Combine(projectDirectory, "sql", "Schema", "public", "Table", "customers.sql"));
            StringAssert.Contains(customersFile, "customers_mood_idx");
            StringAssert.Contains(customersFile, "GRANT SELECT ON TABLE public.customers TO PUBLIC");

            // G-76: a row-level security policy and the table's own ENABLE ROW LEVEL SECURITY live in the owning
            // table's file, the same convention already used for its constraints, indexes, and grants.
            var ordersFile = await File.ReadAllTextAsync(Path.Combine(projectDirectory, "sql", "Schema", "public", "Table", "orders.sql"));
            StringAssert.Contains(ordersFile, "CREATE POLICY orders_owner_only");
            StringAssert.Contains(ordersFile, "ENABLE ROW LEVEL SECURITY");

            // The generated project file has the connection string with the password redacted (there is none here,
            // but --source-connection still round-trips through RedactPassword unchanged) and points at the output folder.
            var projectJson = await File.ReadAllTextAsync(projectFile);
            StringAssert.Contains(projectJson, "\"source\"");

            // The scaffolded project builds clean...
            var buildResult = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, buildResult.ExitCode, "build of the scaffolded project failed.\n" + buildResult.Output);
            Assert.IsFalse(buildResult.Output.Contains("[Error]", StringComparison.Ordinal), buildResult.Output);

            // ...and, the strongest proof of fidelity, diffs empty against the exact database it was scaffolded from.
            var diffResult = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, diffResult.ExitCode, "diff failed.\n" + diffResult.Output);
            StringAssert.Contains(diffResult.Output, "Added: 0");
            StringAssert.Contains(diffResult.Output, "Removed: 0");
            StringAssert.Contains(diffResult.Output, "Modified: 0");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            try
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }

    [TestMethod]
    public async Task Init_RefusesToOverwriteWithoutForce_ThenSucceedsWithForce()
    {
        var databaseName = $"g82force_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g82force-" + Guid.NewGuid().ToString("N"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("CREATE TABLE public.t (id bigint PRIMARY KEY);", connection);
                await command.ExecuteNonQueryAsync();
            }

            var projectFile = Path.Combine(projectDirectory, "pgproj.json");

            var first = await RunAsync("init", "--project", projectFile, "--source-connection", connectionString);
            Assert.AreEqual(0, first.ExitCode, "First init failed.\n" + first.Output);

            var second = await RunAsync("init", "--project", projectFile, "--source-connection", connectionString);
            Assert.AreEqual(1, second.ExitCode, "Second init without --force should have been refused.\n" + second.Output);

            var third = await RunAsync("init", "--project", projectFile, "--source-connection", connectionString, "--force");
            Assert.AreEqual(0, third.ExitCode, "Third init with --force failed.\n" + third.Output);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            try
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }

    [TestMethod]
    public async Task Init_CreatesTheOutputDirectoryIncludingMissingParents_EvenForAnEmptyDatabase()
    {
        var databaseName = $"g82empty_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g82empty-" + Guid.NewGuid().ToString("N"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            // Several levels deep and not yet created, to prove -o creates the whole chain, not just the leaf.
            var outputDirectory = Path.Combine(projectDirectory, "generated", "nested", "sql");

            var result = await RunAsync("init", "--project", projectFile, "-o", outputDirectory, "--source-connection", connectionString);

            Assert.AreEqual(0, result.ExitCode, "init against an empty database failed.\n" + result.Output);
            Assert.IsTrue(Directory.Exists(outputDirectory), "Output directory (and its missing parents) should have been created even though there was nothing to scaffold.");

            // The generated pgproj.json's source must resolve to an existing directory (PgProjectLoader requires
            // this), so build must succeed against it too, even with zero SQL files.
            var buildResult = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, buildResult.ExitCode, "build against the empty scaffolded project failed.\n" + buildResult.Output);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            try
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }

    /// <summary>
    /// G-87: pgproj init --exclude-grants/--exclude-role-memberships (1) doesn't scaffold that category's SQL at
    /// all, and (2) writes the matching excludeGrants/excludeRoleMemberships: true into the generated pgproj.json
    /// so a later diff/publish against the same live database - which still has both, since init reads a database,
    /// never writes to it - doesn't propose removing what was deliberately left out of the scaffold. That second
    /// part is the one this gap exists to get right: an asymmetric exclusion here would make every live grant
    /// look like a genuine removal after G-80.
    /// </summary>
    [TestMethod]
    public async Task Init_ExcludeGrantsAndRoleMemberships_SkipsScaffoldingAndPersistsTheChoiceSoDiffStaysEmpty()
    {
        var databaseName = $"g87_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g87-" + Guid.NewGuid().ToString("N"));
        const string role = "g87_reader";
        const string member = "g87_admin";

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var setup = new NpgsqlCommand(
                    $"""
                    CREATE ROLE {role} NOLOGIN;
                    CREATE ROLE {member} NOLOGIN;
                    CREATE TABLE public.widgets (id bigint PRIMARY KEY);
                    GRANT SELECT ON public.widgets TO {role};
                    GRANT {member} TO {role};
                    """,
                    connection);
                await setup.ExecuteNonQueryAsync();
            }

            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var initResult = await RunAsync(
                "init", "--project", projectFile, "--output", Path.Combine(projectDirectory, "sql"),
                "--source-connection", connectionString, "--exclude-grants", "--exclude-role-memberships");
            Assert.AreEqual(0, initResult.ExitCode, "init failed.\n" + initResult.Output);

            var widgetsFile = await File.ReadAllTextAsync(Path.Combine(projectDirectory, "sql", "Schema", "public", "Table", "widgets.sql"));
            Assert.IsFalse(widgetsFile.Contains("GRANT", StringComparison.Ordinal), widgetsFile);
            Assert.IsFalse(File.Exists(Path.Combine(projectDirectory, "sql", "ROLE", "role-memberships.sql")));

            var projectJson = await File.ReadAllTextAsync(projectFile);
            StringAssert.Contains(projectJson, "\"excludeGrants\": true");
            StringAssert.Contains(projectJson, "\"excludeRoleMemberships\": true");

            // The scaffolded project builds clean and diffs empty even though the live database still has both
            // the grant and the role membership - proof the persisted pgproj.json fields keep diff/publish from
            // ever seeing them, not just init's own scaffold.
            var buildResult = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, buildResult.ExitCode, "build of the scaffolded project failed.\n" + buildResult.Output);

            var diffResult = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, diffResult.ExitCode, "diff failed.\n" + diffResult.Output);
            StringAssert.Contains(diffResult.Output, "Added: 0");
            StringAssert.Contains(diffResult.Output, "Removed: 0");
            StringAssert.Contains(diffResult.Output, "Modified: 0");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await using (var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString))
            {
                await connection.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP ROLE IF EXISTS {role}; DROP ROLE IF EXISTS {member};", connection);
                await cleanup.ExecuteNonQueryAsync();
            }

            try
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj") { InitCommand.Create(context), BuildCommand.Create(context), DiffCommand.Create(context), PublishCommand.Create(context) };

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = await root.Parse(args).InvokeAsync();
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static async Task CreateDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {databaseName};", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);", connection);
        await command.ExecuteNonQueryAsync();
    }
}
