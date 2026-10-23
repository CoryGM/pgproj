using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-271 against a real server: <c>publish --execute --concurrently</c> builds new indexes and keys on tables that already exist after the transaction commits, one
/// statement at a time; a failed build is dropped, named, and finished by the next run; an index PostgreSQL marks <c>INVALID</c> is reported by <c>diff</c>, rebuilt by
/// <c>publish</c>, and left out by <c>init</c>.
/// </summary>
[TestClass]
public sealed class ConcurrentPublishEndToEndTests
{
    private const string LiveSetup = """
        CREATE TABLE public.ref (id int PRIMARY KEY);
        INSERT INTO public.ref VALUES (1), (2);
        CREATE TABLE public.t (id int NOT NULL, ref_id int, email text, v int);
        INSERT INTO public.t SELECT g, 1 + g % 2, 'e' || g, g FROM generate_series(1, 1000) g;
        CREATE INDEX t_v ON public.t (v);
        CREATE TABLE public.p (id int NOT NULL, v int) PARTITION BY RANGE (id);
        CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM (0) TO (100);
        CREATE TABLE public.p2 PARTITION OF public.p FOR VALUES FROM (100) TO (200);
        INSERT INTO public.p SELECT g, g FROM generate_series(0, 199) g;
        """;

    private const string Project = """
        CREATE TABLE public.ref (id int PRIMARY KEY);
        CREATE TABLE public.t (id int NOT NULL, ref_id int, email text, v int,
          CONSTRAINT t_pkey PRIMARY KEY (id),
          CONSTRAINT t_email_key UNIQUE (email),
          CONSTRAINT t_ref_fk FOREIGN KEY (ref_id) REFERENCES public.ref (id),
          CONSTRAINT t_v_check CHECK (v > 0));
        CREATE INDEX t_v ON public.t (v DESC);
        CREATE INDEX t_email_lower ON public.t (lower(email));
        COMMENT ON INDEX public.t_email_lower IS 'lower email';
        CREATE TABLE public.p (id int NOT NULL, v int) PARTITION BY RANGE (id);
        CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM (0) TO (100);
        CREATE TABLE public.p2 PARTITION OF public.p FOR VALUES FROM (100) TO (200);
        CREATE TABLE public.p3 PARTITION OF public.p FOR VALUES FROM (200) TO (300);
        CREATE INDEX p_v ON public.p (v);
        """;

    [TestMethod]
    public async Task IndexesAndKeysOnExistingTablesAreBuiltAfterTheCommit()
    {
        var databaseName = await CreateDatabaseAsync("g271");
        var projectDirectory = CreateProjectDirectory("g271");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            await ExecuteAsync(connectionString, LiveSetup);
            WriteSchema(projectDirectory, Project);

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--concurrently", "--dry-run");
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            var marker = dryRun.Output.IndexOf(PgProj.Deploy.ConcurrentPublishPlan.Marker, StringComparison.Ordinal);
            Assert.IsGreaterThan(0, marker, dryRun.Output);
            Assert.IsGreaterThan(marker, dryRun.Output.IndexOf("CREATE INDEX CONCURRENTLY \"t_email_lower\"", StringComparison.Ordinal), dryRun.Output);
            Assert.IsLessThan(marker, dryRun.Output.IndexOf("NOT VALID;", StringComparison.Ordinal), dryRun.Output);
            StringAssert.Contains(dryRun.Output, "psql --single-transaction would make CREATE INDEX CONCURRENTLY fail");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_indexes WHERE indexname = 't_email_lower'"), "a dry run runs nothing");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--concurrently", "--execute", "--lock-timeout", "5s");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            StringAssert.Contains(published.Output, "outside the transaction (--concurrently)");
            StringAssert.Contains(published.Output, "build index public.p1_v_idx on partition public.p1 concurrently");
            // Nothing that blocks writes is left in the transaction, so G-270 has nothing to name.
            Assert.DoesNotContain("blocks writes", published.Output, StringComparison.Ordinal);

            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_index WHERE NOT indisvalid"));
            Assert.AreEqual("true", await ScalarTextAsync(connectionString, "SELECT convalidated::text FROM pg_constraint WHERE conname = 't_ref_fk'"));
            Assert.AreEqual("lower email", await ScalarTextAsync(connectionString, "SELECT obj_description('public.t_email_lower'::regclass, 'pg_class')"));
            Assert.AreEqual("3", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_inherits WHERE inhparent = 'public.p_v'::regclass"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AFailedBuildIsDroppedAndTheNextRunFinishes()
    {
        var databaseName = await CreateDatabaseAsync("g271f");
        var projectDirectory = CreateProjectDirectory("g271f");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            await ExecuteAsync(connectionString, "CREATE TABLE public.t (id int NOT NULL, email text); INSERT INTO public.t VALUES (1, 'a'), (2, 'a');");
            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int NOT NULL, email text, CONSTRAINT t_email_key UNIQUE (email));\nCREATE INDEX t_id ON public.t (id);\n");

            var failed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--concurrently", "--execute");
            Assert.AreEqual(1, failed.ExitCode, failed.Output);
            StringAssert.Contains(failed.Output, "after the commit failed (build the index of unique constraint t_email_key");
            StringAssert.Contains(failed.Output, "23505");
            StringAssert.Contains(failed.Output, "Dropped what it was building");
            StringAssert.Contains(failed.Output, "Not done:");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_class WHERE relname = 't_email_key'"), "the half-built index was dropped");

            await ExecuteAsync(connectionString, "UPDATE public.t SET email = 'b' WHERE id = 2;");
            var finished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--concurrently", "--execute");
            Assert.AreEqual(0, finished.ExitCode, finished.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AnInvalidIndexIsReportedRebuiltAndLeftOutOfInit()
    {
        var databaseName = await CreateDatabaseAsync("g271i");
        var projectDirectory = CreateProjectDirectory("g271i");
        var initDirectory = CreateProjectDirectory("g271init");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            await ExecuteAsync(connectionString, "CREATE TABLE public.t (id int NOT NULL, v int); INSERT INTO public.t SELECT g, g % 10 FROM generate_series(1, 100) g;");
            // What a failed CREATE INDEX CONCURRENTLY leaves behind.
            try
            {
                await ExecuteAsync(connectionString, "CREATE UNIQUE INDEX CONCURRENTLY t_v ON public.t (v);");
            }
            catch (Npgsql.PostgresException)
            {
            }

            Assert.AreEqual("false", await ScalarTextAsync(connectionString, "SELECT indisvalid::text FROM pg_index WHERE indexrelid = 'public.t_v'::regclass"));

            var init = await RunAsync("init", "--source-connection", connectionString, "--project", ProjectFile(initDirectory), "--output", Path.Combine(initDirectory, "sql"), "--force");
            Assert.AreEqual(0, init.ExitCode, init.Output);
            StringAssert.Contains(init.Output, "PostgreSQL marks INVALID");
            Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(initDirectory, "sql"), "*.sql", SearchOption.AllDirectories).Any(file => File.ReadAllText(file).Contains("t_v", StringComparison.Ordinal)), "init left it out");

            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int NOT NULL, v int);\nCREATE INDEX t_v ON public.t (v);\n");
            var diff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diff.Output, "Valid: ");

            // Without --concurrently it is rebuilt in the transaction, and G-270 says it blocks writes.
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            StringAssert.Contains(published.Output, "CREATE INDEX t_v blocks writes");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            Assert.AreEqual("true", await ScalarTextAsync(connectionString, "SELECT indisvalid::text FROM pg_index WHERE indexrelid = 'public.t_v'::regclass"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(initDirectory);
        }
    }
}
