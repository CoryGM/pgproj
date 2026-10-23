using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-290f against a real server: the sample project <c>samples/SchemaPerTenant</c>, copied to a temporary folder, does what its README says. The first
/// publish creates the shared schema and the three shops, seeds each shop once, and records it; the second changes nothing and runs no marked script;
/// <c>diff</c> is clean; a trigger of the template works in a shop; a shop added to <c>tenants.csv</c> is created and seeded alone.
/// </summary>
[TestClass]
public sealed class SchemaPerTenantSampleEndToEndTests
{
    [TestMethod]
    public async Task TheSamplePublishesConvergesAndTakesANewShop()
    {
        var databaseName = await CreateDatabaseAsync("g290f");
        var projectDirectory = CopySample();
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            async Task<string> EventsAsync() => await ScalarTextAsync(connectionString, "select string_agg(shop || ':' || event, ',' order by id) from platform.shop_events");

            var first = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, first.ExitCode, first.Output);
            StringAssert.Contains(first.Output, "Published: the shared schemas and 3 tenant(s), 3 of them with changes.");
            Assert.AreEqual(
                "*:shared schemas changed,acme:created,acme:schema updated in shop_acme,globex:created,globex:schema updated in shop_globex,initech:created,initech:schema updated in shop_initech",
                await EventsAsync());
            Assert.AreEqual("2|1", await ScalarTextAsync(connectionString, "select (select count(*) from shop_globex.customer_groups) || '|' || (select count(*) from shop_globex.customers)"));
            Assert.AreEqual("4", await ScalarTextAsync(connectionString, "select count(*)::text from platform.currencies"));

            var second = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, second.ExitCode, second.Output);
            StringAssert.Contains(second.Output, "3 tenant(s), 0 of them with changes.");
            Assert.AreEqual(7, (await EventsAsync()).Split(',').Length, "no marked script ran again");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            await ExecuteAsync(connectionString, """
                INSERT INTO shop_acme.products (sku, name, price, currency, updated_at) VALUES ('h-1', 'Hammer', 12.50, 'USD', '2000-01-01');
                UPDATE shop_acme.products SET price = 13.00 WHERE sku = 'h-1';
                """);
            Assert.AreEqual("true", await ScalarTextAsync(connectionString, "select (updated_at > '2001-01-01')::text from shop_acme.products where sku = 'h-1'"), "the template's trigger set updated_at");

            File.AppendAllText(Path.Combine(projectDirectory, "tenants.csv"), "umbrella,Umbrella Pharmacy,pro\n");
            var added = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, added.ExitCode, added.Output);
            StringAssert.Contains(added.Output, "4 tenant(s), 1 of them with changes.");
            Assert.AreEqual("2|1", await ScalarTextAsync(connectionString, "select (select count(*) from shop_umbrella.customer_groups) || '|' || (select count(*) from shop_umbrella.customers)"));
            Assert.AreEqual("umbrella:created,umbrella:schema updated in shop_umbrella", await ScalarTextAsync(connectionString, "select string_agg(shop || ':' || event, ',' order by id) from platform.shop_events where id > 7"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    /// <summary>A copy of the sample, without its generated documentation, in a temporary folder.</summary>
    private static string CopySample()
    {
        var source = Path.Combine(FindRepositoryRoot(), "samples", "SchemaPerTenant");
        var target = Path.Combine(Path.GetTempPath(), $"pgproj-g290f-{Guid.NewGuid():N}");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("docs" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "pgproj.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("pgproj.slnx was not found above " + AppContext.BaseDirectory);
    }
}
