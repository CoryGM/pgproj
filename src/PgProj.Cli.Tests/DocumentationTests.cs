using PgProj.Engine;
using System.CommandLine;
using PgProj.Cli.Documentation;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-159: <c>pgproj document</c>. A comment's text is read as a description and <c>@tag: value</c> lines (every tag optional, custom tags kept, bad values reported without losing what was written),
/// the data dictionary is built from the model (project or live), and it is written as HTML, Markdown, and JSON.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DocumentationTests
{
    private const string Shop = """
        CREATE TABLE app.customer (id bigint PRIMARY KEY, email text NOT NULL UNIQUE, born date, note varchar(40), flag boolean DEFAULT false);
        CREATE TABLE app.orders (id int PRIMARY KEY, customer_id bigint NOT NULL REFERENCES app.customer (id) ON DELETE CASCADE, total numeric(10,2) CHECK (total >= 0));
        CREATE INDEX orders_customer ON app.orders (customer_id);
        CREATE VIEW app.big_orders AS SELECT * FROM app.orders WHERE total > 100;
        CREATE TYPE app.mood AS ENUM ('sad', 'ok');
        CREATE SEQUENCE app.ticket;
        CREATE FUNCTION app.add(a int, b int) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT a + b';
        CREATE TABLE audit.entry (id int PRIMARY KEY);
        COMMENT ON TABLE app.customer IS $$A buyer.

        @business-name: Customer
        @see-also: app.orders
        @see-also: https://example.com
        @team: growth$$;
        COMMENT ON COLUMN app.customer.email IS $$Where receipts go.
        @pii: true$$;
        """;

    [TestMethod]
    public async Task AWarningAboutATagDoesNotFailTheCommandButIsListed()
    {
        using var project = TempProject.Create("CREATE TABLE app.t (id int PRIMARY KEY, CONSTRAINT t_ck CHECK (id > 0));\nCOMMENT ON CONSTRAINT t_ck ON app.t IS '@pii: true';");

        var run = await RunAsync("document", "--project", project.File, "--format", "json");

        Assert.AreEqual(0, run.ExitCode, run.Output);
        StringAssert.Contains(run.Output, "Documentation warning: app.t.constraint:t_ck: @pii does not apply to a constraint");
        StringAssert.Contains(File.ReadAllText(Path.Combine(project.Directory, "docs", "database-documentation.json")), "\"pii\": true");
    }

    [TestMethod]
    public void FormatsAreParsedWithoutRegardToCase()
    {
        Assert.AreEqual(DocFormat.Markdown, DocumentationFiles.ParseFormat("MD"));
        Assert.AreEqual(DocFormat.Markdown, DocumentationFiles.ParseFormat("Markdown"));
        Assert.AreEqual(DocFormat.Html, DocumentationFiles.ParseFormat(" html "));
        Assert.IsNull(DocumentationFiles.ParseFormat("pdf"));
    }

    // ---- the command

    [TestMethod]
    public async Task ByDefaultTheCommandWritesHtmlAndJsonUnderDocsInTheProjectDirectory()
    {
        using var project = TempProject.Create(Shop);

        var run = await RunAsync("document", "--project", project.File);

        Assert.AreEqual(0, run.ExitCode, run.Output);
        Assert.IsTrue(File.Exists(Path.Combine(project.Directory, "docs", "database-documentation.html")));
        Assert.IsTrue(File.Exists(Path.Combine(project.Directory, "docs", "database-documentation.json")));
        Assert.IsFalse(File.Exists(Path.Combine(project.Directory, "docs", "database-documentation.md")));
        StringAssert.Contains(run.Output, "Wrote documentation:");
        StringAssert.Contains(run.Output, "Documented 2 schema(s)");
    }

    [TestMethod]
    public async Task EveryFormatCanBeChosenAsAListOrRepeatedAndOutputIsRelativeToTheProject()
    {
        using var project = TempProject.Create(Shop);

        var list = await RunAsync("document", "--project", project.File, "--format", "markdown,json", "--output", "out/a");
        var repeated = await RunAsync("document", "--project", project.File, "--format", "md", "--format", "html", "--output", "out/b");

        Assert.AreEqual(0, list.ExitCode, list.Output);
        Assert.AreEqual(0, repeated.ExitCode, repeated.Output);
        CollectionAssert.AreEquivalent(new[] { "database-documentation.json", "database-documentation.md" }, Directory.GetFiles(Path.Combine(project.Directory, "out", "a")).Select(Path.GetFileName).ToArray());
        CollectionAssert.AreEquivalent(new[] { "database-documentation.html", "database-documentation.md" }, Directory.GetFiles(Path.Combine(project.Directory, "out", "b")).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task AnUnknownFormatOrExistingModeOrBothSchemaOptionsAreRefused()
    {
        using var project = TempProject.Create(Shop);

        var format = await RunAsync("document", "--project", project.File, "--format", "pdf");
        var existing = await RunAsync("document", "--project", project.File, "--existing", "replace");
        var both = await RunAsync("document", "--project", project.File, "--schemas", "app", "--exclude-schemas", "audit");
        var unknown = await RunAsync("document", "--project", project.File, "--schemas", "nope");

        Assert.AreNotEqual(0, format.ExitCode);
        StringAssert.Contains(format.Output, "--format takes html, markdown, and json, not pdf.");
        StringAssert.Contains(existing.Output, "--existing takes overwrite, backup, or keep.");
        StringAssert.Contains(both.Output, "--schemas and --exclude-schemas cannot be used together.");
        Assert.AreEqual(1, unknown.ExitCode, unknown.Output);
        StringAssert.Contains(unknown.Output, "names a schema with no documented object: nope.");
        Assert.IsFalse(Directory.Exists(Path.Combine(project.Directory, "docs")));
    }

    [TestMethod]
    public async Task TheSchemaOptionsLimitWhatIsDocumented()
    {
        using var project = TempProject.Create(Shop);

        var run = await RunAsync("document", "--project", project.File, "--format", "json", "--schemas", "audit");

        Assert.AreEqual(0, run.ExitCode, run.Output);
        var json = File.ReadAllText(Path.Combine(project.Directory, "docs", "database-documentation.json"));
        StringAssert.Contains(json, "\"entry\"");
        Assert.IsFalse(json.Contains("\"customer\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TagErrorsAreListedTheFilesAreWrittenAndTheExitCodeIsOneUnlessToldNotTo()
    {
        using var project = TempProject.Create("CREATE TABLE app.t (id int PRIMARY KEY, a int);\nCOMMENT ON COLUMN app.t.a IS '@retention-months: soon';");

        var failing = await RunAsync("document", "--project", project.File, "--format", "json");
        var lenient = await RunAsync("document", "--project", project.File, "--format", "json", "--no-fail-on-doc-errors", "--existing", "overwrite");

        Assert.AreEqual(1, failing.ExitCode, failing.Output);
        StringAssert.Contains(failing.Output, "Documentation error: app.t.a: @retention-months must be a whole number");
        StringAssert.Contains(failing.Output, "--no-fail-on-doc-errors");
        Assert.AreEqual(0, lenient.ExitCode, lenient.Output);
        StringAssert.Contains(lenient.Output, "Documentation error:");
        StringAssert.Contains(File.ReadAllText(Path.Combine(project.Directory, "docs", "database-documentation.json")), "\"retention-months\": \"soon\"");
    }

    [TestMethod]
    public async Task AProjectWithErrorsGetsNoDocumentation()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int,, x int);");

        var run = await RunAsync("document", "--project", project.File);

        Assert.AreEqual(1, run.ExitCode, run.Output);
        StringAssert.Contains(run.Output, "No documentation was written");
        Assert.IsFalse(Directory.Exists(Path.Combine(project.Directory, "docs")));
    }

    [TestMethod]
    public async Task FilesAlreadyThereAreBackedUpIfWeWroteThemAndKeptIfAPersonDid()
    {
        using var project = TempProject.Create(Shop);
        var docs = Path.Combine(project.Directory, "docs");

        await RunAsync("document", "--project", project.File, "--format", "markdown,json");
        var second = await RunAsync("document", "--project", project.File, "--format", "markdown,json");
        Assert.AreEqual(0, second.ExitCode, second.Output);
        StringAssert.Contains(second.Output, "Moved existing documentation file to:");
        Assert.AreEqual(2, Directory.GetFiles(docs).Count(static file => Path.GetFileName(file).Split('.').Length == 3), "Both earlier files are backed up with one stamp.");

        File.WriteAllText(Path.Combine(docs, "database-documentation.md"), "# My own notes");
        var third = await RunAsync("document", "--project", project.File, "--format", "markdown,json", "--quiet");
        Assert.AreEqual(0, third.ExitCode, third.Output);
        Assert.AreEqual("# My own notes", File.ReadAllText(Path.Combine(docs, "database-documentation.md")));
        StringAssert.Contains(third.Output, "not written by document");
        StringAssert.Contains(third.Output, "The documentation was written to");

        var overwrite = await RunAsync("document", "--project", project.File, "--format", "markdown", "--existing", "overwrite", "--quiet");
        Assert.AreEqual(0, overwrite.ExitCode, overwrite.Output);
        StringAssert.Contains(File.ReadAllText(Path.Combine(docs, "database-documentation.md")), DocumentationFiles.Marker);
    }

    [TestMethod]
    public void TheCommandIsRegisteredAndItsHelpExplainsTheTagsAndAnExample()
    {
        var command = DocumentCommand.Create(new CliContext());

        Assert.AreEqual("document", command.Name);
        foreach (var option in new[] { "--project", "--source-connection", "--format", "--output", "--existing", "--schemas", "--exclude-schemas", "--no-fail-on-doc-errors", "--verbose", "--quiet" })
        {
            Assert.IsTrue(command.Options.Any(o => o.Name == option), option);
        }

        foreach (var tag in DocTags.StarterSet)
        {
            StringAssert.Contains(command.Description!, "@" + tag);
        }

        StringAssert.Contains(command.Description!, "COMMENT ON COLUMN app.customer.email IS");
        StringAssert.Contains(command.Description!, "--no-fail-on-doc-errors");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var context = new CliContext();
        var root = new RootCommand("pgproj") { DocumentCommand.Create(context) };
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            return (await root.Parse(args).InvokeAsync(), output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class TempProject : IDisposable
    {
        private TempProject(string directory)
        {
            Directory = directory;
            File = Path.Combine(directory, "pgproj.json");
        }

        public string Directory { get; }

        public string File { get; }

        public static TempProject Create(string sql)
        {
            var directory = Path.Combine(Path.GetTempPath(), "pgproj-doc-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(Path.Combine(directory, "pgproj.json"), """{ "name": "doc_test", "version": "1.0", "source": "./sql" }""");
            System.IO.Directory.CreateDirectory(Path.Combine(directory, "sql"));
            System.IO.File.WriteAllText(Path.Combine(directory, "sql", "t.sql"), sql);
            return new TempProject(directory);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
