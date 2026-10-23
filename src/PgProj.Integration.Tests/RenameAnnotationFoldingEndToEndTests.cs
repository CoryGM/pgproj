using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-287 against a real server (approved by the user 2026-10-14). A rename annotation written with a capital (<c>-- @renamed-from: Email</c>) or quoted
/// (<c>-- @renamed-from: "Email"</c>) was shown by <c>diff</c> as a rename and published as a dropped and an added column, losing the column's data once
/// <c>--allow-destructive</c> was given. Each is now one <c>RENAME COLUMN</c>, runs without the option, keeps the data, and converges.
/// </summary>
[TestClass]
public sealed class RenameAnnotationFoldingEndToEndTests
{
    [TestMethod]
    public async Task ACapitalizedAndAQuotedAnnotationRenameAndKeepTheData()
    {
        var databaseName = await CreateDatabaseAsync("g287");
        var projectDirectory = CreateProjectDirectory("g287");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int PRIMARY KEY, email text, \"Phone\" text);\n");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1, 'ada@example.com', '555-0100');");

            WriteSchema(projectDirectory, """
                CREATE TABLE public.t (id int PRIMARY KEY,
                    -- @renamed-from: Email
                    email_address text,
                    -- @renamed-from: "Phone"
                    "PhoneNumber" text);

                """);
            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "the renames needed --allow-destructive.\n" + published.Output);
            Assert.AreEqual("ada@example.com|555-0100", await ScalarTextAsync(connectionString, "SELECT email_address || '|' || \"PhoneNumber\" FROM public.t"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
