using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-126 against a real server, on every version: a NOT NULL that has a name, or that is written as a table constraint, is only its column's
/// NOT NULL. Before, <c>CONSTRAINT nn NOT NULL</c> on a column was modeled as a constraint with nothing to deploy, so <c>publish --execute</c> refused
/// the whole project (<c>[Constraint] public.t.nn</c>) and <c>diff</c> showed it as added for ever. The table-constraint forms
/// (<c>CONSTRAINT nn NOT NULL b</c> in <c>CREATE TABLE</c>, <c>ALTER TABLE ... ADD CONSTRAINT nn NOT NULL c</c>) parse with PostgreSQL 18's grammar on
/// every server and are deployed as the plain column NOT NULL, which every server accepts.
/// </summary>
[TestClass]
public sealed class NamedNotNullEndToEndTests
{
    private const string Schema = """
        CREATE TABLE public.t (
            a integer CONSTRAINT nn_a NOT NULL,
            b integer,
            c integer,
            d integer,
            CONSTRAINT nn_b NOT NULL b
        );
        ALTER TABLE public.t ADD CONSTRAINT nn_c NOT NULL c;
        """;

    [TestMethod]
    public async Task ANamedNotNullPublishesConvergesAndKeepsThePlainNotNull()
    {
        var databaseName = await CreateDatabaseAsync("g126");
        var projectDirectory = CreateProjectDirectory("g126");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("a:true|b:true|c:true|d:false", await NotNull(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);

            // Dropping a name changes nothing either, and dropping the NOT NULL is a real change.
            WriteSchema(projectDirectory, Schema.Replace("CONSTRAINT nn_a NOT NULL", "NOT NULL", StringComparison.Ordinal));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            WriteSchema(projectDirectory, Schema.Replace("integer CONSTRAINT nn_a NOT NULL", "integer", StringComparison.Ordinal));
            var dropped = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, dropped.ExitCode, dropped.Output);
            Assert.AreEqual("a:false|b:true|c:true|d:false", await NotNull(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> NotNull(string connectionString)
        => ScalarTextAsync(
            connectionString,
            "SELECT string_agg(attname || ':' || attnotnull::text, '|' ORDER BY attnum) FROM pg_attribute WHERE attrelid = 'public.t'::regclass AND attnum > 0 AND NOT attisdropped;");
}
