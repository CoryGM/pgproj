using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-133 against a real server (the user's design of 2026-11-10): foreign tables on <c>postgres_fdw</c> are deployed with their options, their columns' options, a
/// <c>CHECK</c>, a trigger, statistics, a comment, a grant, and an owner, read back, compared, and changed in place; a foreign partition is deployed under its parent; a move
/// to another server drops and creates the table, refused while a view reads it; dropping one needs <c>--allow-destructive</c>; <c>init</c> writes it to
/// <c>Schema/&lt;schema&gt;/ForeignTable/</c> and the scaffold builds the same in another database. Creating a foreign table connects to nothing, so the servers point nowhere.
/// </summary>
[TestClass]
public sealed class ForeignTableEndToEndTests
{
    private const string Servers = """
        CREATE EXTENSION postgres_fdw;
        CREATE SERVER remote FOREIGN DATA WRAPPER postgres_fdw OPTIONS (host 'remote.invalid', dbname 'shop');
        CREATE SERVER archive FOREIGN DATA WRAPPER postgres_fdw OPTIONS (host 'archive.invalid', dbname 'shop');

        """;

    private static string NewName(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private static string Tables(string role) => $"""
        CREATE FUNCTION public.touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;

        CREATE FOREIGN TABLE public.remote_orders (
            id integer OPTIONS (column_name 'order_id') NOT NULL,
            amount numeric(10,2) DEFAULT 0,
            note text COLLATE "C",
            CONSTRAINT positive CHECK (id > 0)
        ) SERVER remote OPTIONS (schema_name 'sales', table_name 'orders');
        COMMENT ON FOREIGN TABLE public.remote_orders IS 'orders kept on the remote server';
        COMMENT ON COLUMN public.remote_orders.note IS 'free text';
        GRANT SELECT ON public.remote_orders TO {role};
        ALTER FOREIGN TABLE public.remote_orders OWNER TO {role}o;
        CREATE TRIGGER touch BEFORE INSERT ON public.remote_orders FOR EACH ROW EXECUTE FUNCTION public.touch();
        CREATE STATISTICS public.remote_orders_stats (ndistinct) ON id, amount FROM public.remote_orders;

        CREATE TABLE public.events (happened date NOT NULL, what text) PARTITION BY RANGE (happened);
        CREATE TABLE public.events_now PARTITION OF public.events FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
        CREATE FOREIGN TABLE public.events_old PARTITION OF public.events FOR VALUES FROM ('2000-01-01') TO ('2026-01-01') SERVER archive OPTIONS (table_name 'events');

        """;

    [TestMethod]
    public async Task ForeignTablesPublishConvergeAndChangeInPlace()
    {
        var role = NewName("g133r");
        var databaseName = await CreateDatabaseAsync("g133");
        var projectDirectory = CreateProjectDirectory("g133");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN; CREATE ROLE {role}o NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Servers + Tables(role));

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("not modeled", StringComparison.Ordinal), built.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual(
                "remote schema_name=sales,table_name=orders",
                await ScalarTextAsync(connectionString, "SELECT s.srvname || ' ' || array_to_string(ft.ftoptions, ',') FROM pg_foreign_table ft JOIN pg_foreign_server s ON s.oid = ft.ftserver WHERE ft.ftrelid = 'public.remote_orders'::regclass"));
            Assert.AreEqual("column_name=order_id", await ScalarTextAsync(connectionString, "SELECT array_to_string(attfdwoptions, ',') FROM pg_attribute WHERE attrelid = 'public.remote_orders'::regclass AND attname = 'id'"));
            Assert.AreEqual($"{role}o orders kept on the remote server", await ScalarTextAsync(connectionString, "SELECT pg_get_userbyid(relowner) || ' ' || obj_description(oid, 'pg_class') FROM pg_class WHERE oid = 'public.remote_orders'::regclass"));
            Assert.AreEqual("1 1 1", await ScalarTextAsync(connectionString, "SELECT (SELECT count(*) FROM pg_constraint WHERE conrelid = 'public.remote_orders'::regclass AND contype = 'c') || ' ' || (SELECT count(*) FROM pg_trigger WHERE tgrelid = 'public.remote_orders'::regclass) || ' ' || (SELECT count(*) FROM pg_statistic_ext WHERE stxrelid = 'public.remote_orders'::regclass)"));
            Assert.AreEqual("f true", await ScalarTextAsync(connectionString, "SELECT relkind::text || ' ' || relispartition::text FROM pg_class WHERE oid = 'public.events_old'::regclass"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Options change in place: the table keeps its oid.
            var oid = await ScalarTextAsync(connectionString, "SELECT 'public.remote_orders'::regclass::oid::text");
            WriteSchema(projectDirectory, Edit(Edit(Servers + Tables(role), "table_name 'orders'", "table_name 'orders_v2', fetch_size '500'"), "column_name 'order_id'", "column_name 'id'"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual(
                $"{oid} schema_name=sales,table_name=orders_v2,fetch_size=500 column_name=id",
                await ScalarTextAsync(connectionString, "SELECT ft.ftrelid::oid::text || ' ' || array_to_string(ft.ftoptions, ',') || ' ' || (SELECT array_to_string(attfdwoptions, ',') FROM pg_attribute WHERE attrelid = ft.ftrelid AND attname = 'id') FROM pg_foreign_table ft WHERE ft.ftrelid = 'public.remote_orders'::regclass"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A move to another server drops and creates the table, with its grant, owner, trigger, check, and statistics, and is not destructive: it keeps no rows.
            WriteSchema(projectDirectory, Edit(Servers + Tables(role), ") SERVER remote OPTIONS", ") SERVER archive OPTIONS"));
            var moved = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, moved.ExitCode, moved.Output);
            Assert.AreEqual(
                $"archive {role}o true 1 1 1",
                await ScalarTextAsync(connectionString, $"SELECT s.srvname || ' ' || pg_get_userbyid(c.relowner) || ' ' || has_table_privilege('{role}', c.oid, 'SELECT')::text || ' ' || (SELECT count(*) FROM pg_constraint WHERE conrelid = c.oid AND contype = 'c') || ' ' || (SELECT count(*) FROM pg_trigger WHERE tgrelid = c.oid) || ' ' || (SELECT count(*) FROM pg_statistic_ext WHERE stxrelid = c.oid) FROM pg_class c JOIN pg_foreign_table ft ON ft.ftrelid = c.oid JOIN pg_foreign_server s ON s.oid = ft.ftserver WHERE c.oid = 'public.remote_orders'::regclass"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Dropping one needs --allow-destructive.
            WriteSchema(projectDirectory, Servers + """
                CREATE FUNCTION public.touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
                CREATE TABLE public.events (happened date NOT NULL, what text) PARTITION BY RANGE (happened);
                CREATE TABLE public.events_now PARTITION OF public.events FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');

                """);
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "foreign table dropped");
            var dropped = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, dropped.ExitCode, dropped.Output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_foreign_table"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role}; DROP ROLE IF EXISTS {role}o;");
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AMoveToAnotherServerIsRefusedWhileAViewReadsTheTable()
    {
        var databaseName = await CreateDatabaseAsync("g133v");
        var projectDirectory = CreateProjectDirectory("g133v");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string table = "CREATE FOREIGN TABLE public.ft (a integer) SERVER remote;\nCREATE VIEW public.v AS SELECT a FROM public.ft;\n";
            WriteSchema(projectDirectory, Servers + table);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            WriteSchema(projectDirectory, Servers + table.Replace("SERVER remote", "SERVER archive", StringComparison.Ordinal));
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "[Table] public.ft");
            var preview = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(preview.Output, "-- TODO: The foreign table public.ft moves to another server");
            StringAssert.Contains(preview.Output, "public.v");
            Assert.AreEqual("remote", await ScalarTextAsync(connectionString, "SELECT s.srvname FROM pg_foreign_table ft JOIN pg_foreign_server s ON s.oid = ft.ftserver"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task InitWritesForeignTablesAndTheScaffoldConverges()
    {
        var role = NewName("g133i");
        var databaseName = await CreateDatabaseAsync("g133i");
        var secondDatabase = await CreateDatabaseAsync("g133j");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g133-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN; CREATE ROLE {role}o NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, Servers + Tables(role));

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.IsFalse(scaffolded.Output.Contains("Foreign table", StringComparison.Ordinal), "A foreign table is read now.\n" + scaffolded.Output);
            var file = File.ReadAllText(Path.Combine(scaffoldDirectory, "sql", "Schema", "public", "ForeignTable", "remote_orders.sql"));
            StringAssert.Contains(file, "CREATE FOREIGN TABLE public.remote_orders (");
            StringAssert.Contains(file, "id integer OPTIONS (column_name 'order_id') NOT NULL");
            StringAssert.Contains(file, "SERVER remote OPTIONS (schema_name 'sales', table_name 'orders');");
            StringAssert.Contains(file, "COMMENT ON FOREIGN TABLE public.remote_orders IS 'orders kept on the remote server';");
            StringAssert.Contains(file, "CREATE TRIGGER touch");
            StringAssert.Contains(file, "CREATE STATISTICS");
            StringAssert.Contains(file, $"GRANT SELECT ON TABLE public.remote_orders TO {role}");
            Assert.IsTrue(File.Exists(Path.Combine(scaffoldDirectory, "sql", "Schema", "public", "ForeignTable", "events_old.sql")));
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var secondConnection = ConnectionStringFor(secondDatabase);
            var copied = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, copied.ExitCode, copied.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role}; DROP ROLE IF EXISTS {role}o;");
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
