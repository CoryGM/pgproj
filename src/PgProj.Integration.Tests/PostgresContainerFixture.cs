using PgProj.Cli;
using Testcontainers.PostgreSql;

namespace PgProj.Integration.Tests;

/// <summary>
/// Shared disposable PostgreSQL container for the G-30 live integration suite. A single
/// container instance is started for the whole test assembly (expensive to start per-test)
/// and each test isolates its own state inside a uniquely-named schema that it creates and
/// drops itself, so tests can run against the same server without interfering with each other.
/// </summary>
[TestClass]
public static class PostgresContainerFixture
{
    /// <summary>The environment variable that picks the server image, so the suite can be run against each supported PostgreSQL version.</summary>
    public const string ImageVariable = "PGPROJ_TEST_POSTGRES_IMAGE";

    /// <summary>The newest supported release (the user's policy of 2026-10-13): 16 and 17 are run with <see cref="ImageVariable"/> for what differs on them.</summary>
    private const string DefaultImage = "postgres:18-alpine";

    private static PostgreSqlContainer? s_container;

    public static string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The image the suite runs against: <see cref="ImageVariable"/> when set, otherwise <see cref="DefaultImage"/>.</summary>
    public static string Image { get; } = Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } image ? image : DefaultImage;

    [AssemblyInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        context.WriteLine($"PostgreSQL image: {Image}");
        s_container = new PostgreSqlBuilder()
            .WithImage(Image)
            .WithDatabase("pgproj_g30")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await s_container.StartAsync();
        ConnectionString = s_container.GetConnectionString();
    }

    [AssemblyCleanup]
    public static async Task CleanupAsync()
    {
        if (s_container is not null)
        {
            await s_container.DisposeAsync();
        }
    }

    public static CliContext CreateCliContext() => new();

    /// <summary>
    /// Makes a directory inside the container that the server may use as a tablespace's location (G-151): <c>CREATE TABLESPACE</c> needs a
    /// directory that exists, is empty, and belongs to the server's operating-system user.
    /// </summary>
    public static async Task CreateTablespaceDirectoryAsync(string path)
    {
        var result = await s_container!.ExecAsync(["sh", "-c", $"mkdir -p {path} && chown postgres:postgres {path} && chmod 700 {path}"]);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
    }
}
