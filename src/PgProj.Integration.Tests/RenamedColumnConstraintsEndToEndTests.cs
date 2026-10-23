using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-277 against a real server. A column renamed with <c>@renamed-from</c> had its unique constraint, <c>CHECK</c>, and indexes dropped and added again,
/// and the foreign keys that reference it too, though <c>RENAME COLUMN</c> carries them all. Now the rename is the only statement, and every one of
/// them is the same object afterwards.
/// </summary>
[TestClass]
public sealed class RenamedColumnConstraintsEndToEndTests
{
    private const string Before = """
        CREATE TABLE public.a (id int PRIMARY KEY, code text NOT NULL, CONSTRAINT a_code_key UNIQUE (code), CONSTRAINT a_code_check CHECK (code <> '' AND code <> 'code'));
        CREATE UNIQUE INDEX a_code_lower ON public.a (lower(code));
        CREATE INDEX a_code_idx ON public.a (code) INCLUDE (id) WHERE code IS NOT NULL;
        CREATE TABLE public.b (id int PRIMARY KEY, a_code text REFERENCES public.a (code));
        """;

    private const string After = """
        CREATE TABLE public.a (
            id int PRIMARY KEY,
            -- @renamed-from: code
            sku text NOT NULL,
            CONSTRAINT a_code_key UNIQUE (sku),
            CONSTRAINT a_code_check CHECK (sku <> '' AND sku <> 'code'));
        CREATE UNIQUE INDEX a_code_lower ON public.a (lower(sku));
        CREATE INDEX a_code_idx ON public.a (sku) INCLUDE (id) WHERE sku IS NOT NULL;
        CREATE TABLE public.b (id int PRIMARY KEY, a_code text REFERENCES public.a (sku));
        """;

    private const string Identities = """
        SELECT string_agg(name || '=' || oid, ',' ORDER BY name) FROM (
            SELECT conname::text AS name, oid::text FROM pg_constraint WHERE conrelid IN ('public.a'::regclass, 'public.b'::regclass)
            UNION ALL SELECT relname::text, oid::text FROM pg_class WHERE relname IN ('a_code_lower', 'a_code_idx', 'a_code_key')) objects
        """;

    [TestMethod]
    public async Task ARenameKeepsTheColumnsConstraintsIndexesAndReferencingForeignKeys()
    {
        var databaseName = await CreateDatabaseAsync("g277");
        var projectDirectory = CreateProjectDirectory("g277");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Before);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "INSERT INTO public.a VALUES (1, 'x'); INSERT INTO public.b VALUES (1, 'x');");
            var before = await ScalarTextAsync(connectionString, Identities);

            WriteSchema(projectDirectory, After);
            var script = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, script.ExitCode, script.Output);
            StringAssert.Contains(script.Output, "RENAME COLUMN \"code\" TO \"sku\"");
            Assert.DoesNotContain("DROP CONSTRAINT", script.Output);
            Assert.DoesNotContain("DROP INDEX", script.Output);
            Assert.DoesNotContain("blocks writes", script.Output, "no index is built, so no lock warning");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual(before, await ScalarTextAsync(connectionString, Identities), "every constraint and index is the same object");
            Assert.AreEqual("x", await ScalarTextAsync(connectionString, "SELECT sku FROM public.a"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
