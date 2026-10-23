using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-289 against a real server (approved as recommended, 2026-10-14). Renaming a column with an unnamed unique constraint, <c>CHECK</c>, or index dropped and added
/// them again, since their default names follow the column. They are now renamed in place: the database ends with the names a fresh deploy gives, the unique
/// index is the same one (its OID kept), the data is kept, and a second diff is empty.
/// </summary>
[TestClass]
public sealed class RenamedColumnGeneratedNameEndToEndTests
{
    [TestMethod]
    public async Task UnnamedConstraintsAndIndexesAreRenamedWithTheirColumn()
    {
        var databaseName = await CreateDatabaseAsync("g289");
        var projectDirectory = CreateProjectDirectory("g289");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int PRIMARY KEY, email text UNIQUE CHECK (email <> ''), code text);\nCREATE INDEX ON public.t (code);\n");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1, 'ada@example.com', 'A-1');");
            var uniqueIndexOid = await ScalarTextAsync(connectionString, "SELECT 'public.t_email_key'::regclass::oid::text");

            WriteSchema(projectDirectory, """
                CREATE TABLE public.t (id int PRIMARY KEY,
                    -- @renamed-from: email
                    email_address text UNIQUE CHECK (email_address <> ''),
                    -- @renamed-from: code
                    sku text);
                CREATE INDEX ON public.t (sku);

                """);
            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.DoesNotContain("DROP CONSTRAINT", dryRun.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("CREATE INDEX", dryRun.Output, StringComparison.Ordinal);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            Assert.AreEqual(
                "t_email_address_check,t_email_address_key,t_pkey",
                await ScalarTextAsync(connectionString, "SELECT string_agg(conname, ',' ORDER BY conname) FROM pg_constraint WHERE conrelid = 'public.t'::regclass AND contype <> 'n'"));
            Assert.AreEqual(
                "t_email_address_key,t_pkey,t_sku_idx",
                await ScalarTextAsync(connectionString, "SELECT string_agg(c.relname, ',' ORDER BY c.relname) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE i.indrelid = 'public.t'::regclass"));
            Assert.AreEqual(uniqueIndexOid, await ScalarTextAsync(connectionString, "SELECT 'public.t_email_address_key'::regclass::oid::text"), "the unique index was renamed, not rebuilt");
            Assert.AreEqual("ada@example.com|A-1", await ScalarTextAsync(connectionString, "SELECT email_address || '|' || sku FROM public.t"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
