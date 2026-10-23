using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-303a against a real server, on every version: a PL/pgSQL body is read to warn about names the project does not define, and changes nothing else. Two routines that call each
/// other and a table created after the routine that reads it publish in the order the project gives, and a second publish proposes nothing.
/// </summary>
[TestClass]
public sealed class PlpgsqlBodyEndToEndTests
{
    private const string Schema = """
        CREATE FUNCTION is_even(n int) RETURNS boolean LANGUAGE plpgsql AS $$
        BEGIN
          IF n = 0 THEN RETURN true; END IF;
          RETURN is_odd(n - 1);
        END $$;

        CREATE FUNCTION is_odd(n int) RETURNS boolean LANGUAGE plpgsql AS $$
        BEGIN
          IF n = 0 THEN RETURN false; END IF;
          RETURN is_even(n - 1);
        END $$;

        CREATE FUNCTION recent_ids() RETURNS SETOF int LANGUAGE plpgsql AS $$
        BEGIN
          RETURN QUERY SELECT id FROM events ORDER BY id DESC LIMIT 3;
          RETURN QUERY SELECT id FROM not_in_the_project;
        END $$;

        CREATE FUNCTION stamp() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          NEW.seen = (SELECT count(*) FROM events) > 0;
          CREATE TEMP TABLE IF NOT EXISTS scratch AS SELECT 1 AS a;
          PERFORM a FROM scratch;
          EXECUTE 'select * from ' || 'unknown_to_the_project';
          RETURN NEW;
        END $$;

        CREATE TABLE events (id int PRIMARY KEY, seen boolean);
        CREATE TRIGGER events_stamp BEFORE INSERT ON events FOR EACH ROW EXECUTE FUNCTION stamp();
        """;

    [TestMethod]
    public async Task ABodyIsWarnedAboutAndNeverReordersOrBlocksAPublish()
    {
        var databaseName = await CreateDatabaseAsync("g303a");
        var projectDirectory = CreateProjectDirectory("g303a");
        try
        {
            var connection = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var build = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, build.ExitCode, build.Output);
            StringAssert.Contains(build.Output, "[Warning] The PL/pgSQL body of function 'public.recent_ids()' names table or view 'not_in_the_project'");
            Assert.DoesNotContain("unknown_to_the_project", build.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("scratch", build.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("'events'", build.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("is_odd'", build.Output, StringComparison.Ordinal);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connection, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("true", await ScalarTextAsync(connection, "SELECT is_even(4)::text"));

            await ExecuteAsync(connection, "CREATE TABLE unknown_to_the_project (x int)");
            await ExecuteAsync(connection, "INSERT INTO events (id) VALUES (1); INSERT INTO events (id) VALUES (2)");
            await ExecuteAsync(connection, "DROP TABLE unknown_to_the_project");
            Assert.AreEqual("true", await ScalarTextAsync(connection, "SELECT seen::text FROM events WHERE id = 2"), "the trigger function's body ran");

            await AssertDiffIsEmptyAsync(projectFile, connection);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task DroppingWhatABodyNamesIsNotedAndTheRoutineThenFailsWhenItRuns()
    {
        var databaseName = await CreateDatabaseAsync("g303b");
        var projectDirectory = CreateProjectDirectory("g303b");
        try
        {
            var connection = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string reader = """
                CREATE FUNCTION audit_count() RETURNS bigint LANGUAGE plpgsql AS $$
                BEGIN
                  RETURN (SELECT count(*) FROM audit_log a WHERE a.id > 0);
                END $$;
                """;
            WriteSchema(projectDirectory, "CREATE TABLE audit_log (id int PRIMARY KEY, note text);\n" + reader);
            var first = await RunAsync("publish", "--project", projectFile, "--target-connection", connection, "--execute");
            Assert.AreEqual(0, first.ExitCode, first.Output);
            Assert.DoesNotContain("PL/pgSQL routine names", first.Output, StringComparison.Ordinal, "nothing is dropped, so nothing is said");

            // The project drops the table and keeps the function.
            WriteSchema(projectDirectory, reader);
            var diff = await RunAsync("diff", "--project", projectFile, "--target-connection", connection);
            StringAssert.Contains(diff.Output, "what the body of a PL/pgSQL routine names");
            StringAssert.Contains(diff.Output, "function 'public.audit_count()' names table 'public.audit_log' is dropped");

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connection, "--dry-run", "--allow-destructive");
            StringAssert.Contains(dryRun.Output, "function 'public.audit_count()' names table 'public.audit_log' is dropped");

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connection, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connection, "--execute", "--allow-destructive");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            StringAssert.Contains(published.Output, "function 'public.audit_count()' names table 'public.audit_log' is dropped");

            // PostgreSQL did not stop it, and the function now fails when it runs: the hazard the note names.
            var failure = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () => await ScalarTextAsync(connection, "SELECT audit_count()"));
            Assert.AreEqual("42P01", failure.SqlState);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
