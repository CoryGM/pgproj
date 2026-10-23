using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-290e against a real server: a tenant's pre- and post-deploy scripts run in its transaction, with psql's variables filled in and the tenant's settings
/// set; a file marked <c>tenant-created</c> runs once per tenant, <c>tenant-changed</c> only with changes, an unmarked one every time; a project's file
/// marked <c>changed</c> runs only with shared changes; a script that fails rolls its tenant back; <c>--comments-only</c> runs no script.
/// </summary>
[TestClass]
public sealed class TenantScriptsEndToEndTests
{
    private const string Shared = """
        CREATE SCHEMA platform;
        CREATE TABLE platform.audit (tenant text NOT NULL, note text NOT NULL);
        CREATE TABLE platform.publishes (n int);
        """;

    [TestMethod]
    public async Task EachFileRunsWhenItsMarkerSays()
    {
        var databaseName = await CreateDatabaseAsync("g290e");
        var projectDirectory = CreateProject("g290e", """, "file": "./tenants.csv", "postdeploy": "./scripts/tenant/post.sql" """, "tenant\nacme\nglobex\n");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            async Task<string> AuditAsync() => await ScalarTextAsync(connectionString, "select coalesce(string_agg(tenant || ':' || note, ',' order by tenant, note), '') from platform.audit");

            var first = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, first.ExitCode, first.Output);
            Assert.AreEqual("acme:changed,acme:ran in tenant_acme,globex:changed,globex:ran in tenant_globex", await AuditAsync());
            Assert.AreEqual("1|1", await ScalarTextAsync(connectionString, "select (select count(*) from tenant_acme.customers) || '|' || (select count(*) from tenant_globex.customers)"), "seeded once each");
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "select count(*)::text from platform.publishes"), "the shared changed file ran with the shared changes");

            await ExecuteAsync(connectionString, "TRUNCATE platform.audit;");
            var second = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, second.ExitCode, second.Output);
            StringAssert.Contains(second.Output, "0 of them with changes; 2 ran only their scripts.");
            Assert.AreEqual("acme:ran in tenant_acme,globex:ran in tenant_globex", await AuditAsync(), "only the unmarked file ran");
            Assert.AreEqual("1|1", await ScalarTextAsync(connectionString, "select (select count(*) from tenant_acme.customers) || '|' || (select count(*) from tenant_globex.customers)"));
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "select count(*)::text from platform.publishes"), "no shared changes, so the changed file did not run");

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(dryRun.Output, "-- pgproj: skipped seed.sql (it runs when=tenant-created)");
            StringAssert.Contains(dryRun.Output, "SET LOCAL pgproj.tenant_id = 'globex';");

            var commentsOnly = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--comments-only");
            Assert.AreEqual(0, commentsOnly.ExitCode, commentsOnly.Output);
            Assert.AreEqual("acme:ran in tenant_acme,globex:ran in tenant_globex", await AuditAsync(), "--comments-only runs no script");

            File.AppendAllText(Path.Combine(projectDirectory, "tenants.csv"), "newco\n");
            File.WriteAllText(Path.Combine(projectDirectory, "scripts", "tenant", "always.sql"), "INSERT INTO platform.audit (tenant, note) SELECT :'tenant_id', 'ran in ' || current_schema() WHERE :'tenant_id' <> 'newco';\nSELECT 1 / (CASE WHEN :'tenant_id' = 'globex' THEN 0 ELSE 1 END);\n");
            var failed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, failed.ExitCode, failed.Output);
            StringAssert.Contains(failed.Output, "Publish failed in tenant_globex: 22012");
            StringAssert.Contains(failed.Output, "Not started: tenant_newco.");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "select count(*)::text from pg_namespace where nspname = 'tenant_newco'"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AnEmptySchemaTheApplicationCreatedIsSeededTheFirstTime()
    {
        var databaseName = await CreateDatabaseAsync("g290ep");
        var projectDirectory = CreateProject("g290ep", """, "predeploy": "./scripts/tenant/seed-pre.sql", "postdeploy": "./scripts/tenant/post.sql" """, null);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "scripts", "tenant", "seed-pre.sql"), "-- pgproj: run when=tenant-created\nINSERT INTO platform.audit VALUES (current_setting('pgproj.tenant_schema'), 'pre');\n");
            await ExecuteAsync(connectionString, Shared + "CREATE SCHEMA tenant_acme;");

            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "select count(*)::text from tenant_acme.customers"), "seeded once, when pgproj first filled the schema");
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "select count(*)::text from platform.audit where note = 'pre'"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static string CreateProject(string prefix, string tenantSettings, string? tenantsCsv)
    {
        var directory = CreateProjectDirectory(prefix);
        File.WriteAllText(
            ProjectFile(directory),
            $$"""{ "name": "{{prefix}}", "version": "1.0", "source": "./sql", "searchPath": ["platform", "public"], "postdeploy": "./scripts/shared-post.sql", "tenants": { "template": "./sql/Tenant", "prefix": "tenant"{{tenantSettings}} } }""");
        Directory.CreateDirectory(Path.Combine(directory, "sql", "Tenant"));
        Directory.CreateDirectory(Path.Combine(directory, "scripts", "tenant"));
        File.WriteAllText(Path.Combine(directory, "sql", "platform.sql"), Shared);
        File.WriteAllText(Path.Combine(directory, "sql", "Tenant", "customers.sql"), "CREATE TABLE customers (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL UNIQUE);");
        File.WriteAllText(Path.Combine(directory, "scripts", "shared-post.sql"), "-- pgproj: run when=changed\nINSERT INTO platform.publishes VALUES (1);\n");
        File.WriteAllText(Path.Combine(directory, "scripts", "tenant", "post.sql"), ":r seed.sql\n:r audit.sql\n:r always.sql\n");
        File.WriteAllText(Path.Combine(directory, "scripts", "tenant", "seed.sql"), "-- Seed a new tenant.\n-- pgproj: run when=tenant-created\nINSERT INTO :\"tenant_schema\".customers (name) VALUES ('Walk-in customer');\n");
        File.WriteAllText(Path.Combine(directory, "scripts", "tenant", "audit.sql"), "-- pgproj: run when=tenant-changed\nINSERT INTO platform.audit (tenant, note) VALUES (:'tenant_id', 'changed');\n");
        File.WriteAllText(Path.Combine(directory, "scripts", "tenant", "always.sql"), "INSERT INTO platform.audit (tenant, note) VALUES (current_setting('pgproj.tenant_id'), 'ran in ' || current_schema());\n");
        if (tenantsCsv is not null)
        {
            File.WriteAllText(Path.Combine(directory, "tenants.csv"), tenantsCsv);
        }

        return directory;
    }
}
