using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-180 against a real server. A view's <c>WITH (security_barrier, security_invoker)</c> and <c>WITH CHECK OPTION</c> were dropped from the
/// deployed view and never compared, so a view meant to protect rows was created without that protection and <c>publish</c> reported nothing to do.
/// </summary>
[TestClass]
public sealed class ViewOptionsEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.e (id int PRIMARY KEY, name text);
        CREATE VIEW public.ev WITH (security_barrier = true) AS SELECT id, name FROM public.e WHERE id > 0;
        CREATE VIEW public.ev2 WITH (security_invoker = on) AS SELECT id, name FROM public.ev;
        CREATE VIEW public.ev3 WITH (security_barrier, security_invoker = true) AS SELECT id FROM public.e WHERE id > 0 WITH LOCAL CHECK OPTION;
        CREATE VIEW public.ev4 WITH (security_barrier = false) AS SELECT id FROM public.e;
        """;

    [TestMethod]
    [PostgreSqlVersion(Minimum = 15)]
    public async Task ViewOptionsAreDeployed_ConvergeAreChangedAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g180");
        var projectDirectory = CreateProjectDirectory("g180");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g180-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("security_barrier=true", await OptionsOfAsync(connectionString, "ev"));
            // Deployed as written (G-217): PostgreSQL keeps the value it was given.
            Assert.AreEqual("security_invoker=on", await OptionsOfAsync(connectionString, "ev2"));
            StringAssert.Contains(await OptionsOfAsync(connectionString, "ev3"), "check_option=local");
            StringAssert.Contains(await OptionsOfAsync(connectionString, "ev3"), "security_barrier");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A second publish proposes nothing.
            var scriptPath = Path.Combine(projectDirectory, "second.sql");
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, republished.ExitCode, republished.Output);
            var secondScript = File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath) : string.Empty;
            Assert.DoesNotContain("CREATE OR REPLACE VIEW", secondScript, StringComparison.Ordinal);

            // init writes the options back into each view's file, and that project converges.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var viewFile = Directory.EnumerateFiles(scaffoldDirectory, "ev3.sql", SearchOption.AllDirectories).Single();
            var scaffoldedView = await File.ReadAllTextAsync(viewFile);
            StringAssert.Contains(scaffoldedView, "WITH (security_barrier=true, security_invoker=true)");
            StringAssert.Contains(scaffoldedView, "WITH LOCAL CHECK OPTION");
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // An option turned off, one turned on, and a check option changed are each seen and deployed, with no drop.
            var changedSql = Edit(Edit(Edit(Sql, "WITH (security_barrier = true) AS", "WITH (security_barrier = false) AS"),
                "WITH (security_invoker = on) AS", "WITH (security_barrier = on) AS"),
                "WITH LOCAL CHECK OPTION", "WITH CASCADED CHECK OPTION");
            WriteSchema(projectDirectory, changedSql);
            var changedScript = Path.Combine(projectDirectory, "changed.sql");
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", changedScript);
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.DoesNotContain("DROP VIEW", await File.ReadAllTextAsync(changedScript), StringComparison.Ordinal);
            Assert.IsTrue(string.IsNullOrEmpty(await OptionsOfAsync(connectionString, "ev")) || !(await OptionsOfAsync(connectionString, "ev"))!.Contains("true", StringComparison.Ordinal));
            Assert.AreEqual("security_barrier=on", await OptionsOfAsync(connectionString, "ev2"));
            StringAssert.Contains(await OptionsOfAsync(connectionString, "ev3"), "check_option=cascaded");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Removing every option clears them.
            WriteSchema(projectDirectory, Sql.Replace("WITH (security_barrier = true) ", string.Empty, StringComparison.Ordinal)
                .Replace("WITH (security_invoker = on) ", string.Empty, StringComparison.Ordinal)
                .Replace("WITH (security_barrier, security_invoker = true) ", string.Empty, StringComparison.Ordinal)
                .Replace(" WITH LOCAL CHECK OPTION", string.Empty, StringComparison.Ordinal));
            var cleared = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, cleared.ExitCode, cleared.Output);
            Assert.IsTrue(string.IsNullOrEmpty(await OptionsOfAsync(connectionString, "ev3")));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    private static async Task<string?> OptionsOfAsync(string connectionString, string viewName)
        => await ScalarTextAsync(connectionString, $"SELECT coalesce(array_to_string(reloptions, ','), '') FROM pg_class WHERE oid = 'public.{viewName}'::regclass;");
}
