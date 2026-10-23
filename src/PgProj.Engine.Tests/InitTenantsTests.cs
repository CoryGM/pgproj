using PgProj.Core;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-290d: <c>init --tenant-prefix</c> moves the chosen tenant's files into the template folder with the schema name taken out, leaves out what belongs to the whole
/// database, writes the CSV file of tenants, and takes its tenant options only with the prefix.
/// </summary>
[TestClass]
public sealed class InitTenantsTests
{
    [TestMethod]
    public void TheTenantsFilesBecomeTheTemplate()
    {
        ScaffoldedFile[] files =
        [
            new("Schema/tenant_acme/tenant_acme.sql", "CREATE SCHEMA IF NOT EXISTS tenant_acme;\nGRANT USAGE ON SCHEMA tenant_acme TO PUBLIC;\n"),
            new("Schema/tenant_acme/Table/orders.sql", "CREATE TABLE tenant_acme.orders (id int);\n"),
            new("Schema/tenant_acme/Function/f.sql", "CREATE FUNCTION tenant_acme.f() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM tenant_acme.orders $$;\n"),
            new("Extension/hstore.sql", "CREATE EXTENSION hstore SCHEMA tenant_acme;\n"),
        ];

        var template = InitTenants.ToTemplate(files, "tenant_acme", "tenant");

        CollectionAssert.AreEqual(new[] { "Tenant/tenant.sql", "Tenant/Table/orders.sql", "Tenant/Function/f.sql" }, template.Files.Select(static file => file.RelativePath).ToArray());
        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS tenant;\nGRANT USAGE ON SCHEMA tenant TO PUBLIC;\n", template.Files[0].Content);
        Assert.AreEqual("CREATE TABLE orders (id int);\n", template.Files[1].Content);
        CollectionAssert.AreEqual(new[] { "Extension/hstore.sql" }, template.LeftOut.ToArray());
        CollectionAssert.AreEqual(new[] { "Tenant/Function/f.sql" }, template.BodiesNamingSchema.ToArray());
    }

    [TestMethod]
    public void TheTenantFileHasAHeaderAndQuotesWhatNeedsIt()
    {
        var csv = InitTenants.BuildCsv(["acme", "a,b", "say \"hi\""]);

        Assert.AreEqual("tenant\nacme\n\"a,b\"\n\"say \"\"hi\"\"\"\n", csv);
        CollectionAssert.AreEqual(new[] { "acme", "a,b", "say \"hi\"" }, TenantFileReader.Parse(csv, "t.csv", "tenant").Select(static entry => entry.Identifier).ToArray(), "it reads back");
    }
}
