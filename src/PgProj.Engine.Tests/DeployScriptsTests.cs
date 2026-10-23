using PgProj.Core;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-290e: which files of a deploy script run for a part of a publish, and a tenant's values for psql's variables. A project's file marked <c>when=changed</c>
/// runs when there are changes; a tenant's <c>tenant-changed</c> file when the tenant has changes, <c>tenant-created</c> when pgproj creates its schema or
/// it has no objects yet; an unmarked file always. A tenant's script starts by creating a new tenant's schema and setting the tenant's values.
/// </summary>
[TestClass]
public sealed class DeployScriptsTests
{
    private static readonly ChangeSet NoChanges = new() { Added = [], Removed = [], Modified = [] };

    [TestMethod]
    public void AProjectsChangedFileRunsOnlyWithChanges()
    {
        using var directory = new TempDirectory();
        var project = directory.Project(post: "-- pgproj: run when=changed\nINSERT INTO log VALUES (1);\n");

        Assert.IsNull(DeployScripts.ForProject(project, NoChanges).Post, "nothing runs, so there is no script");
        StringAssert.Contains(DeployScripts.ForProject(project, OneChange()).Post!, "INSERT INTO log VALUES (1);");
    }

    [TestMethod]
    public void ATenantsFilesRunByTheirMarkers()
    {
        using var directory = new TempDirectory();
        directory.Write("seed.sql", "-- pgproj: run when=tenant-created\nINSERT INTO :\"tenant_schema\".customers (name) VALUES ('walk-in');\n");
        directory.Write("audit.sql", "-- pgproj: run when=tenant-changed\nINSERT INTO platform.audit VALUES (:'tenant_id');\n");
        directory.Write("always.sql", "UPDATE :\"tenant_schema\".settings SET touched = now();\n");
        var tenants = directory.Tenants(post: ":r seed.sql\n:r audit.sql\n:r always.sql\n");

        var upToDate = DeployScripts.ForTenant(tenants, new TenantTarget("acme", "tenant_acme", Exists: true), NoChanges, LiveWithTable("tenant_acme"));
        var changed = DeployScripts.ForTenant(tenants, new TenantTarget("acme", "tenant_acme", Exists: true), OneChange(), LiveWithTable("tenant_acme"));
        var created = DeployScripts.ForTenant(tenants, new TenantTarget("newco", "tenant_newco", Exists: false), OneChange(), DatabaseModel.Empty);

        StringAssert.Contains(upToDate.Post!, "-- pgproj: skipped seed.sql (it runs when=tenant-created)");
        StringAssert.Contains(upToDate.Post!, "-- pgproj: skipped audit.sql (it runs when=tenant-changed)");
        StringAssert.Contains(upToDate.Post!, "UPDATE \"tenant_acme\".settings SET touched = now();");
        StringAssert.Contains(changed.Post!, "INSERT INTO platform.audit VALUES ('acme');");
        StringAssert.Contains(changed.Post!, "-- pgproj: skipped seed.sql");
        StringAssert.Contains(created.Post!, "INSERT INTO \"tenant_newco\".customers (name) VALUES ('walk-in');");
        StringAssert.Contains(created.Post!, "INSERT INTO platform.audit VALUES ('newco');");
    }

    [TestMethod]
    public void AnExistingSchemaWithNoObjectsIsCreatedForTheMarker()
    {
        using var directory = new TempDirectory();
        directory.Write("seed.sql", "-- pgproj: run when=tenant-created\nSELECT 1;\n");
        var tenants = directory.Tenants(post: ":r seed.sql\n");

        var firstFill = DeployScripts.ForTenant(tenants, new TenantTarget("acme", "tenant_acme", Exists: true), OneChange(), DatabaseModel.Empty);
        var filled = DeployScripts.ForTenant(tenants, new TenantTarget("acme", "tenant_acme", Exists: true), OneChange(), LiveWithTable("tenant_acme"));

        StringAssert.Contains(firstFill.Post!, "SELECT 1;");
        Assert.IsNull(filled.Post, "nothing runs once the schema has objects");
    }

    [TestMethod]
    public void ATenantsScriptStartsWithItsSchemaAndSettings()
    {
        using var directory = new TempDirectory();
        var tenants = directory.Tenants(pre: "SELECT 1;\n");

        var newTenant = DeployScripts.ForTenant(tenants, new TenantTarget("O'Neil", "tenant_O'Neil", Exists: false), NoChanges, DatabaseModel.Empty);
        var existing = DeployScripts.ForTenant(tenants, new TenantTarget("acme", "tenant_acme", Exists: true), NoChanges, LiveWithTable("tenant_acme"));

        StringAssert.StartsWith(newTenant.Pre!, "CREATE SCHEMA IF NOT EXISTS \"tenant_O'Neil\";" + Environment.NewLine + "SET LOCAL pgproj.tenant_schema = 'tenant_O''Neil';" + Environment.NewLine + "SET LOCAL pgproj.tenant_id = 'O''Neil';");
        StringAssert.StartsWith(existing.Pre!, "SET LOCAL pgproj.tenant_schema = 'tenant_acme';");
    }

    [TestMethod]
    public void ATenantWithNothingToRunHasNoScript()
    {
        using var directory = new TempDirectory();
        directory.Write("seed.sql", "-- pgproj: run when=tenant-created\nSELECT 1;\n");
        var tenants = directory.Tenants(post: ":r seed.sql\n");

        Assert.AreEqual((null, null), DeployScripts.ForTenant(tenants, new TenantTarget("acme", "tenant_acme", Exists: true), NoChanges, LiveWithTable("tenant_acme")));
    }

    [TestMethod]
    public void BuildChecksTheVariablesOfEachScript()
    {
        using var directory = new TempDirectory();
        var project = directory.Project(post: "INSERT INTO log VALUES (:'tenant_id');\n") with { Tenants = directory.Tenants(pre: "SELECT :'tenant_name', :'tenant_id';\n") };

        var errors = DeployScripts.Check(project, new PgQueryParser()).Select(static diagnostic => diagnostic.Message).ToArray();

        Assert.IsTrue(errors.Any(static message => message.StartsWith("The project's deploy script uses :'tenant_id': variables are only for a tenant's scripts", StringComparison.Ordinal)), string.Join(" | ", errors));
        Assert.IsTrue(errors.Any(static message => message.StartsWith("A tenant's script uses :'tenant_name', which pgproj does not set", StringComparison.Ordinal)), string.Join(" | ", errors));
    }

    private static ChangeSet OneChange()
    {
        var table = new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE TABLE public.t (id int);")]).Tables.Values.Single();
        return NoChanges with { Added = [new AddedChange { NodeId = table.Key, NodeKind = ChangeNodeKind.Table, Node = table }] };
    }

    private static DatabaseModel LiveWithTable(string schema)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse($"CREATE TABLE {schema}.customers (id int);")]);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pgproj-g290e-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string content)
        {
            var file = System.IO.Path.Combine(Path, name);
            File.WriteAllText(file, content);
            return file;
        }

        public PgProject Project(string post)
        {
            var script = ScriptIncludeExpander.Expand(Write("project-post.sql", post));
            return new PgProject("p", "1", [], Path, null, script.Root!.FilePath, [])
            {
                PostDeployScriptContent = script.Text,
                PostDeployScriptFiles = script.Files,
                PostDeployScriptParts = script.Root,
            };
        }

        public PgProjectTenants Tenants(string? pre = null, string? post = null)
            => new(Path, "tenant", TenantSource.Schemas)
            {
                PreDeploy = pre is null ? null : ScriptIncludeExpander.Expand(Write("tenant-pre.sql", pre)),
                PostDeploy = post is null ? null : ScriptIncludeExpander.Expand(Write("tenant-post.sql", post)),
            };

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
