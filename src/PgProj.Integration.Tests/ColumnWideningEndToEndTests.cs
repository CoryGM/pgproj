using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-281 against a real server (approved by the user 2026-10-13). Widening a column (<c>varchar(50)</c> to <c>varchar(100)</c>, <c>numeric(10,2)</c> to
/// <c>numeric(12,2)</c>, <c>int</c> to <c>bigint</c>) was refused as destructive, so it needed <c>--allow-destructive</c>, which also let a dropped column
/// through. It now runs without it, keeps the data, and is still named before it runs when it rewrites the table; a narrowing is still refused.
/// </summary>
[TestClass]
public sealed class ColumnWideningEndToEndTests
{
    [TestMethod]
    public async Task AWideningRunsWithoutAllowDestructiveAndANarrowingIsStillRefused()
    {
        var databaseName = await CreateDatabaseAsync("g281");
        var projectDirectory = CreateProjectDirectory("g281");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int PRIMARY KEY, name varchar(50), amt numeric(10,2), qty int);\n");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1, 'Ada', 12345678.91, 2147483647);");

            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int PRIMARY KEY, name varchar(100), amt numeric(12,2), qty bigint);\n");
            var widened = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, widened.ExitCode, "a widening needed --allow-destructive.\n" + widened.Output);
            StringAssert.Contains(widened.Output, "public.t: ALTER COLUMN qty TYPE rewrites the table");
            Assert.AreEqual(
                "character varying(100),numeric(12,2),bigint",
                await ScalarTextAsync(connectionString, "SELECT string_agg(format_type(atttypid, atttypmod), ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid = 'public.t'::regclass AND attname IN ('name', 'amt', 'qty')"));
            Assert.AreEqual("Ada|12345678.91|2147483647", await ScalarTextAsync(connectionString, "SELECT name || '|' || amt || '|' || qty FROM public.t"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int PRIMARY KEY, name varchar(10), amt numeric(12,2), qty bigint);\n");
            var narrowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, narrowed.ExitCode, narrowed.Output);
            StringAssert.Contains(narrowed.Output, "column \"name\" type changed");
            Assert.AreEqual("character varying(100)", await ScalarTextAsync(connectionString, "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.t'::regclass AND attname = 'name'"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
