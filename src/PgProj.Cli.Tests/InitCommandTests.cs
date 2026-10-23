using System.CommandLine;

namespace PgProj.Cli.Tests;

/// <summary>
/// Regression coverage for G-82 (<c>pgproj init</c>). The overwrite-safety guard is tested end to end through the
/// real CLI (it must run before any connection attempt, so an unreachable --source-connection is fine here); the actual
/// scaffold-and-write path needs a real database and is covered by
/// <c>PgProj.Integration.Tests.InitCommandEndToEndTests</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class InitCommandTests
{
    private const string UnreachableConnection = "Host=127.0.0.1;Port=1;Database=doesnotmatter;Username=x;Password=x;Timeout=1";

    [TestMethod]
    public void RedactPasswordReplacesOnlyThePasswordValue()
    {
        var redacted = InitCommand.RedactPassword("Host=localhost; Database=app; Username=web_service; Password=WebServicePassword21; Include Error Detail=true;");

        Assert.AreEqual("Host=localhost; Database=app; Username=web_service; Password=<password>; Include Error Detail=true;", redacted);
    }

    [TestMethod]
    public void RedactPasswordHandlesPwdAliasCaseInsensitively()
    {
        var redacted = InitCommand.RedactPassword("Host=localhost;PWD=secret;Database=app");

        Assert.AreEqual("Host=localhost;PWD=<password>;Database=app", redacted);
    }

    [TestMethod]
    public void RedactPasswordLeavesAConnectionStringWithNoPasswordUnchanged()
    {
        const string connectionString = "Host=localhost;Database=app;Username=web_service";

        Assert.AreEqual(connectionString, InitCommand.RedactPassword(connectionString));
    }

    [TestMethod]
    public async Task InitRefusesAnExistingProjectFileWithoutForce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-init-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectFile = Path.Combine(directory, "pgproj.json");
        await File.WriteAllTextAsync(projectFile, "{}");
        try
        {
            var (exitCode, output) = await RunInitAsync(["--project", projectFile, "--source-connection", UnreachableConnection]);

            Assert.AreEqual(1, exitCode);
            StringAssert.Contains(output, "already exists");
            StringAssert.Contains(output, "--force");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitRefusesANonEmptyOutputDirectoryWithoutForce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-init-test-" + Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(directory, "sql");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "existing.sql"), "-- pre-existing file");
        try
        {
            var (exitCode, output) = await RunInitAsync(["--project", Path.Combine(directory, "pgproj.json"), "--output", outputDirectory, "--source-connection", UnreachableConnection]);

            Assert.AreEqual(1, exitCode);
            StringAssert.Contains(output, "not empty");
            StringAssert.Contains(output, "--force");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ShortDashOAliasResolvesToTheSameOutputOption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-init-test-" + Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(directory, "sql");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "existing.sql"), "-- pre-existing file");
        try
        {
            // -o must resolve to the exact same directory --output would: refused as non-empty, not silently
            // ignored in favor of the ./sql default (which would leave the pre-populated directory unnoticed).
            var (exitCode, output) = await RunInitAsync(["--project", Path.Combine(directory, "pgproj.json"), "-o", outputDirectory, "--source-connection", UnreachableConnection]);

            Assert.AreEqual(1, exitCode);
            StringAssert.Contains(output, "not empty");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// G-108: the help still said row-level security and domain structure are not read, though G-76 and G-77 made both work, which told
    /// users to distrust correct output. G-152 then found the list too short: it named only the foreign-data kinds, and a database's owners,
    /// roles, collations, text search objects, statistics, and tablespaces were left out without a word. The expected kinds are written out
    /// here on purpose: a kind dropped from the notice by mistake fails this test, and modeling one (G-146 to G-151) means editing both.
    /// </summary>
    [TestMethod]
    public async Task HelpNamesEveryObjectKindInitCannotRead()
    {
        var (_, output) = await RunInitAsync(["--help"]);
        var flattened = string.Join(" ", output.Split((char[])['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));

        StringAssert.Contains(flattened, "Not read by pgproj, so absent from the scaffolded project even if the database has them:");
        string[] unreadKinds =
        [
            "roles and their settings (role memberships are scaffolded)",
            "text search parsers and templates (a text search configuration and a dictionary are scaffolded)",
            "tablespaces (a table's TABLESPACE clause is scaffolded)",
            "foreign data wrappers, servers, user mappings, and foreign tables",
            "subscriptions",
            "operator classes and families",
            "conversions",
            "base types (CREATE TYPE with INPUT and OUTPUT functions), with their input and output functions",
            "languages",
            "transforms",
            "access methods",
            "security labels",
            "database-level settings",
            "comments on event triggers, rules, operators, aggregates, casts, publications, and a domain's constraints (the objects themselves are scaffolded)",
            "indexes PostgreSQL marks INVALID, left by a failed CREATE INDEX CONCURRENTLY (each is named)",
        ];
        foreach (var kind in unreadKinds)
        {
            StringAssert.Contains(flattened, kind);
        }

        Assert.IsFalse(flattened.Contains("Row-level security, real domain structure", StringComparison.Ordinal), output);
        StringAssert.Contains(flattened, "row-level security policies, rules, extended statistics, and grants live alongside it");
    }

    private static async Task<(int ExitCode, string Output)> RunInitAsync(string[] args)
    {
        var root = new RootCommand("pgproj") { InitCommand.Create(new CliContext()) };

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = await root.Parse(["init", .. args]).InvokeAsync();
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
