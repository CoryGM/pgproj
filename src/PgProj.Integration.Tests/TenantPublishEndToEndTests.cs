using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-290c against a real server: <c>publish</c> deploys a project with tenants in parts, the shared changes in one transaction, then each tenant in its own,
/// with the tenant's schema first on the search path; a new tenant's schema is created; a failure stops the run and a second run finishes; every part is
/// checked before anything runs; a former tenant's schema is never dropped.
/// </summary>
[TestClass]
public sealed class TenantPublishEndToEndTests
{
    private const string Shared = """
        CREATE SCHEMA platform;
        CREATE TABLE platform.currencies (code char(3) PRIMARY KEY);
        CREATE FUNCTION platform.round_money(numeric) RETURNS numeric LANGUAGE sql IMMUTABLE RETURN round($1, 2);
        """;

    private const string Template = """
        CREATE TYPE order_status AS ENUM ('open', 'paid');
        CREATE TABLE customers (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL);
        CREATE TABLE orders (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            customer_id bigint NOT NULL REFERENCES customers (id),
            currency char(3) NOT NULL REFERENCES platform.currencies (code),
            status order_status NOT NULL DEFAULT 'open',
            total numeric NOT NULL CHECK (total >= 0));
        CREATE INDEX ON orders (customer_id);
        CREATE VIEW open_orders AS
            SELECT o.id, c.name, round_money(o.total) AS total FROM orders o JOIN customers c ON c.id = o.customer_id WHERE o.status = 'open';
        CREATE FUNCTION order_count() RETURNS bigint LANGUAGE sql STABLE SET search_path FROM CURRENT AS $$ SELECT count(*) FROM orders $$;
        COMMENT ON TABLE orders IS 'Orders placed.';
        GRANT USAGE ON SCHEMA tenant TO PUBLIC;
        """;

    [TestMethod]
    public async Task AFileOfTenantsIsPublishedTenantByTenantAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g290cf");
        var projectDirectory = CreateTenantProject("g290cf", """, "file": "./tenants.csv" """, "tenant\nacme\nglobex\n");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            StringAssert.Contains(dryRun.Output, "-- pgproj: tenant tenant_acme (a transaction of its own)\nSET search_path TO tenant_acme, platform, public;".ReplaceLineEndings());
            StringAssert.Contains(dryRun.Output, "CREATE SCHEMA IF NOT EXISTS \"tenant_globex\"");
            StringAssert.Contains(dryRun.Output, "the script is not wrapped in transactions");

            var publish = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, publish.ExitCode, publish.Output);
            StringAssert.Contains(publish.Output, "Published the shared schemas.");
            StringAssert.Contains(publish.Output, "Published tenant_acme.");
            StringAssert.Contains(publish.Output, "Published: the shared schemas and 2 tenant(s), 2 of them with changes.");

            foreach (var schema in new[] { "tenant_acme", "tenant_globex" })
            {
                Assert.AreEqual("t", await ScalarTextAsync(connectionString, $"select has_schema_privilege('public', '{schema}', 'USAGE')::text::char"), $"{schema}: the stand-in's grant is the tenant's");
                Assert.AreEqual($"search_path={schema}, platform, public", await ScalarTextAsync(connectionString, $"select array_to_string(proconfig, ',') from pg_proc where proname = 'order_count' and pronamespace = '{schema}'::regnamespace"));
                Assert.AreEqual("Orders placed.", await ScalarTextAsync(connectionString, $"select obj_description('{schema}.orders'::regclass)"));
            }

            await ExecuteAsync(connectionString, "INSERT INTO platform.currencies VALUES ('EUR'); INSERT INTO tenant_acme.customers (name) VALUES ('Ann'); INSERT INTO tenant_acme.orders (customer_id, currency, total) VALUES (1, 'EUR', 12.345);");
            Assert.AreEqual("1|0", await ScalarTextAsync(connectionString, "select tenant_acme.order_count() || '|' || tenant_globex.order_count()"), "each tenant's function counts its own orders");
            Assert.AreEqual("12.35", await ScalarTextAsync(connectionString, "select total::text from tenant_acme.open_orders"));

            await AssertConvergedAsync(projectFile, connectionString);
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            StringAssert.Contains(again.Output, "Published: the shared schemas (no changes) and 2 tenant(s), 0 of them with changes.");

            // A change to the template reaches every tenant; --tenants narrows it.
            File.WriteAllText(Path.Combine(projectDirectory, "sql", "Tenant", "orders.sql"), Template.Replace("name text NOT NULL)", "name text NOT NULL, email text)", StringComparison.Ordinal));
            var onlyAcme = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--tenants", "acme");
            Assert.AreEqual(0, onlyAcme.ExitCode, onlyAcme.Output);
            Assert.AreEqual("1|0", await ScalarTextAsync(connectionString, "select count(*) filter (where table_schema = 'tenant_acme') || '|' || count(*) filter (where table_schema = 'tenant_globex') from information_schema.columns where column_name = 'email'"));
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await AssertConvergedAsync(projectFile, connectionString);

            // A tenant dropped from the file keeps its schema.
            File.WriteAllText(Path.Combine(projectDirectory, "tenants.csv"), "tenant\nacme\n");
            var dropped = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, dropped.ExitCode, dropped.Output);
            StringAssert.Contains(dropped.Output, "tenant_globex is no longer a tenant");
            StringAssert.Contains(dropped.Output, "pgproj left the schema and everything in it as they are");
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "select count(*)::text from pg_namespace where nspname = 'tenant_globex'"));
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "select count(*)::text from pg_tables where schemaname = 'tenant_globex' and tablename = 'orders'"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AFailedTenantStopsTheRunAndASecondRunFinishes()
    {
        var databaseName = await CreateDatabaseAsync("g290cx");
        var projectDirectory = CreateTenantProject("g290cx", """, "file": "./tenants.csv" """, "tenant\na\nb\nc\n");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "INSERT INTO tenant_b.customers (name) VALUES ('Bea');");

            File.WriteAllText(Path.Combine(projectDirectory, "sql", "platform.sql"), Shared + "CREATE TABLE platform.plans (code text PRIMARY KEY);");
            File.WriteAllText(Path.Combine(projectDirectory, "sql", "Tenant", "orders.sql"), Template.Replace("name text NOT NULL)", "name text NOT NULL, phone text NOT NULL)", StringComparison.Ordinal));
            var failed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");

            Assert.AreEqual(1, failed.ExitCode, failed.Output);
            StringAssert.Contains(failed.Output, "Publish failed in tenant_b: 23502");
            StringAssert.Contains(failed.Output, "Published before tenant_b, and kept: the shared schemas, tenant_a.");
            StringAssert.Contains(failed.Output, "Not started: tenant_c.");
            Assert.AreEqual("1|1|0|0", await ScalarTextAsync(
                connectionString,
                "select (select count(*) from pg_tables where schemaname = 'platform' and tablename = 'plans') || '|' || "
                + "count(*) filter (where table_schema = 'tenant_a') || '|' || count(*) filter (where table_schema = 'tenant_b') || '|' || count(*) filter (where table_schema = 'tenant_c') "
                + "from information_schema.columns where column_name = 'phone'"));

            await ExecuteAsync(connectionString, "DELETE FROM tenant_b.customers;");
            var finished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, finished.ExitCode, finished.Output);
            StringAssert.Contains(finished.Output, "Published: the shared schemas (no changes) and 3 tenant(s), 2 of them with changes.");
            await AssertConvergedAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task EveryTenantIsCheckedBeforeAnythingRuns()
    {
        var databaseName = await CreateDatabaseAsync("g290cd");
        var projectDirectory = CreateTenantProject("g290cd", """, "file": "./tenants.csv" """, "tenant\na\nb\n");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            File.WriteAllText(Path.Combine(projectDirectory, "sql", "platform.sql"), Shared + "CREATE TABLE platform.plans (code text PRIMARY KEY);");
            File.WriteAllText(Path.Combine(projectDirectory, "sql", "Tenant", "orders.sql"), Template.Replace("total numeric NOT NULL CHECK (total >= 0))", "total numeric NOT NULL CHECK (total >= 0), note text)", StringComparison.Ordinal).Replace("name text NOT NULL)", "id2 int)", StringComparison.Ordinal));
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");

            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "can discard data, in 2 part(s); nothing was run.");
            StringAssert.Contains(refused.Output, "  tenant_a: [");
            StringAssert.Contains(refused.Output, "  tenant_b: [");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "select count(*)::text from pg_tables where schemaname = 'platform' and tablename = 'plans'"), "the shared part was not run either");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AQueryWhoseTableTheSharedChangesCreateIsReadAfterThem()
    {
        var databaseName = await CreateDatabaseAsync("g290cq");
        var projectDirectory = CreateTenantProject("g290cq", """, "query": "SELECT slug FROM platform.tenants WHERE active" """, null, """, "postdeploy": "./post.sql" """);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "sql", "tenants.sql"), "CREATE TABLE platform.tenants (slug text PRIMARY KEY, active bool NOT NULL);");
            File.WriteAllText(Path.Combine(projectDirectory, "post.sql"), "INSERT INTO platform.tenants VALUES ('acme', true), ('globex', false) ON CONFLICT DO NOTHING;");

            var publish = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");

            Assert.AreEqual(0, publish.ExitCode, publish.Output);
            StringAssert.Contains(publish.Output, "Read the tenants after publishing the shared changes: 1.");
            StringAssert.Contains(publish.Output, "Published tenant_acme.");
            Assert.AreEqual("tenant_acme", await ScalarTextAsync(connectionString, "select string_agg(nspname, ',') from pg_namespace where nspname like 'tenant%'"));
            await AssertConvergedAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ThePrefixAloneFillsTheSchemasTheApplicationCreatesAndConcurrentlyRunsPerTenant()
    {
        var databaseName = await CreateDatabaseAsync("g290cp");
        var projectDirectory = CreateTenantProject("g290cp", string.Empty, null);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            await ExecuteAsync(connectionString, "CREATE SCHEMA tenant_acme; CREATE SCHEMA tenant_globex;");

            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "select count(*)::text from pg_tables where tablename = 'orders'"));

            File.WriteAllText(Path.Combine(projectDirectory, "sql", "Tenant", "orders.sql"), Template + "\nCREATE INDEX orders_status_idx ON orders (status);");
            var concurrently = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--concurrently");

            Assert.AreEqual(0, concurrently.ExitCode, concurrently.Output);
            StringAssert.Contains(concurrently.Output, "statement(s) of tenant_acme outside the transaction (--concurrently)");
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "select count(*)::text from pg_indexes where indexname = 'orders_status_idx'"));
            await AssertConvergedAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static async Task AssertConvergedAsync(string projectFile, string connectionString)
    {
        var diff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--exit-code");
        Assert.AreEqual(0, diff.ExitCode, diff.Output);
    }

    private static string CreateTenantProject(string prefix, string tenantSource, string? tenantsCsv, string extra = "")
    {
        var directory = CreateProjectDirectory(prefix);
        File.WriteAllText(
            ProjectFile(directory),
            $$"""{ "name": "{{prefix}}", "version": "1.0", "source": "./sql", "searchPath": ["platform", "public"]{{extra}}, "tenants": { "template": "./sql/Tenant", "prefix": "tenant"{{tenantSource}} } }""");
        Directory.CreateDirectory(Path.Combine(directory, "sql", "Tenant"));
        File.WriteAllText(Path.Combine(directory, "sql", "platform.sql"), Shared);
        File.WriteAllText(Path.Combine(directory, "sql", "Tenant", "orders.sql"), Template);
        if (tenantsCsv is not null)
        {
            File.WriteAllText(Path.Combine(directory, "tenants.csv"), tenantsCsv);
        }

        return directory;
    }
}
