using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-288 against a real server (approved by the user 2026-10-14). A domain check PostgreSQL stores with a cast the written text lacks (<c>length(VALUE)</c> on a
/// <c>varchar</c> domain, on a domain over one, on the quoted <c>"Name"</c> of G-286) showed as modified after every publish. Each converges now, a real change to a
/// check is still found and published.
/// </summary>
[TestClass]
public sealed class DomainCheckRenderingEndToEndTests
{
    private const string Schema = """
        CREATE DOMAIN public.v50 AS varchar(50) CHECK (length(VALUE) < 10);
        CREATE DOMAIN public.nm AS character varying(50);
        CREATE DOMAIN public.short2 AS nm CHECK (length(VALUE) < 10);
        CREATE DOMAIN public."Name" AS character varying(50);
        CREATE DOMAIN public.short_name AS "Name" CHECK (length(VALUE) < 10) CHECK (VALUE <> '');
        CREATE TABLE public.person (id int PRIMARY KEY, a public.v50, b public.short2, c public.short_name);

        """;

    [TestMethod]
    public async Task ADomainCheckWithAnImplicitCastConvergesAndARealChangeIsPublished()
    {
        var databaseName = await CreateDatabaseAsync("g288");
        var projectDirectory = CreateProjectDirectory("g288");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, Edit(Schema, "CREATE DOMAIN public.v50 AS varchar(50) CHECK (length(VALUE) < 10);", "CREATE DOMAIN public.v50 AS varchar(50) CHECK (length(VALUE) < 20);"));
            var changed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(changed.Output, "~ Type public.v50");
            StringAssert.Contains(changed.Output, "< 20)");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            Assert.AreEqual(
                "CHECK ((length((VALUE)::text) < 20))",
                await ScalarTextAsync(connectionString, "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE contypid = 'public.v50'::regtype"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
