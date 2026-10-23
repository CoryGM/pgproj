using System.CommandLine;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-290a: <c>build</c> reads a project's tenant template once, in the stand-in schema, beside the shared project, says where the tenants come from, and
/// reports what the template may not do. <c>diff</c> (G-290b) and <c>publish</c> (G-290c) handle it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TenantBuildTests
{
    private const string SharedSql = """
        CREATE SCHEMA platform;
        CREATE TABLE platform.currencies (code char(3) PRIMARY KEY);
        """;

    private const string TemplateSql = """
        CREATE TABLE customers (id bigint PRIMARY KEY, name text NOT NULL);
        CREATE TABLE orders (id bigint PRIMARY KEY, customer_id bigint NOT NULL REFERENCES customers (id), currency char(3) NOT NULL REFERENCES platform.currencies (code));
        CREATE VIEW order_count AS SELECT customer_id, count(*) AS n FROM orders GROUP BY customer_id;
        GRANT USAGE ON SCHEMA tenant TO PUBLIC;
        """;

    [TestMethod]
    public async Task AProjectWithAFileOfTenantsBuildsAndSaysWhereTheTenantsComeFrom()
    {
        using var project = TempProject.Create(""", "file": "./tenants.csv" """, TemplateSql);

        var (exitCode, output) = await RunAsync("build", "--project", project.File);

        Assert.AreEqual(0, exitCode, output);
        StringAssert.Contains(output, "SQL files: 1");
        StringAssert.Contains(output, "(1 SQL file(s)), checked in the stand-in schema tenant");
        StringAssert.Contains(output, "Tenants: 2 listed in");
        StringAssert.Contains(output, "each in schema tenant_<identifier>");
    }

    [TestMethod]
    [DataRow("", "Tenants: the database's schemas named tenant_...")]
    [DataRow(""", "query": "SELECT id FROM platform.tenants" """, "Tenants: the identifiers the tenant query returns")]
    public async Task TheOtherSourcesOfTenantsAreNamed(string source, string expected)
    {
        using var project = TempProject.Create(source, TemplateSql);

        var (exitCode, output) = await RunAsync("build", "--project", project.File);

        Assert.AreEqual(0, exitCode, output);
        StringAssert.Contains(output, expected);
    }

    [TestMethod]
    public async Task ATemplateThatChangesASharedSchemaFailsTheBuild()
    {
        using var project = TempProject.Create(string.Empty, TemplateSql + "CREATE TABLE platform.audit (id int);");

        var (exitCode, output) = await RunAsync("build", "--project", project.File);

        Assert.AreEqual(1, exitCode, output);
        StringAssert.Contains(output, Path.Combine("sql", "Tenant", "t.sql") + ":4:40-4:76: [Error] Tenant template: Table platform.audit is in the shared schema 'platform'");
    }

    [TestMethod]
    public async Task ABadTenantFileFailsTheBuildWithItsLine()
    {
        using var project = TempProject.Create(""", "file": "./tenants.csv" """, TemplateSql, "tenant\nacme\nacme\n");

        var (exitCode, output) = await RunAsync("build", "--project", project.File);

        Assert.AreEqual(1, exitCode, output);
        StringAssert.Contains(output, "tenants.csv:3: the tenant 'acme' is listed again (first on line 2)");
    }

    [TestMethod]
    public async Task TheDiagramDrawsTheTemplateOnceInTheStandInSchema()
    {
        using var project = TempProject.Create(string.Empty, TemplateSql);

        var (exitCode, output) = await RunAsync("build", "--project", project.File, "--erd", "model");

        Assert.AreEqual(0, exitCode, output);
        var diagram = File.ReadAllText(Path.Combine(project.Directory, "model.mmd"));
        StringAssert.Contains(diagram, "\"tenant.orders\"");
        StringAssert.Contains(diagram, "\"platform.currencies\" ||--o{ \"tenant.orders\"");
    }

    /// <summary>G-290b and G-290c replaced G-290a's refusals: diff and publish now handle a project with tenants, so they connect (and here fail to).</summary>
    [TestMethod]
    [DataRow("diff")]
    [DataRow("publish")]
    public async Task DiffAndPublishHandleTenantsSoTheyConnect(string command)
    {
        using var project = TempProject.Create(string.Empty, TemplateSql);

        var (exitCode, output) = await RunAsync(command, "--project", project.File, "--target-connection", "Host=127.0.0.1;Port=1;Database=x;Username=x;Timeout=1");

        Assert.AreEqual(1, exitCode, output);
        Assert.IsFalse(output.Contains("this project has tenants", StringComparison.Ordinal), output);
    }

    [TestMethod]
    [DataRow("diff", "Diff failed: --tenants is only for a project with tenants")]
    [DataRow("publish", "Publish failed: --tenants is only for a project with tenants")]
    public async Task TenantsIsOnlyForAProjectWithTenants(string command, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-tenants-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path.Combine(directory, "sql"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "pgproj.json"), """{ "name": "plain", "version": "1.0", "source": "./sql" }""");
            File.WriteAllText(Path.Combine(directory, "sql", "t.sql"), "CREATE TABLE t (id int);");

            var (exitCode, output) = await RunAsync(command, "--project", Path.Combine(directory, "pgproj.json"), "--target-connection", "Host=127.0.0.1;Port=1;Timeout=1", "--tenants", "acme");

            Assert.AreEqual(1, exitCode, output);
            StringAssert.Contains(output, expected);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void TheHelpSaysWhatEachCommandDoesWithTenants()
    {
        var context = new CliContext();

        StringAssert.Contains(BuildCommand.Create(context).Description!, "has its template read once, in the stand-in schema (the prefix), and checked");
        StringAssert.Contains(BuildCommand.Create(context).Description!, "docs/notes/tenants.md");
        StringAssert.Contains(DiffCommand.Create(context).Description!, "is compared in parts: the shared schemas once, then each tenant's schema against the template");
        StringAssert.Contains(DiffCommand.Create(context).Description!, "--tenants acme,globex");
        Assert.IsTrue(DiffCommand.Create(context).Options.Any(static option => option.Name == "--tenants"));
        StringAssert.Contains(PublishCommand.Create(context).Description!, "is published in parts: the shared changes in one transaction, with the pre- and post-deploy scripts, then each tenant in a transaction of its own");
        StringAssert.Contains(PublishCommand.Create(context).Description!, "--execute --tenants acme,globex");
        Assert.IsTrue(PublishCommand.Create(context).Options.Any(static option => option.Name == "--tenants"));
        StringAssert.Contains(PublishCommand.Create(context).Description!, "A tenant's own scripts (tenants.predeploy and tenants.postdeploy) run in its transaction");
        StringAssert.Contains(PublishCommand.Create(context).Description!, "-- pgproj: run when=tenant-changed or when=tenant-created");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var context = new CliContext();
        var root = new RootCommand("pgproj") { BuildCommand.Create(context), DiffCommand.Create(context), PublishCommand.Create(context) };
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetDirectoryName(args[Array.IndexOf(args, "--project") + 1])!;
            return (await root.Parse(args).InvokeAsync(), output.ToString());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class TempProject : IDisposable
    {
        private TempProject(string directory)
        {
            Directory = directory;
            File = Path.Combine(directory, "pgproj.json");
        }

        public string Directory { get; }

        public string File { get; }

        public static TempProject Create(string tenantSource, string templateSql, string tenantsCsv = "tenant\nacme\nglobex\n")
        {
            var directory = Path.Combine(Path.GetTempPath(), "pgproj-tenants-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path.Combine(directory, "sql", "Tenant"));
            System.IO.File.WriteAllText(
                Path.Combine(directory, "pgproj.json"),
                $$"""{ "name": "shop", "version": "1.0", "source": "./sql", "tenants": { "template": "./sql/Tenant", "prefix": "tenant"{{tenantSource}} } }""");
            System.IO.File.WriteAllText(Path.Combine(directory, "sql", "shared.sql"), SharedSql);
            System.IO.File.WriteAllText(Path.Combine(directory, "sql", "Tenant", "t.sql"), templateSql);
            System.IO.File.WriteAllText(Path.Combine(directory, "tenants.csv"), tenantsCsv);
            return new TempProject(directory);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
