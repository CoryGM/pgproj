using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-278 against a real server. An index whose operator class is written with its schema (<c>embedding public.vector_cosine_ops</c>, how <c>pg_dump</c>
/// writes pgvector's) never matched the database, which prints a class in <c>public</c> or <c>pg_catalog</c> without it, so every publish dropped and
/// rebuilt the index. The test image has no pgvector, so a class is made in <c>public</c> the way an extension puts one there.
/// </summary>
[TestClass]
public sealed class OperatorClassSchemaEndToEndTests
{
    [TestMethod]
    public async Task ASchemaQualifiedOperatorClassConverges()
    {
        var databaseName = await CreateDatabaseAsync("g278");
        var projectDirectory = CreateProjectDirectory("g278");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, """
                CREATE OPERATOR CLASS public.reverse_int_ops FOR TYPE int USING btree AS
                    OPERATOR 1 >, OPERATOR 2 >=, OPERATOR 3 =, OPERATOR 4 <=, OPERATOR 5 <, FUNCTION 1 btint4cmp(int, int);
                """);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE TABLE public.t (id int PRIMARY KEY, n int, s text);
                CREATE INDEX t_n_reverse ON public.t (n public.reverse_int_ops);
                CREATE INDEX t_s_pattern ON public.t (s pg_catalog.text_pattern_ops);
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            var indexes = "SELECT string_agg(relname || '=' || oid, ',' ORDER BY relname) FROM pg_class WHERE relname IN ('t_n_reverse', 't_s_pattern')";
            var before = await ScalarTextAsync(connectionString, indexes);
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT pg_get_indexdef('public.t_n_reverse'::regclass)"), "reverse_int_ops");

            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            Assert.AreEqual(before, await ScalarTextAsync(connectionString, indexes), "a second publish rebuilt nothing");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
