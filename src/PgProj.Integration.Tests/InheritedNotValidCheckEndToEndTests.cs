using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-234 follow-up, found by the differential QA run (<c>inherit</c>, <c>create_table_like</c>): a parent's <c>NOT VALID</c> check is inherited by its children as
/// <c>NOT VALID</c>, and PostgreSQL refuses a child's own valid check of the same name after that (<c>42P17</c>). The parent's check is created after the child's, so
/// the two merge, and the project (and the one <c>init</c> writes from it) publishes.
/// </summary>
[TestClass]
public sealed class InheritedNotValidCheckEndToEndTests
{
    [TestMethod]
    public async Task AParentsNotValidCheckAndAChildsValidCheckOfTheSameNameArePublishedInThatOrder()
    {
        var databaseName = await CreateDatabaseAsync("g234i");
        var projectDirectory = CreateProjectDirectory("g234i");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g234i-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE TABLE public.a_parent (f1 int);
                CREATE TABLE public.b_child () INHERITS (public.a_parent);
                ALTER TABLE public.a_parent ADD CONSTRAINT chk CHECK (f1 > 0) NOT VALID;
                ALTER TABLE public.b_child ADD CONSTRAINT chk CHECK (f1 > 0);
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("a_parent:false,b_child:true", await ScalarTextAsync(connectionString, "SELECT string_agg(conrelid::regclass::text || ':' || convalidated::text, ',' ORDER BY conrelid::regclass::text) FROM pg_constraint WHERE conname = 'chk'"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondDatabase = await CreateDatabaseAsync("g234ib");
            try
            {
                var secondConnection = ConnectionStringFor(secondDatabase);
                var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
                Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
                await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
            }
            finally
            {
                await DropDatabaseAsync(secondDatabase);
            }
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
