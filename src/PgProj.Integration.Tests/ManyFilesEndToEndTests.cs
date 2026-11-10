using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-343: a project of more than about a thousand files publishes and compares. Each file was parsed on a new thread, and libpg_query takes a
/// thread-local key on each thread it parses on and never gives it back, so on Linux (1,024 keys) the process had none left for OpenSSL, and the
/// first connection crashed it (a segmentation fault, exit 139) after the files were read. Windows was not affected; the CI runs this suite on Linux.
/// </summary>
[TestClass]
public sealed class ManyFilesEndToEndTests
{
    private const int FileCount = 1_200;

    [TestMethod]
    public async Task AProjectOfMoreThanAThousandFilesPublishesAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g343");
        var projectDirectory = CreateProjectDirectory("g343");
        try
        {
            for (var index = 1; index <= FileCount; index++)
            {
                File.WriteAllText(Path.Combine(projectDirectory, "sql", $"t{index:D4}.sql"), $"CREATE TABLE public.t{index:D4} (id integer PRIMARY KEY, name text);\n");
            }

            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual($"{FileCount}", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_tables WHERE schemaname = 'public'"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
