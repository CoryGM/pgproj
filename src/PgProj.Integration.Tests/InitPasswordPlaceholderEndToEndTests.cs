using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-188 against a real server. <c>init</c> wrote the password placeholder into <c>pgproj.json</c> with JSON's Unicode escapes for the angle brackets, so a
/// person opening the file did not see the <c>&lt;password&gt;</c> the README and the help tell them to fill in.
/// </summary>
[TestClass]
public sealed class InitPasswordPlaceholderEndToEndTests
{
    [TestMethod]
    public async Task TheGeneratedProjectFileShowsThePlaceholderAsText()
    {
        var databaseName = await CreateDatabaseAsync("g188");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g188-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);

            var json = await File.ReadAllTextAsync(scaffoldProject);
            StringAssert.Contains(json, "Password=<password>");
            Assert.DoesNotContain("\\" + "u003C", json, StringComparison.OrdinalIgnoreCase);

            // The file is still valid JSON and loads back the same text.
            using var document = System.Text.Json.JsonDocument.Parse(json);
            StringAssert.Contains(document.RootElement.GetProperty("sourceConnectionString").GetString(), "Password=<password>");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
