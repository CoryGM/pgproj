using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-344 against a real server: an index whose expression compares an enum column with an untyped literal converges, since PostgreSQL renders both sides'
/// keys (it stores the literal with its cast); a changed expression is still found and deployed; and, with G-345, such an index survives the enum's
/// rebuild under its column.
/// </summary>
[TestClass]
public sealed class IndexKeyRenderingEndToEndTests
{
    private const string Sql = """
        CREATE TYPE public.color AS ENUM ('r', 'g');
        CREATE TABLE public.t (id int, c public.color, name text);
        CREATE INDEX t_c_r ON public.t ((c = 'r'));
        CREATE INDEX t_lower ON public.t (lower(name), id);

        """;

    [TestMethod]
    public async Task AnUntypedLiteralInAnExpressionKeyConvergesAndAChangeIsStillFound()
    {
        var databaseName = await CreateDatabaseAsync("g344");
        var projectDirectory = CreateProjectDirectory("g344");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            var plan = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.IsFalse(plan.Output.Contains("CREATE INDEX", StringComparison.Ordinal), "A second publish proposes nothing.\n" + plan.Output);

            // Another literal is another key: found, and deployed.
            WriteSchema(projectDirectory, Edit(Sql, "((c = 'r'))", "((c = 'g'))"));
            var changed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--exit-code");
            Assert.AreEqual(2, changed.ExitCode, changed.Output);
            var applied = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, applied.ExitCode, applied.Output);
            Assert.AreEqual("(c = 'g'::color)", await ScalarTextAsync(connectionString, "SELECT pg_get_indexdef('public.t_c_r'::regclass, 1, true)"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // G-345: the enum is rebuilt under its column with the index on it, which comes back.
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1, 'g', 'a'), (2, 'r', 'b');");
            WriteSchema(projectDirectory, Edit(Edit(Sql, "((c = 'r'))", "((c = 'g'))"), "ENUM ('r', 'g')", "ENUM ('g', 'r')"));
            var rebuilt = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, rebuilt.ExitCode, rebuilt.Output);
            Assert.AreEqual("(c = 'g'::color)", await ScalarTextAsync(connectionString, "SELECT pg_get_indexdef('public.t_c_r'::regclass, 1, true)"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
