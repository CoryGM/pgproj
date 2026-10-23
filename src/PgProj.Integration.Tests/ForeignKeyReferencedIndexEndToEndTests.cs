using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-273 against a real server. A unique index that is not a constraint, and that a foreign key references, was not read from the
/// database: PostgreSQL records the referenced index in the foreign key's <c>conindid</c>, and the live read took every index named there
/// for a constraint's own. `diff` then always showed the index as added, a second `publish` failed with <c>42P07</c>, and `init` left the
/// index out, so publishing the scaffold failed with <c>42830</c>.
/// </summary>
[TestClass]
public sealed class ForeignKeyReferencedIndexEndToEndTests
{
    [TestMethod]
    public async Task AUniqueIndexAForeignKeyReferencesIsReadConvergesAndIsScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g273");
        var projectDirectory = CreateProjectDirectory("g273");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g273-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g273b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE TABLE public.p (id integer NOT NULL, code text NOT NULL);
                CREATE UNIQUE INDEX p_id ON public.p (id);
                CREATE UNIQUE INDEX p_code ON public.p (code);
                COMMENT ON INDEX public.p_code IS 'referenced by two foreign keys';
                CREATE TABLE public.c (id integer PRIMARY KEY, p_id integer REFERENCES public.p (id), p_code text REFERENCES public.p (code));
                CREATE TABLE public.d (id integer PRIMARY KEY, p_code text REFERENCES public.p (code));
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("p_code,p_id", await ScalarTextAsync(connectionString, "SELECT string_agg(indexrelid::regclass::text, ',' ORDER BY indexrelid::regclass::text) FROM pg_index WHERE indrelid = 'public.p'::regclass"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var second = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, second.ExitCode, "the second publish failed.\n" + second.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var tableFile = File.ReadAllText(Path.Combine(scaffoldDirectory, "sql", "Schema", "public", "Table", "p.sql"));
            StringAssert.Contains(tableFile, "CREATE UNIQUE INDEX p_id ON p USING btree (id);");
            StringAssert.Contains(tableFile, "CREATE UNIQUE INDEX p_code ON p USING btree (code);");
            StringAssert.Contains(tableFile, "referenced by two foreign keys");

            var secondConnection = ConnectionStringFor(secondDatabase);
            var scaffoldPublished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, scaffoldPublished.ExitCode, "publishing the scaffold failed.\n" + scaffoldPublished.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
            await AssertDiffIsEmptyAsync(projectFile, secondConnection);
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
