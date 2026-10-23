using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-282 against a real server (option (a), the user's decision of 2026-10-14). <c>diff --exit-code</c> exits 2 when the database differs from the project and 0
/// when it does not, in text and JSON; an error is 1 even with the option; without it, <c>diff</c> exits 0 either way, as before.
/// </summary>
[TestClass]
public sealed class DiffExitCodeEndToEndTests
{
    [TestMethod]
    public async Task ExitCodeTellsDriftFromNoDriftFromAnError()
    {
        var databaseName = await CreateDatabaseAsync("g282");
        var projectDirectory = CreateProjectDirectory("g282");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int PRIMARY KEY);\n");

            Assert.AreEqual(2, (await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--exit-code")).ExitCode, "the table is to be added");
            Assert.AreEqual(0, (await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString)).ExitCode, "without --exit-code, as before");
            var json = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--exit-code", "--format", "json");
            Assert.AreEqual(2, json.ExitCode, json.Output);
            StringAssert.Contains(json.Output, "\"added\"");

            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            var clean = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--exit-code");
            Assert.AreEqual(0, clean.ExitCode, clean.Output);
            Assert.AreEqual(0, (await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--exit-code", "--format", "json")).ExitCode);

            Assert.AreEqual(1, (await RunAsync("diff", "--project", projectFile, "--target-connection", "Host=localhost;Port=notaport", "--exit-code")).ExitCode, "an error is 1");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
