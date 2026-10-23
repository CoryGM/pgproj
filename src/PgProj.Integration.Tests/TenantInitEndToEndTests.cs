using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-290d against a real server: <c>init --tenant-prefix</c> scaffolds the shared schemas as usual and one tenant into the template with its schema name taken
/// out (a function's <c>search_path</c> naming the stand-in, so each tenant's copy is pinned to its own schema), names the tenants that differ, writes the
/// <c>tenants</c> block and the tenant file; the project builds, and published into an empty database it gives every tenant its own copy and converges.
/// </summary>
[TestClass]
public sealed class TenantInitEndToEndTests
{
    private const string Shared = """
        CREATE SCHEMA platform;
        CREATE TABLE platform.currencies (code char(3) PRIMARY KEY);
        CREATE FUNCTION platform.round_money(numeric) RETURNS numeric LANGUAGE sql IMMUTABLE RETURN round($1, 2);
        """;

    private const string Tenant = """
        CREATE TYPE order_status AS ENUM ('open', 'paid');
        CREATE TABLE customers (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL);
        CREATE TABLE orders (
            id serial PRIMARY KEY,
            customer_id bigint NOT NULL REFERENCES customers (id),
            currency char(3) NOT NULL REFERENCES platform.currencies (code),
            status order_status NOT NULL DEFAULT 'open',
            total numeric NOT NULL);
        CREATE INDEX ON orders (customer_id);
        CREATE VIEW open_orders AS
            SELECT o.id, c.name, round_money(o.total) AS total FROM orders o JOIN customers c ON c.id = o.customer_id WHERE o.status = 'open';
        CREATE FUNCTION order_count() RETURNS bigint LANGUAGE sql STABLE SET search_path FROM CURRENT AS $$ SELECT count(*) FROM orders $$;
        COMMENT ON TABLE orders IS 'Orders placed.';
        """;

    [TestMethod]
    public async Task ATenantDatabaseIsScaffoldedIntoATemplateThatPublishesEveryTenant()
    {
        var sourceDatabase = await CreateDatabaseAsync("g290di");
        var targetDatabase = await CreateDatabaseAsync("g290dt");
        var projectDirectory = Path.Combine(Path.GetTempPath(), $"pgproj-g290d-{Guid.NewGuid():N}");
        try
        {
            var source = ConnectionStringFor(sourceDatabase);
            await ExecuteAsync(source, Shared);
            foreach (var identifier in new[] { "acme", "globex", "initech" })
            {
                var schema = "tenant_" + identifier;
                await ExecuteAsync(source, $"CREATE SCHEMA {schema}; SET search_path TO {schema}, platform, public; {Tenant} GRANT USAGE ON SCHEMA {schema} TO PUBLIC; RESET search_path;");
            }

            await ExecuteAsync(source, "CREATE INDEX orders_status_idx ON tenant_initech.orders (status);");
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");

            var init = await RunAsync("init", "--source-connection", source, "--project", projectFile, "--tenant-prefix", "tenant", "--tenant-file", "--report");

            Assert.AreEqual(0, init.ExitCode, init.Output);
            StringAssert.Contains(init.Output, "Tenants: 3 schema(s) named tenant_...; the template (Tenant,");
            StringAssert.Contains(init.Output, "was scaffolded from tenant_acme.");
            StringAssert.Contains(init.Output, "1 tenant(s) differ from the template; pgproj diff shows how, and pgproj publish would make each match it: tenant_initech (0 added, 1 removed, 1 modified).");
            var json = File.ReadAllText(projectFile);
            StringAssert.Contains(json, "\"template\": \"./sql/Tenant\"");
            StringAssert.Contains(json, "\"prefix\": \"tenant\"");
            StringAssert.Contains(json, "\"file\": \"./tenants.csv\"");
            Assert.AreEqual("tenant\nacme\nglobex\ninitech\n", File.ReadAllText(Path.Combine(projectDirectory, "tenants.csv")));
            Assert.IsFalse(Directory.Exists(Path.Combine(projectDirectory, "sql", "Schema", "tenant_acme")), "a tenant's schema is not scaffolded as shared");
            Assert.IsTrue(File.Exists(Path.Combine(projectDirectory, "sql", "Schema", "platform", "Table", "currencies.sql")));
            var orders = File.ReadAllText(Path.Combine(projectDirectory, "sql", "Tenant", "Table", "orders.sql"));
            Assert.IsFalse(orders.Contains("tenant_acme", StringComparison.Ordinal), orders);
            StringAssert.Contains(orders, "nextval('orders_id_seq'::regclass)");
            StringAssert.Contains(File.ReadAllText(Path.Combine(projectDirectory, "sql", "Tenant", "Function", "order_count.sql")), "SET search_path TO 'tenant', 'platform', 'public'");
            StringAssert.Contains(File.ReadAllText(Path.Combine(projectDirectory, "sql", "Tenant", "tenant.sql")), "GRANT USAGE ON SCHEMA tenant TO PUBLIC;");
            StringAssert.Contains(File.ReadAllText(Directory.GetFiles(Path.Combine(projectDirectory, "sql"), "init-report-*.md").Single()), "| `tenant_initech` | 0 | 1 | 1 |");

            var build = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, build.ExitCode, build.Output);
            StringAssert.Contains(build.Output, "No diagnostics.");

            var diff = await RunAsync("diff", "--project", projectFile, "--target-connection", source);
            StringAssert.Contains(diff.Output, "2 tenant(s) up to date: tenant_acme, tenant_globex");

            var target = ConnectionStringFor(targetDatabase);
            var publish = await RunAsync("publish", "--project", projectFile, "--target-connection", target, "--execute");
            Assert.AreEqual(0, publish.ExitCode, publish.Output);
            Assert.AreEqual(
                "tenant_acme: search_path=tenant_acme, platform, public|tenant_globex: search_path=tenant_globex, platform, public|tenant_initech: search_path=tenant_initech, platform, public",
                await ScalarTextAsync(target, "select string_agg(n.nspname || ': ' || array_to_string(p.proconfig, ','), '|' order by n.nspname) from pg_proc p join pg_namespace n on n.oid = p.pronamespace where p.proname = 'order_count'"));
            Assert.AreEqual("0", (await RunAsync("diff", "--project", projectFile, "--target-connection", target, "--exit-code")).ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await DropDatabaseAsync(sourceDatabase);
            await DropDatabaseAsync(targetDatabase);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task TheTemplateTenantAndTheQueryCanBeChosenAndNoTenantIsAnError()
    {
        var databaseName = await CreateDatabaseAsync("g290dq");
        var projectDirectory = Path.Combine(Path.GetTempPath(), $"pgproj-g290dq-{Guid.NewGuid():N}");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, Shared + "CREATE SCHEMA tenant_a; CREATE TABLE tenant_a.t (id int); CREATE SCHEMA tenant_b; CREATE TABLE tenant_b.t (id int, extra text);");
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");

            var none = await RunAsync("init", "--source-connection", connectionString, "--project", projectFile, "--tenant-prefix", "customer");
            Assert.AreEqual(1, none.ExitCode, none.Output);
            StringAssert.Contains(none.Output, "The database has no schema named customer_<identifier>");

            var init = await RunAsync("init", "--source-connection", connectionString, "--project", projectFile, "--force", "--tenant-prefix", "tenant", "--tenant-from", "b", "--tenant-query", "SELECT slug FROM platform.tenants");
            Assert.AreEqual(0, init.ExitCode, init.Output);
            StringAssert.Contains(init.Output, "was scaffolded from tenant_b.");
            StringAssert.Contains(init.Output, "tenant_a (1 added, 0 removed, 1 modified)");
            StringAssert.Contains(File.ReadAllText(projectFile), "\"query\": \"SELECT slug FROM platform.tenants\"");
            StringAssert.Contains(File.ReadAllText(Path.Combine(projectDirectory, "sql", "Tenant", "Table", "t.sql")), "extra text");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
