using PgProj.Engine;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-332: <c>pgproj about</c> prints the engine's <see cref="AboutResult"/> for a bug report: the version, the environment, the project, and each database with
/// where its connection came from. It exits 0 whatever it could not read, never prints a password, and says how pgproj was installed.
/// </summary>
[TestClass]
public sealed class AboutCommandTests
{
    [TestMethod]
    public void ItExitsZeroAndReportsWhatItCouldNotRead()
    {
        // Arrange
        var missing = Path.Combine(Path.GetTempPath(), "pgproj-about-" + Guid.NewGuid().ToString("N"), "pgproj.json");

        // Act
        var run = VersionOptionTests.Run("about", "--project", missing, "--target-connection", "Host=localhost;Password=hunter2;Bogus=1");

        // Assert
        Assert.AreEqual(0, run.ExitCode, run.Error);
        StringAssert.StartsWith(run.Out, "pgproj ");
        StringAssert.Contains(run.Out, "  Installed as: ");
        StringAssert.Contains(run.Out, "  Operating system: ");
        StringAssert.Contains(run.Out, "  .NET: .NET ");
        StringAssert.Contains(run.Out, "  PostgreSQL parser: loads");
        StringAssert.Contains(run.Out, $"Project: none found at {Path.GetFullPath(missing)}");
        StringAssert.Contains(run.Out, "Target database: from --target-connection");
        StringAssert.Contains(run.Out, "  Could not connect: The connection string is not valid");
        StringAssert.Contains(run.Out, "No password is shown.");
        Assert.IsFalse(run.Out.Contains("hunter2", StringComparison.Ordinal), run.Out);
        Assert.AreEqual(string.Empty, run.Error);
    }

    [TestMethod]
    public void ItPrintsTheVersionTheVersionOptionPrints()
    {
        // Act
        var version = VersionOptionTests.Run("--version").Out.Trim();
        var about = VersionOptionTests.Run("about", "--project", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "pgproj.json"));

        // Assert
        Assert.AreEqual($"pgproj {version}", about.Out.ReplaceLineEndings("\n").Split('\n')[0]);
    }

    [TestMethod]
    public void AConnectedDatabaseAndAProjectAreLaidOutLineByLine()
    {
        // Arrange
        var result = Result() with
        {
            ProjectStatus = AboutProjectStatus.Found,
            ProjectFilePath = Path.GetFullPath("pgproj.json"),
            Project = new AboutProject
            {
                Name = "shop",
                Version = "2.1",
                SourcePath = Path.GetFullPath("sql"),
                SqlFileCount = 1,
                Schemas = [],
                SearchPath = ["app", "public"],
                PostDeployScript = "scripts/post.sql",
                ExcludeGrants = true,
                ExcludeOwners = true,
                TenantLines = ["Tenants: the database's schemas named t_..."],
            },
            Target = new AboutConnection
            {
                Origin = "the PGPROJ_TARGET_CONNECTION environment variable",
                Status = AboutConnectionStatus.Connected,
                Host = "db.example",
                Port = 5433,
                Database = "shop",
                Username = "app",
                ServerVersion = "17.2",
                IsSuperuser = false,
                CanCreateTemporaryObjects = true,
            },
            Source = new AboutConnection { Origin = "sourceConnectionString in pgproj.json", Status = AboutConnectionStatus.Failed, Host = "prod.example", Port = 5432, Message = "no answer within 5 s" },
        };

        // Act
        var lines = AboutCommand.Lines(result, "0.1.0+abc", "a .NET tool").ToArray();

        // Assert
        CollectionAssert.AreEqual(
            new[]
            {
                "pgproj 0.1.0+abc",
                "  Installed as: a .NET tool",
                "  Operating system: Ubuntu 24.04.1 LTS (X64)",
                "  .NET: .NET 10.0.13 (linux-x64)",
                "  PostgreSQL parser: loads (libpg_query, PostgreSQL 18's grammar)",
                "",
                "Project: pgproj.json",
                "  Name: shop (version 2.1)",
                "  Source: sql (1 SQL file)",
                "  Schemas: not listed (every schema the SQL files use)",
                "  Search path: app, public",
                "  Post-deploy script: scripts/post.sql",
                "  Left out of comparisons: grants, owners",
                "  Tenants: the database's schemas named t_...",
                "",
                "Target database: from the PGPROJ_TARGET_CONNECTION environment variable",
                "  db.example:5433, database shop, user app",
                "  Server: PostgreSQL 17.2",
                "  Superuser: no; can create temporary objects: yes",
                "Source database: from sourceConnectionString in pgproj.json",
                "  prod.example:5432, database (the user's), user (the default)",
                "  Could not connect: no answer within 5 s",
                "",
                "No password is shown. Host names and paths are: remove them before pasting this into an issue if they are private.",
            },
            lines,
            string.Join("\n", lines));
    }

    [TestMethod]
    public void AParserThatDoesNotLoadAndAnotherEngineVersionAreSaidPlainly()
    {
        // Arrange
        var result = Result() with { ParserLoaded = false, ParserMessage = "Unable to load shared library 'libpg_query'", EngineVersion = "0.2.0", ProcessArchitecture = "X64", OperatingSystemArchitecture = "Arm64" };

        // Act
        var lines = AboutCommand.Lines(result, "0.1.0", "an archive (.zip or .tar.gz)").ToArray();

        // Assert
        Assert.AreEqual("pgproj 0.1.0 (engine 0.2.0)", lines[0]);
        CollectionAssert.Contains(lines, "  Operating system: Ubuntu 24.04.1 LTS (Arm64, running as X64)");
        CollectionAssert.Contains(lines, "  PostgreSQL parser: does not load, so no SQL can be read: Unable to load shared library 'libpg_query'");
        CollectionAssert.Contains(lines, "Project: none named");
        CollectionAssert.Contains(lines, "Target database: none configured");
        CollectionAssert.Contains(lines, "Source database: none configured");
    }

    [TestMethod]
    public void TheInstallKindIsReadFromWhereItRuns()
    {
        Assert.AreEqual("a .NET tool", InstallKind.Describe("/home/u/.dotnet/tools/.store/pgproj/0.1.0/pgproj/0.1.0/tools/net10.0/any/", selfContained: false, programFiles: null));
        Assert.AreEqual("a .NET tool", InstallKind.Describe(@"C:\Users\u\.dotnet\tools\.store\pgproj\0.1.0\pgproj\0.1.0\tools\net10.0\any\", selfContained: false, programFiles: @"C:\Program Files"));
        Assert.AreEqual("a .deb or .rpm package", InstallKind.Describe("/usr/lib/pgproj/", selfContained: true, programFiles: null));
        Assert.AreEqual("the Windows installer (.msi)", InstallKind.Describe(@"C:\Program Files\pgproj\", selfContained: true, programFiles: @"C:\Program Files"));
        Assert.AreEqual("an archive (.zip or .tar.gz)", InstallKind.Describe("/opt/pgproj-0.1.0-linux-x64/", selfContained: true, programFiles: null));
        Assert.AreEqual("a build that runs on the installed .NET (from source)", InstallKind.Describe(@"D:\src\pgproj\src\PgProj.Cli\bin\Debug\net10.0\", selfContained: false, programFiles: @"C:\Program Files"));
    }

    [TestMethod]
    public void TheHelpSaysWhatItPrintsAndThatItChangesNothing()
    {
        // Act
        var run = VersionOptionTests.Run("about", "--help");

        // Assert
        Assert.AreEqual(0, run.ExitCode, run.Error);
        foreach (var expected in new[] { "--project", "--target-connection", "--source-connection", "PGPROJ_TARGET_CONNECTION", "PGPROJ_SOURCE_CONNECTION", "superuser", "changes nothing", "No password is ever printed", "pgproj about" })
        {
            StringAssert.Contains(run.Out, expected);
        }
    }

    private static AboutResult Result() => new()
    {
        EngineVersion = "0.1.0+abc",
        OperatingSystem = "Ubuntu 24.04.1 LTS",
        OperatingSystemArchitecture = "X64",
        ProcessArchitecture = "X64",
        Runtime = ".NET 10.0.13",
        RuntimeIdentifier = "linux-x64",
        ParserLoaded = true,
        ProjectStatus = AboutProjectStatus.NotRequested,
    };
}
