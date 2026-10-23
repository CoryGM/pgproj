using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-290b against a real server: <c>diff</c> compares a project with tenants in parts, the shared schemas once and each tenant's schema against the template,
/// in each of the three ways of giving the tenants. The tenants are set up here by running the template in each schema, as <c>publish</c> will (G-290c).
/// </summary>
[TestClass]
public sealed class TenantDiffEndToEndTests
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
    public async Task AFileOfTenantsIsComparedTenantByTenant()
    {
        var databaseName = await CreateDatabaseAsync("g290f");
        var projectDirectory = CreateTenantProject("g290f", """, "file": "./tenants.csv" """);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "tenants.csv"), "tenant,plan\nacme,gold\nglobex,silver\nnewco,bronze\n");
            await SetUpAsync(connectionString, "acme", "globex", "old");
            await ExecuteAsync(connectionString, "CREATE INDEX orders_status_idx ON tenant_globex.orders (status); COMMENT ON TABLE tenant_globex.orders IS 'Old comment';");

            var diff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--exit-code");

            Assert.AreEqual(2, diff.ExitCode, diff.Output);
            StringAssert.Contains(diff.Output, "Shared schemas:\n  No changes.".ReplaceLineEndings());
            StringAssert.Contains(diff.Output, "Tenants: 3, their objects shown in the template's stand-in schema, tenant.");
            StringAssert.Contains(diff.Output, "1 tenant(s): tenant_globex");
            StringAssert.Contains(diff.Output, "  - Index tenant.orders_status_idx");
            StringAssert.Contains(diff.Output, "Comment: 'Old comment' -> 'Orders placed.'");
            StringAssert.Contains(diff.Output, "1 new tenant(s), with no schema yet; publish creates each with the template's");
            StringAssert.Contains(diff.Output, "tenant_newco");
            StringAssert.Contains(diff.Output, "1 tenant(s) up to date: tenant_acme");
            StringAssert.Contains(diff.Output, "tenant_old is no longer a tenant");
            StringAssert.Contains(diff.Output, "drop it by other means (for example DROP SCHEMA tenant_old CASCADE)");
            Assert.IsFalse(diff.Output.Contains("tenant_acme.", StringComparison.Ordinal), "a tenant's objects are shown in the stand-in schema");

            var json = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--format", "json");
            Assert.AreEqual(0, json.ExitCode, json.Output);
            StringAssert.Contains(json.Output, "\"standInSchema\": \"tenant\"");
            StringAssert.Contains(json.Output, "\"tenantGroups\": [");
            StringAssert.Contains(json.Output, "\"id\": \"tenant.orders_status_idx\"");
            StringAssert.Contains(json.Output, "\"schema\": \"tenant_newco\"");
            StringAssert.Contains(json.Output, "\"upToDate\": [\n    \"tenant_acme\"".ReplaceLineEndings());
            StringAssert.Contains(json.Output, "\"formerTenants\": [\n    \"tenant_old\"".ReplaceLineEndings());

            var acme = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--exit-code", "--tenants", "acme");
            Assert.AreEqual(0, acme.ExitCode, acme.Output);
            StringAssert.Contains(acme.Output, "1 tenant(s) up to date: tenant_acme");

            var unknown = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--tenants", "tenant_acme");
            Assert.AreEqual(1, unknown.ExitCode, unknown.Output);
            StringAssert.Contains(unknown.Output, "--tenants takes tenant identifiers (acme, not tenant_acme)");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ThePrefixAloneComparesTheSchemasTheDatabaseHasAndGroupsTenantsThatChangeAlike()
    {
        var databaseName = await CreateDatabaseAsync("g290p");
        var projectDirectory = CreateTenantProject("g290p", string.Empty);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            await SetUpAsync(connectionString, "acme", "globex", "initech");
            await ExecuteAsync(connectionString, "ALTER TABLE tenant_acme.customers ADD COLUMN email text; ALTER TABLE tenant_globex.customers ADD COLUMN email text;");
            await ExecuteAsync(connectionString, "CREATE SCHEMA tenantx; CREATE TABLE tenantx.t (id int); CREATE SCHEMA tenant; CREATE TABLE tenant.t (id int);");

            var diff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);

            Assert.AreEqual(0, diff.ExitCode, diff.Output);
            StringAssert.Contains(diff.Output, "Tenants: 3,");
            StringAssert.Contains(diff.Output, "2 tenant(s): tenant_acme, tenant_globex");
            StringAssert.Contains(diff.Output, "1 tenant(s) up to date: tenant_initech");
            Assert.IsFalse(diff.Output.Contains("changed differently", StringComparison.Ordinal), diff.Output);
            Assert.IsFalse(diff.Output.Contains("no longer a tenant", StringComparison.Ordinal), "the prefix alone has no former tenants");
            StringAssert.Contains(diff.Output, "Table tenantx.t", "a schema not named prefix_ is shared, so its table is to be removed");
            Assert.IsFalse(diff.Output.Contains("Table tenant.t", StringComparison.Ordinal), "a real schema named as the stand-in is neither shared nor a tenant");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AQueryListsTheTenantsOnceItsTableExists()
    {
        var databaseName = await CreateDatabaseAsync("g290q");
        var projectDirectory = CreateTenantProject("g290q", """, "query": "SELECT slug FROM platform.tenants WHERE active" """);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            await SetUpAsync(connectionString, "acme", "globex");

            var before = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--exit-code");
            Assert.AreEqual(0, before.ExitCode, before.Output);
            StringAssert.Contains(before.Output, "Tenants: not read (see the warning).");
            StringAssert.Contains(before.Output, "The tenant query could not run (42P01");
            StringAssert.Contains(before.Output, "They are read once the shared project is published.");

            await ExecuteAsync(connectionString, "CREATE TABLE platform.tenants (slug text PRIMARY KEY, active bool NOT NULL); INSERT INTO platform.tenants VALUES ('acme', true), ('globex', false), ('newco', true);");
            File.WriteAllText(Path.Combine(projectDirectory, "sql", "tenants.sql"), "CREATE TABLE platform.tenants (slug text PRIMARY KEY, active bool NOT NULL);");

            var after = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, after.ExitCode, after.Output);
            StringAssert.Contains(after.Output, "Shared schemas:\n  No changes.".ReplaceLineEndings());
            StringAssert.Contains(after.Output, "Tenants: 2,");
            StringAssert.Contains(after.Output, "1 tenant(s) up to date: tenant_acme");
            StringAssert.Contains(after.Output, "1 new tenant(s)");
            StringAssert.Contains(after.Output, "tenant_globex is no longer a tenant");

            Assert.AreEqual(1, (await RunWithATwoColumnQueryAsync(projectDirectory, projectFile, connectionString)).ExitCode);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    /// <summary>A query that returns two columns is an error, not a guess at which one holds the identifiers.</summary>
    private static async Task<(int ExitCode, string Output)> RunWithATwoColumnQueryAsync(string projectDirectory, string projectFile, string connectionString)
    {
        var json = File.ReadAllText(projectFile).Replace("SELECT slug FROM", "SELECT slug, active FROM", StringComparison.Ordinal);
        File.WriteAllText(projectFile, json);
        var result = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
        StringAssert.Contains(result.Output, "The tenant query returns 2 columns; it must return one column");
        return result;
    }

    /// <summary>The shared schema, then each tenant: its schema, and the template run in it as a publish of the tenant would.</summary>
    private static async Task SetUpAsync(string connectionString, params string[] identifiers)
    {
        await ExecuteAsync(connectionString, Shared);
        foreach (var identifier in identifiers)
        {
            var schema = "tenant_" + identifier;
            await ExecuteAsync(
                connectionString,
                $"CREATE SCHEMA {schema}; SET search_path TO {schema}, platform, public; " + Template.Replace("ON SCHEMA tenant", $"ON SCHEMA {schema}", StringComparison.Ordinal) + " RESET search_path;");
        }
    }

    private static string CreateTenantProject(string prefix, string tenantSource)
    {
        var directory = CreateProjectDirectory(prefix);
        File.WriteAllText(
            ProjectFile(directory),
            $$"""{ "name": "{{prefix}}", "version": "1.0", "source": "./sql", "searchPath": ["platform", "public"], "tenants": { "template": "./sql/Tenant", "prefix": "tenant"{{tenantSource}} } }""");
        Directory.CreateDirectory(Path.Combine(directory, "sql", "Tenant"));
        File.WriteAllText(Path.Combine(directory, "sql", "platform.sql"), Shared);
        File.WriteAllText(Path.Combine(directory, "sql", "Tenant", "orders.sql"), Template);
        return directory;
    }
}
