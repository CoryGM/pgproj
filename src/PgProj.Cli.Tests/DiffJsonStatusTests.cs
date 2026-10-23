using System.CommandLine;
using System.Text.Json;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-316: <c>diff --format json</c> writes the result's envelope whatever its status, so a tool always gets a document: a project with errors is <c>failed</c> with its
/// diagnostics, a missing project <c>projectNotFound</c>, and neither has the comparison's part. The exit code stays 1.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DiffJsonStatusTests
{
    [TestMethod]
    public async Task AProjectWithErrorsIsAFailedDocumentWithItsDiagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g316-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "sql"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "pgproj.json"), """{ "name": "broken", "version": "1", "source": "./sql" }""");
            File.WriteAllText(Path.Combine(directory, "sql", "broken.sql"), "CREATE TABLE b (a int, ;\n");

            var (exitCode, output) = await DiffJsonAsync(Path.Combine(directory, "pgproj.json"));

            Assert.AreEqual(1, exitCode);
            using var json = JsonDocument.Parse(output);
            Assert.AreEqual("failed", json.RootElement.GetProperty("status").GetString());
            Assert.AreEqual("broken", json.RootElement.GetProperty("project").GetProperty("name").GetString());
            Assert.AreEqual(1, json.RootElement.GetProperty("counts").GetProperty("errors").GetInt32());
            Assert.AreEqual("Error", json.RootElement.GetProperty("diagnostics")[0].GetProperty("severity").GetString());
            Assert.IsFalse(json.RootElement.TryGetProperty("added", out _), "nothing was compared");
            Assert.IsFalse(json.RootElement.TryGetProperty("summary", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task AMissingProjectIsADocumentThatSaysSo()
    {
        var (exitCode, output) = await DiffJsonAsync(Path.Combine(Path.GetTempPath(), "pgproj-g316-none", "pgproj.json"));

        Assert.AreEqual(1, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.AreEqual("projectNotFound", json.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(0, json.RootElement.GetProperty("documents").GetArrayLength());
    }

    /// <summary>Runs <c>diff --format json</c> in process with an unreachable target, and returns what it wrote to standard output.</summary>
    private static async Task<(int ExitCode, string Output)> DiffJsonAsync(string projectFile)
    {
        var root = new RootCommand("pgproj") { DiffCommand.Create(new CliContext()) };
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(TextWriter.Null);
        try
        {
            var exitCode = await root.Parse(["diff", "--project", projectFile, "--target-connection", "Host=127.0.0.1;Port=1;Database=x;Timeout=2", "--format", "json"]).InvokeAsync();
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
