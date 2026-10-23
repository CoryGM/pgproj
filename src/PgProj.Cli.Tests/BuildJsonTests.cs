using System.CommandLine;
using System.Text.Json;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-309f: <c>build --format json</c> writes the engine's document, the envelope <c>diff --format json</c> starts with, on standard output and nothing else there; the
/// header and the diagnostics' text go to standard error, and the exit code is unchanged.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BuildJsonTests
{
    [TestMethod]
    public async Task AProjectWithErrorsIsAFailedDocumentWithItsDiagnosticsAndTheirRanges()
    {
        var directory = CreateProject("CREATE TABLE b (a int, ;\n");
        try
        {
            var (exitCode, output, error) = await BuildJsonAsync(Path.Combine(directory, "pgproj.json"));

            Assert.AreEqual(1, exitCode);
            using var json = JsonDocument.Parse(output); // standard output holds the JSON document and nothing else
            var root = json.RootElement;
            Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.AreEqual("failed", root.GetProperty("status").GetString());
            Assert.AreEqual("broken", root.GetProperty("project").GetProperty("name").GetString());
            Assert.AreEqual(1, root.GetProperty("documents").GetArrayLength());
            Assert.AreEqual(1, root.GetProperty("counts").GetProperty("errors").GetInt32());

            var diagnostic = root.GetProperty("diagnostics")[0];
            Assert.AreEqual("Error", diagnostic.GetProperty("severity").GetString());
            StringAssert.EndsWith(diagnostic.GetProperty("file").GetString(), "broken.sql");
            Assert.AreEqual(1, diagnostic.GetProperty("line").GetInt32());
            Assert.AreEqual(24, diagnostic.GetProperty("column").GetInt32());
            Assert.AreEqual(1, diagnostic.GetProperty("endLine").GetInt32());
            Assert.AreEqual(25, diagnostic.GetProperty("endColumn").GetInt32());
            Assert.IsFalse(root.TryGetProperty("added", out _), "a build has no comparison part");

            StringAssert.Contains(error, "Loaded project: broken", "the header goes to standard error");
            StringAssert.Contains(error, "broken.sql:1:24-1:25: [Error]", "so does the diagnostics' text");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task AProjectWithoutErrorsSucceedsAndExitsZero()
    {
        var directory = CreateProject("CREATE TABLE public.t (id int PRIMARY KEY);\n");
        try
        {
            var (exitCode, output, _) = await BuildJsonAsync(Path.Combine(directory, "pgproj.json"));

            Assert.AreEqual(0, exitCode);
            using var json = JsonDocument.Parse(output);
            Assert.AreEqual("succeeded", json.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(0, json.RootElement.GetProperty("counts").GetProperty("errors").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task AMissingProjectIsADocumentThatSaysSo()
    {
        var (exitCode, output, error) = await BuildJsonAsync(Path.Combine(Path.GetTempPath(), "pgproj-g309f-none", "pgproj.json"));

        Assert.AreEqual(1, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.AreEqual("projectNotFound", json.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(0, json.RootElement.GetProperty("documents").GetArrayLength());
        StringAssert.Contains(error, "Build failed: project file was not found.");
    }

    private static string CreateProject(string sql)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g309f-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "sql"));
        File.WriteAllText(Path.Combine(directory, "pgproj.json"), """{ "name": "broken", "version": "1", "source": "./sql" }""");
        File.WriteAllText(Path.Combine(directory, "sql", "broken.sql"), sql);
        return directory;
    }

    /// <summary>Runs <c>build --format json</c> in process and returns what it wrote to standard output and to standard error.</summary>
    private static async Task<(int ExitCode, string Output, string Error)> BuildJsonAsync(string projectFile)
    {
        var root = new RootCommand("pgproj") { BuildCommand.Create(new CliContext()) };
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exitCode = await root.Parse(["build", "--project", projectFile, "--format", "json"]).InvokeAsync();
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
