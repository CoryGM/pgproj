using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-275 against a real server. A type whose quoted name differs from a built-in only by case (<c>"Name"</c>, <c>"Date"</c>, as the
/// AdventureWorks port declares) was taken for the built-in, so a table using it got no dependency on it and, declared in an earlier file,
/// was created first: <c>42704: type "Name" does not exist</c>. Such a type is now the project's, and what uses it is created after it.
/// </summary>
[TestClass]
public sealed class QuotedBuiltInTypeNameEndToEndTests
{
    [TestMethod]
    public async Task ATypeNamedLikeABuiltInIsCreatedBeforeWhatUsesItConvergesAndIsScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g275");
        var projectDirectory = CreateProjectDirectory("g275");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g275-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g275b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            var sqlDirectory = Path.Combine(projectDirectory, "sql");
            // a.sql is read before b.sql, so only a dependency on the types puts them first.
            File.WriteAllText(Path.Combine(sqlDirectory, "a.sql"), """
                CREATE SCHEMA hr;
                CREATE TABLE hr.t (id int PRIMARY KEY, n "Name" NOT NULL, d "Date"[], plain name);
                CREATE FUNCTION hr.label(p "Name") RETURNS "Date" LANGUAGE sql AS $$ SELECT current_date $$;
                CREATE FUNCTION hr.name_length("Name") RETURNS int LANGUAGE sql AS $$ SELECT length($1) $$;
                CREATE CAST ("Name" AS int) WITH FUNCTION hr.name_length("Name");
                """);
            File.WriteAllText(Path.Combine(sqlDirectory, "b.sql"), """
                CREATE DOMAIN public."Name" AS character varying(50);
                CREATE DOMAIN public."Date" AS date;
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(
                "\"Name\",\"Date\"[],name",
                await ScalarTextAsync(connectionString, """
                    SELECT string_agg(format_type(atttypid, atttypmod), ',' ORDER BY attnum)
                    FROM pg_attribute WHERE attrelid = 'hr.t'::regclass AND attname IN ('n', 'd', 'plain')
                    """));
            Assert.AreEqual("\"Date\"", await ScalarTextAsync(connectionString, "SELECT format_type(prorettype, NULL) FROM pg_proc WHERE proname = 'label'"));
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_cast WHERE castsource = '\"Name\"'::regtype"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
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
