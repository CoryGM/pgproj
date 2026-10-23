using System.Text.Json;
using System.Text.RegularExpressions;
using PgProj.Core;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-309f: every C# example of <c>docs/library.md</c> is copied here, between the setup that gives it its variables (<c>projectFile</c>,
/// <c>connectionString</c>, <c>cancellationToken</c>, ...) and the assertions, so it compiles and runs; and the page's examples must all be found
/// in this file, so the page cannot go stale without a test failing. A database is not available to these tests, so the examples that need one
/// run with a malformed connection string and end refused, which still runs their code.
/// </summary>
[TestClass]
public sealed partial class LibraryExamplesTests
{
    private const string MalformedConnectionString = "Host=localhost;Port=notaport;Database=app";

    [TestMethod]
    public async Task BuildingAProjectFromDiskAndItsJson()
    {
        var fixture = new ProjectFixture();
        fixture.WriteToDisk();
        try
        {
            var projectFile = fixture.ProjectFile;
            var cancellationToken = TestContext.CancellationToken;

            var engine = new PgProjEngine();
            var result = await engine.BuildAsync(new BuildRequest { ProjectFilePath = projectFile }, cancellationToken);

            if (result.Status is BuildStatus.ProjectNotFound or BuildStatus.ProjectInvalid)
            {
                Console.WriteLine(result.Message);
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                Console.WriteLine($"{diagnostic.File}:{diagnostic.Line}:{diagnostic.Column}-{diagnostic.EndLine}:{diagnostic.EndColumn}: [{diagnostic.Severity}] {diagnostic.Message}");
            }

            var json = result.ToJson();
            var forDisplay = result.ToJson(path => Path.GetRelativePath(Environment.CurrentDirectory, path));

            Assert.AreEqual(BuildStatus.Succeeded, result.Status);
            Assert.IsNotEmpty(result.Diagnostics, "the fixture's INSERT is a warning");
            using var document = JsonDocument.Parse(json);
            Assert.AreEqual("succeeded", document.RootElement.GetProperty("status").GetString());
            using var displayed = JsonDocument.Parse(forDisplay);
            Assert.AreEqual(Path.GetRelativePath(Environment.CurrentDirectory, projectFile), displayed.RootElement.GetProperty("project").GetProperty("file").GetString());
        }
        finally
        {
            fixture.Delete();
        }
    }

    [TestMethod]
    public async Task AProjectFromMemoryAndFromSettings()
    {
        var cancellationToken = TestContext.CancellationToken;

        var files = new InMemoryFileSource()
            .Set("/project/pgproj.json", """{ "name": "shop", "version": "1.0", "source": "./sql" }""")
            .Set("/project/sql/customer.sql", "CREATE TABLE customer (id bigint PRIMARY KEY, email text NOT NULL);");

        var fromMemory = await new PgProjEngine().BuildAsync(new BuildRequest { Files = files, ProjectFilePath = "/project/pgproj.json" }, cancellationToken);

        var settings = new ProjectSettings { Name = "shop", Version = "1.0", Source = "./sql" };
        var fromSettings = await new PgProjEngine().BuildAsync(new BuildRequest { Files = files, Settings = settings, RootDirectory = "/project" }, cancellationToken);

        foreach (var built in new[] { fromMemory, fromSettings })
        {
            Assert.AreEqual(BuildStatus.Succeeded, built.Status);
            Assert.AreEqual("shop", built.ProjectName);
            Assert.IsTrue(built.Model!.Tables.Values.Any(static table => table.Name == "customer"));
        }
    }

    [TestMethod]
    public async Task UnsavedBuffersOverDisk()
    {
        var fixture = new ProjectFixture();
        fixture.WriteToDisk();
        try
        {
            var projectFile = fixture.ProjectFile;
            var openDocumentPath = Path.Combine(fixture.Root, "sql", "Schema", "orders.sql");
            var unsavedText = "CREATE TABLE app.orders (id bigint PRIMARY KEY);\nCREATE TABLE app.unsaved (a int);\n";
            var cancellationToken = TestContext.CancellationToken;

            var buffers = new OverlayFileSource(DiskFileSource.Instance);
            buffers.Set(openDocumentPath, unsavedText, version: 7);

            var fromBuffers = await new PgProjEngine().BuildAsync(new BuildRequest { Files = buffers, ProjectFilePath = projectFile }, cancellationToken);
            var document = fromBuffers.Documents.Single(d => d.Path == Path.GetFullPath(openDocumentPath));
            Console.WriteLine($"Built version {document.Version} of {document.Path}");

            Assert.AreEqual(7, document.Version);
            Assert.IsTrue(fromBuffers.Model!.Tables.Values.Any(static table => table.Name == "unsaved"), "the buffer is what was built, not the file");
        }
        finally
        {
            fixture.Delete();
        }
    }

    [TestMethod]
    public async Task BuildingAgainAfterAnEdit()
    {
        var fixture = new ProjectFixture();
        fixture.WriteToDisk();
        try
        {
            var projectFile = fixture.ProjectFile;
            var openDocumentPath = Path.Combine(fixture.Root, "sql", "Schema", "orders.sql");
            var editedText = "CREATE TABLE app.orders (id bigint PRIMARY KEY);\nCREATE TABLE app.edited (a int);\n";
            var buffers = new OverlayFileSource(DiskFileSource.Instance);
            var cancellationToken = TestContext.CancellationToken;

            var parseCache = new ParseCache();
            var request = new BuildRequest { Files = buffers, ProjectFilePath = projectFile, ParseCache = parseCache };

            var first = await new PgProjEngine().BuildAsync(request, cancellationToken);
            buffers.Set(openDocumentPath, editedText, version: 8);
            var afterTheEdit = await new PgProjEngine().BuildAsync(request, cancellationToken);
            Console.WriteLine($"{parseCache.Count} files' parses held");

            Assert.AreEqual(BuildStatus.Succeeded, first.Status);
            Assert.IsTrue(afterTheEdit.Model!.Tables.Values.Any(static table => table.Name == "edited"), "the edit is what was built");
            Assert.AreEqual(5, parseCache.Parses, "four files at first, then the edited one");
        }
        finally
        {
            fixture.Delete();
        }
    }

    [TestMethod]
    public async Task CheckingOneDocumentAsItIsTyped()
    {
        var fixture = new ProjectFixture();
        fixture.WriteToDisk();
        try
        {
            var projectFile = fixture.ProjectFile;
            var openDocumentPath = Path.Combine(fixture.Root, "sql", "Schema", "orders.sql");
            var typedText = "CREATE TABLE app.orders (id bigint PRIMARY KEY,, x int);\n";
            var buffers = new OverlayFileSource(DiskFileSource.Instance);
            var parseCache = new ParseCache();
            var cancellationToken = TestContext.CancellationToken;

            var check = await new PgProjEngine().CheckDocumentAsync(
                new DocumentCheckRequest { Files = buffers, ProjectFilePath = projectFile, DocumentPath = openDocumentPath, Text = typedText, Version = 9, ParseCache = parseCache },
                cancellationToken);
            if (check.Status == DocumentCheckStatus.Succeeded)
            {
                Console.WriteLine($"{check.Kind} version {check.Version}: {check.Diagnostics.Count} syntax diagnostics");
            }

            Assert.AreEqual(DocumentCheckKind.Schema, check.Kind);
            Assert.AreEqual(9, check.Version);
            Assert.AreEqual(EngineSeverity.Error, check.Diagnostics.Single().Severity);
            Assert.AreEqual(1, parseCache.Count);
        }
        finally
        {
            fixture.Delete();
        }
    }

    [TestMethod]
    public async Task BuildingADatabase()
    {
        var connectionString = MalformedConnectionString;
        var cancellationToken = TestContext.CancellationToken;

        var fromDatabase = await new PgProjEngine().BuildAsync(new BuildRequest { SourceConnectionString = connectionString }, cancellationToken);

        if (fromDatabase.Status is BuildStatus.Refused or BuildStatus.ConnectionFailed)
        {
            Console.WriteLine($"{fromDatabase.Status}: {fromDatabase.Message}");
        }
        else
        {
            Console.WriteLine($"{fromDatabase.Source}: {fromDatabase.Documents.Count} files, {fromDatabase.ErrorCount} errors");
        }

        Assert.AreEqual(BuildStatus.Refused, fromDatabase.Status);
        StringAssert.Contains(fromDatabase.Message, "The connection string is not valid");
    }

    [TestMethod]
    public async Task ComparingWithADatabase()
    {
        var fixture = new ProjectFixture();
        fixture.WriteToDisk();
        try
        {
            var projectFile = fixture.ProjectFile;
            var connectionString = MalformedConnectionString;
            var cancellationToken = TestContext.CancellationToken;

            var comparison = await new PgProjEngine().CompareAsync(
                new CompareRequest { ProjectFilePath = projectFile, TargetConnectionString = connectionString },
                cancellationToken);

            if (comparison.Status == CompareStatus.Succeeded)
            {
                Console.WriteLine($"{comparison.AddedCount} added, {comparison.RemovedCount} removed, {comparison.ModifiedCount} modified");
                foreach (var change in comparison.Changes.Modified)
                {
                    Console.WriteLine($"~ {change.NodeId}");
                }
            }
            else
            {
                Console.WriteLine($"{comparison.Status}: {comparison.Message}");
            }

            Assert.AreEqual(CompareStatus.Refused, comparison.Status);
            StringAssert.Contains(comparison.Message, "connection string is not valid");
        }
        finally
        {
            fixture.Delete();
        }
    }

    [TestMethod]
    public async Task PlanningThenPublishing()
    {
        var fixture = new ProjectFixture();
        fixture.WriteToDisk();
        try
        {
            var projectFile = fixture.ProjectFile;
            var connectionString = MalformedConnectionString;
            var cancellationToken = TestContext.CancellationToken;

            var engine = new PgProjEngine();
            var plan = await engine.PublishAsync(
                new PublishRequest { ProjectFilePath = projectFile, TargetConnectionString = connectionString, Mode = PublishMode.Plan },
                cancellationToken);

            if (plan.Status == PublishStatus.Planned)
            {
                Console.WriteLine(plan.Script);
                foreach (var warning in plan.Warnings)
                {
                    Console.WriteLine($"{warning.Kind}: {warning.Text}");
                }
            }

            // Later, once someone has read the script:
            var run = await engine.PublishAsync(
                new PublishRequest { ProjectFilePath = projectFile, TargetConnectionString = connectionString, Mode = PublishMode.Execute },
                cancellationToken);

            if (run.Status != PublishStatus.Published)
            {
                Console.WriteLine($"{run.Status}: {run.Message}");
                foreach (var refusal in run.Refusals)
                {
                    Console.WriteLine(refusal.Text);
                }
            }

            Assert.AreEqual(PublishStatus.Refused, plan.Status);
            Assert.AreEqual(PublishStatus.Refused, run.Status);
        }
        finally
        {
            fixture.Delete();
        }
    }

    [TestMethod]
    public async Task ScaffoldingADatabase()
    {
        var connectionString = MalformedConnectionString;
        var cancellationToken = TestContext.CancellationToken;

        var scaffold = await new PgProjEngine().ScaffoldAsync(
            new ScaffoldRequest { SourceConnectionString = connectionString, ProjectFilePath = "/new/pgproj.json", Files = new InMemoryFileSource() },
            cancellationToken);

        if (scaffold.Status == InitStatus.Succeeded)
        {
            Console.WriteLine(scaffold.ProjectFileText);
            foreach (var file in scaffold.Files)
            {
                Console.WriteLine($"{file.RelativePath}: {file.Content.Length} characters");
            }
        }

        Assert.AreEqual(InitStatus.Refused, scaffold.Status);
    }

    [TestMethod]
    public async Task DocumentingAProject()
    {
        var fixture = new ProjectFixture();
        fixture.WriteToDisk();
        try
        {
            var projectFile = fixture.ProjectFile;
            var cancellationToken = TestContext.CancellationToken;

            var documentation = await new PgProjEngine().DocumentAsync(
                new DocumentRequest { ProjectFilePath = projectFile, Formats = [DocFormat.Markdown, DocFormat.Json] },
                cancellationToken);

            foreach (var file in documentation.Files)
            {
                Console.WriteLine($"{file.FileName}: {file.Text.Length} characters");
            }

            foreach (var problem in documentation.Problems)
            {
                Console.WriteLine($"{problem.Severity} on {problem.Object}: {problem.Message}");
            }

            Assert.AreEqual(DocumentStatus.Succeeded, documentation.Status);
            CollectionAssert.AreEqual(new[] { DocFormat.Markdown, DocFormat.Json }, documentation.Files.Select(static file => file.Format).ToArray());
        }
        finally
        {
            fixture.Delete();
        }
    }

    [TestMethod]
    public async Task DescribingTheEnvironment()
    {
        var fixture = new ProjectFixture();
        fixture.WriteToDisk();
        try
        {
            var projectFile = fixture.ProjectFile;
            var connectionString = MalformedConnectionString;
            var cancellationToken = TestContext.CancellationToken;

            var about = await new PgProjEngine().AboutAsync(
                new AboutRequest { ProjectFilePath = projectFile, TargetConnectionString = connectionString, TargetConnectionOrigin = "settings" },
                cancellationToken);

            Console.WriteLine($"pgproj {about.EngineVersion} on {about.OperatingSystem} ({about.Runtime}); parser loads: {about.ParserLoaded}");
            if (about.Target is { } target)
            {
                Console.WriteLine(target.Status == AboutConnectionStatus.Connected
                    ? $"{target.Host}: PostgreSQL {target.ServerVersion}, superuser: {target.IsSuperuser}"
                    : $"{target.Host}: {target.Message}");
            }

            Assert.AreEqual(AboutProjectStatus.Found, about.ProjectStatus);
            Assert.IsTrue(about.ParserLoaded);
            Assert.AreEqual(AboutConnectionStatus.Failed, about.Target!.Status);
            Assert.AreEqual("settings", about.Target.Origin);
        }
        finally
        {
            fixture.Delete();
        }
    }

    [TestMethod]
    public async Task ProgressAndCancellation()
    {
        var fixture = new ProjectFixture();
        fixture.WriteToDisk();
        try
        {
            var projectFile = fixture.ProjectFile;

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var progress = new Progress<EngineEvent>(e => Console.WriteLine($"[{e.Level}] {e.Kind} {e.Message ?? e.Path}"));

            var withProgress = await new PgProjEngine().BuildAsync(new BuildRequest { ProjectFilePath = projectFile, Progress = progress }, cancellation.Token);

            Assert.AreEqual(BuildStatus.Succeeded, withProgress.Status);
        }
        finally
        {
            fixture.Delete();
        }
    }

    [TestMethod]
    public void EveryExampleOnTheLibraryPageIsRunHere()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "docs", "library.md"));
        var tests = Collapse(File.ReadAllText(Path.Combine(root, "src", "PgProj.Engine.Tests", "LibraryExamplesTests.cs")));

        var examples = CSharpBlock().Matches(page).Select(static match => match.Groups["code"].Value).ToArray();

        Assert.IsGreaterThanOrEqualTo(8, examples.Length, "the page's C# examples");
        foreach (var example in examples)
        {
            Assert.IsTrue(tests.Contains(Collapse(example), StringComparison.Ordinal), $"This example of docs/library.md is not copied into LibraryExamplesTests, so it is not compiled or run:\n{example}");
        }
    }

    /// <summary>The text with every run of white space made one space, so indentation and line endings do not matter.</summary>
    private static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "pgproj.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (pgproj.slnx) was not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"```csharp\r?\n(?<code>.*?)```", RegexOptions.Singleline)]
    private static partial Regex CSharpBlock();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public TestContext TestContext { get; set; } = null!;
}
