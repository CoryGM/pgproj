using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-182 against a real server. A foreign key with <c>MATCH FULL</c> was scripted with the clause after the actions, which PostgreSQL rejects
/// (<c>42601</c>), and <c>ON DELETE SET NULL (b)</c> lost its column list, which would null every referencing column.
/// </summary>
[TestClass]
public sealed class ForeignKeyMatchAndSetColumnsEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.par (a int, b int, PRIMARY KEY (a, b));
        CREATE TABLE public.full_only (id int PRIMARY KEY, a int, b int, CONSTRAINT fk_full FOREIGN KEY (a, b) REFERENCES public.par (a, b) MATCH FULL);
        CREATE TABLE public.cols_only (id int PRIMARY KEY, a int NOT NULL, b int, CONSTRAINT fk_cols FOREIGN KEY (a, b) REFERENCES public.par (a, b) ON DELETE SET NULL (b));
        CREATE TABLE public.both (id int PRIMARY KEY, a int NOT NULL, b int, CONSTRAINT fk_both FOREIGN KEY (a, b) REFERENCES public.par MATCH FULL ON DELETE SET NULL (b) ON UPDATE CASCADE DEFERRABLE);
        """;

    [TestMethod]
    [PostgreSqlVersion(Minimum = 15)]
    public async Task MatchAndSetColumnsAreDeployed_ConvergeAreChangedAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g182");
        var projectDirectory = CreateProjectDirectory("g182");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g182-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            StringAssert.Contains(await DefinitionAsync(connectionString, "fk_full"), "MATCH FULL");
            StringAssert.Contains(await DefinitionAsync(connectionString, "fk_cols"), "ON DELETE SET NULL (b)");
            var both = await DefinitionAsync(connectionString, "fk_both");
            StringAssert.Contains(both, "MATCH FULL");
            StringAssert.Contains(both, "ON DELETE SET NULL (b)");
            StringAssert.Contains(both, "ON UPDATE CASCADE");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // init reads them back and its project converges.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // A different column list is seen and deployed; so is dropping it.
            WriteSchema(projectDirectory, Edit(Sql, "ON DELETE SET NULL (b)", "ON DELETE SET NULL"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.DoesNotContain("SET NULL (b)", await DefinitionAsync(connectionString, "fk_cols"), StringComparison.Ordinal);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    private static Task<string> DefinitionAsync(string connectionString, string constraintName)
        => ScalarTextAsync(connectionString, $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = '{constraintName}';");
}
