namespace PgProj.Core.Tests;

/// <summary>
/// G-290: <c>tenants</c> in <c>pgproj.json</c> names a template folder, the prefix of the tenants' schema names (also the template's stand-in schema),
/// and at most one of a query and a CSV file. The template is not read as part of the shared project.
/// </summary>
[TestClass]
public sealed class PgProjectLoaderTenantsTests
{
    [TestMethod]
    public void AProjectWithoutTenantsHasNone()
        => Assert.IsNull(Load(string.Empty).Tenants);

    [TestMethod]
    public void ThePrefixAloneFindsTenantsByTheirSchemas()
    {
        var tenants = Load(""", "tenants": { "template": "sql/Tenant", "prefix": "Tenant" }""").Tenants!;

        Assert.AreEqual(TenantSource.Schemas, tenants.Source);
        Assert.AreEqual("tenant", tenants.Prefix, "an unquoted name in pgproj.json is folded, as PostgreSQL folds it (G-206)");
        Assert.AreEqual("tenant", tenants.StandInSchema);
        Assert.AreEqual("tenant_acme", tenants.SchemaFor("acme"));
        Assert.IsTrue(tenants.IsTenantSchemaName("tenant_acme"));
        Assert.IsFalse(tenants.IsTenantSchemaName("tenant"));
        Assert.IsFalse(tenants.IsTenantSchemaName("tenant_"));
        Assert.IsFalse(tenants.IsTenantSchemaName("tenants"));
    }

    [TestMethod]
    public void AQuotedPrefixKeepsItsCase()
        => Assert.AreEqual("Tenant", Load(""", "tenants": { "template": "sql/Tenant", "prefix": "\"Tenant\"" }""").Tenants!.Prefix);

    [TestMethod]
    public void TheTemplateIsReadOnceAndNotAsPartOfTheSharedProject()
    {
        var project = Load(""", "tenants": { "template": "sql/Tenant", "prefix": "tenant" }""", out var directory);

        CollectionAssert.AreEqual(new[] { Path.Combine(directory, "sql", "shared.sql") }, project.SqlFiles.ToArray());
        CollectionAssert.AreEqual(
            new[] { Path.Combine(directory, "sql", "Tenant", "a.sql"), Path.Combine(directory, "sql", "Tenant", "sub", "b.sql") },
            project.Tenants!.TemplateFiles.ToArray());
    }

    [TestMethod]
    public void AQueryListsTheTenants()
    {
        var tenants = Load(""", "tenants": { "template": "sql/Tenant", "prefix": "tenant", "query": " SELECT id FROM platform.tenants " }""").Tenants!;

        Assert.AreEqual(TenantSource.Query, tenants.Source);
        Assert.AreEqual("SELECT id FROM platform.tenants", tenants.Query);
    }

    [TestMethod]
    public void AFileListsTheTenants()
    {
        var tenants = Load(""", "tenants": { "template": "sql/Tenant", "prefix": "tenant", "file": "tenants.csv" }""").Tenants!;

        Assert.AreEqual(TenantSource.File, tenants.Source);
        CollectionAssert.AreEqual(new[] { "acme", "Globex" }, tenants.FileTenants.Select(static entry => entry.Identifier).ToArray());
    }

    [TestMethod]
    [DataRow("""{ "prefix": "tenant" }""", "missing 'template'")]
    [DataRow("""{ "template": "sql/Missing", "prefix": "tenant" }""", "does not exist")]
    [DataRow("""{ "template": "sql", "prefix": "tenant" }""", "is, or holds, the source folder")]
    [DataRow("""{ "template": ".", "prefix": "tenant" }""", "is, or holds, the source folder")]
    [DataRow("""{ "template": "sql/Tenant" }""", "missing 'prefix'")]
    [DataRow("""{ "template": "sql/Tenant", "prefix": "pg_t" }""", "starts with pg_")]
    [DataRow("""{ "template": "sql/Tenant", "prefix": "public" }""", "cannot be 'public'")]
    [DataRow("""{ "template": "sql/Tenant", "prefix": "tenant", "query": "SELECT 1", "file": "tenants.csv" }""", "both 'query' and 'file'")]
    [DataRow("""{ "template": "sql/Tenant", "prefix": "tenant", "file": "missing.csv" }""", "does not exist")]
    public void AnInvalidTenantsBlockIsRefused(string tenants, string expected)
    {
        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => Load(""", "tenants": """ + tenants));

        StringAssert.Contains(exception.Message, expected);
    }

    [TestMethod]
    public void APrefixTooLongForATenantSchemaIsRefused()
    {
        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => Load($$""", "tenants": { "template": "sql/Tenant", "prefix": "{{new string('p', 62)}}" }"""));

        StringAssert.Contains(exception.Message, "62 bytes long");
    }

    [TestMethod]
    [DataRow(""", "schemas": ["tenant"]""", "'schemas' lists 'tenant', the tenant prefix")]
    [DataRow(""", "schemas": ["app", "tenant_acme"]""", "'schemas' lists 'tenant_acme'")]
    [DataRow(""", "searchPath": ["tenant", "public"]""", "'searchPath' lists 'tenant'")]
    public void SchemasAndSearchPathCannotNameTheStandInOrATenant(string extra, string expected)
    {
        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => Load(extra + """, "tenants": { "template": "sql/Tenant", "prefix": "tenant" }"""));

        StringAssert.Contains(exception.Message, expected);
    }

    [TestMethod]
    public void TheWorkspaceReadsTheTemplateApart()
    {
        var directory = CreateProject(""", "tenants": { "template": "sql/Tenant", "prefix": "tenant" }""");
        try
        {
            var loader = new PgProjectLoader();
            var workspace = loader.LoadWorkspace(loader.Load(Path.Combine(directory, "pgproj.json")));

            Assert.AreEqual(1, workspace.Documents.Count);
            CollectionAssert.AreEqual(new[] { "CREATE TABLE a (id int);", "CREATE TABLE b (id int);" }, workspace.TemplateDocuments.Select(static document => document.Content).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PgProject Load(string extraProperties) => Load(extraProperties, out _);

    private static PgProject Load(string extraProperties, out string directory)
    {
        directory = CreateProject(extraProperties);
        try
        {
            return new PgProjectLoader().Load(Path.Combine(directory, "pgproj.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateProject(string extraProperties)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pgproj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "sql", "Tenant", "sub"));
        File.WriteAllText(Path.Combine(directory, "sql", "shared.sql"), "CREATE SCHEMA app;");
        File.WriteAllText(Path.Combine(directory, "sql", "Tenant", "a.sql"), "CREATE TABLE a (id int);");
        File.WriteAllText(Path.Combine(directory, "sql", "Tenant", "sub", "b.sql"), "CREATE TABLE b (id int);");
        File.WriteAllText(Path.Combine(directory, "tenants.csv"), "tenant\nacme\nGlobex\n");
        File.WriteAllText(Path.Combine(directory, "pgproj.json"), $$"""{ "name": "test", "version": "1.0.0", "source": "sql"{{extraProperties}} }""");
        return directory;
    }
}
