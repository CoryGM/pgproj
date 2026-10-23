using PgProj.Engine;
using System.CommandLine;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-158: <c>--erd</c>. The diagram is Mermaid built from the project's parsed SQL (tables, columns with key markers, foreign keys as relationships, a stub marked external for a
/// table the project lacks, no views, no schema when there is only one); the files go where the user said or to a UTC-named default; <c>init</c> writes the bare
/// <c>.mmd</c> and the other commands also a companion <c>.md</c>; a project with errors gets nothing.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ErdTests
{
    private static DatabaseModel Model(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    [TestMethod]
    public void ATableIsAnEntityWithItsColumnsAndKeyMarkersAndTheSoleSchemaIsDropped()
    {
        var diagram = ErdDiagram.Build(Model("""
            CREATE TABLE app.customers (id bigint PRIMARY KEY, email text NOT NULL UNIQUE, joined timestamp with time zone, price numeric(10,2));
            CREATE TABLE app.orders (id int PRIMARY KEY, customer_id bigint NOT NULL REFERENCES app.customers (id), note text);
            """));

        StringAssert.StartsWith(diagram, "erDiagram");
        StringAssert.Contains(diagram, "    customers {");
        StringAssert.Contains(diagram, "        bigint id PK");
        StringAssert.Contains(diagram, "        text email UK");
        StringAssert.Contains(diagram, "        timestamptz joined");
        StringAssert.Contains(diagram, "        numeric price");
        StringAssert.Contains(diagram, "        bigint customer_id FK");
        Assert.IsFalse(diagram.Contains("app.", StringComparison.Ordinal), diagram);
    }

    [TestMethod]
    public void ARelationshipIsDrawnFromParentToChildWithAnOptionalParentWhenTheKeyIsNullable()
    {
        var diagram = ErdDiagram.Build(Model("""
            CREATE TABLE public.parent (id int PRIMARY KEY);
            CREATE TABLE public.required_child (id int PRIMARY KEY, parent_id int NOT NULL, CONSTRAINT req_fk FOREIGN KEY (parent_id) REFERENCES public.parent (id));
            CREATE TABLE public.optional_child (id int PRIMARY KEY, parent_id int, CONSTRAINT opt_fk FOREIGN KEY (parent_id) REFERENCES public.parent (id));
            """));

        StringAssert.Contains(diagram, "    parent ||--o{ required_child : \"req_fk\"");
        StringAssert.Contains(diagram, "    parent |o--o{ optional_child : \"opt_fk\"");
    }

    [TestMethod]
    public void SeveralSchemasKeepTheirNamesQuotedAndATableTheProjectLacksIsAnExternalStub()
    {
        var diagram = ErdDiagram.Build(Model("""
            CREATE TABLE app.customers (id int PRIMARY KEY);
            CREATE TABLE sales.orders (id int PRIMARY KEY, customer_id int REFERENCES app.customers (id), log_id int, CONSTRAINT log_fk FOREIGN KEY (log_id) REFERENCES audit.log_entry (id));
            """));

        StringAssert.Contains(diagram, "    \"app.customers\" {");
        StringAssert.Contains(diagram, "    \"sales.orders\" {");
        StringAssert.Contains(diagram, "    \"audit.log_entry (external)\"");
        StringAssert.Contains(diagram, "\"audit.log_entry (external)\" |o--o{ \"sales.orders\" : \"log_fk\"");
        StringAssert.Contains(diagram, "\"app.customers\" |o--o{ \"sales.orders\"");
    }

    [TestMethod]
    public void AViewIsLeftOutAndTheOrderDoesNotDependOnTheOrderOfTheFile()
    {
        var first = ErdDiagram.Build(Model("CREATE VIEW public.v AS SELECT 1 AS x; CREATE TABLE public.b (id int); CREATE TABLE public.a (id int);"));
        var second = ErdDiagram.Build(Model("CREATE TABLE public.a (id int); CREATE VIEW public.v AS SELECT 1 AS x; CREATE TABLE public.b (id int);"));

        Assert.AreEqual(first, second);
        Assert.IsFalse(first.Contains(" v ", StringComparison.Ordinal) || first.Contains("v {", StringComparison.Ordinal), first);
        Assert.IsTrue(first.IndexOf("    a {", StringComparison.Ordinal) < first.IndexOf("    b {", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NamesThatMermaidCannotTakeAreMadeSafe()
    {
        var diagram = ErdDiagram.Build(Model("CREATE TABLE public.\"My Table\" (\"Col Name\" character varying(20), \"x-y\" int);"));

        StringAssert.Contains(diagram, "    \"My Table\" {");
        StringAssert.Contains(diagram, "        varchar Col_Name");
        StringAssert.Contains(diagram, "integer x_y");
    }

    [TestMethod]
    public void TheDefaultFileIsNamedByTheUtcStartTimeInTheProjectDirectoryAndAPathIsRelativeToIt()
    {
        var project = Path.Combine(Path.GetTempPath(), "proj");
        var started = new DateTimeOffset(2026, 10, 2, 9, 3, 11, TimeSpan.FromHours(-5)).UtcDateTime;

        var byDefault = ErdFiles.Resolve(null, project, started, companion: true);
        Assert.AreEqual(Path.Combine(project, "erd-2026-10-02-14-03-11.mmd"), byDefault.DiagramPath);
        Assert.AreEqual(Path.Combine(project, "erd-2026-10-02-14-03-11.md"), byDefault.CompanionPath);

        Assert.AreEqual(Path.Combine(project, "docs", "model.mmd"), ErdFiles.Resolve("docs/model", project, started, true).DiagramPath);
        Assert.AreEqual(Path.Combine(project, "docs", "model.mmd"), ErdFiles.Resolve("docs/model.mmd", project, started, true).DiagramPath);
        Assert.AreEqual(Path.Combine(project, "docs", "model.md"), ErdFiles.Resolve("docs/model.md", project, started, true).CompanionPath);
        Assert.AreEqual(Path.Combine(project, "docs", "model.v2.mmd"), ErdFiles.Resolve("docs/model.v2", project, started, true).DiagramPath);

        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "e.mmd");
        Assert.AreEqual(absolute, ErdFiles.Resolve(absolute, project, started, true).DiagramPath);
        Assert.IsNull(ErdFiles.Resolve(null, project, started, companion: false).CompanionPath);
    }

    [TestMethod]
    public void TheCompanionLinksTheDiagramFileBesideItAndShowsIt()
    {
        var page = ErdFiles.Companion("shop", "erd one.mmd", "erDiagram\n    a {\n    }\n", new DateTime(2026, 10, 2, 14, 3, 11, DateTimeKind.Utc));

        StringAssert.Contains(page, "# Entity relationship diagram: shop");
        StringAssert.Contains(page, "[erd one.mmd](erd%20one.mmd)");
        StringAssert.Contains(page, "2026-10-02 14:03:11 UTC");
        StringAssert.Contains(page, "```mermaid");
        StringAssert.Contains(page, "erDiagram");
    }

    [TestMethod]
    public async Task BuildWritesTheDiagramAndItsCompanionAndQuietKeepsTheFilesButNotTheMessage()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY); CREATE TABLE public.u (id int PRIMARY KEY, t_id int REFERENCES public.t (id));");

        var loud = await RunAsync("build", "--project", project.File, "--erd", "out/model");
        var quiet = await RunAsync("build", "--project", project.File, "--erd", "--quiet");

        Assert.AreEqual(0, loud.ExitCode, loud.Output);
        StringAssert.Contains(loud.Output, "Wrote ERD:");
        Assert.IsTrue(File.Exists(Path.Combine(project.Directory, "out", "model.mmd")));
        var page = File.ReadAllText(Path.Combine(project.Directory, "out", "model.md"));
        StringAssert.Contains(page, "[model.mmd](model.mmd)");
        StringAssert.Contains(File.ReadAllText(Path.Combine(project.Directory, "out", "model.mmd")), "t |o--o{ u");

        Assert.AreEqual(0, quiet.ExitCode, quiet.Output);
        Assert.AreEqual(string.Empty, quiet.Output);
        Assert.AreEqual(1, Directory.GetFiles(project.Directory, "erd-*.mmd").Length);
        Assert.AreEqual(1, Directory.GetFiles(project.Directory, "erd-*.md").Length);
    }

    [TestMethod]
    public async Task AProjectWithErrorsGetsNoDiagram()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int,, x int);");

        var run = await RunAsync("build", "--project", project.File, "--erd");

        Assert.AreEqual(1, run.ExitCode, run.Output);
        StringAssert.Contains(run.Output, "No entity relationship diagram was written: the project has errors");
        Assert.AreEqual(0, Directory.GetFiles(project.Directory, "erd-*").Length);
    }

    [TestMethod]
    public async Task DiffAndPublishWriteTheDiagramBeforeTheyConnect()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY);");
        const string Unreachable = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1";

        var diff = await RunAsync("diff", "--project", project.File, "--target-connection", Unreachable, "--erd", "d");
        var publish = await RunAsync("publish", "--project", project.File, "--target-connection", Unreachable, "--erd", "p");

        Assert.AreEqual(1, diff.ExitCode, diff.Output);
        Assert.AreEqual(1, publish.ExitCode, publish.Output);
        Assert.IsTrue(File.Exists(Path.Combine(project.Directory, "d.mmd")) && File.Exists(Path.Combine(project.Directory, "d.md")));
        Assert.IsTrue(File.Exists(Path.Combine(project.Directory, "p.mmd")) && File.Exists(Path.Combine(project.Directory, "p.md")));
    }

    private const string ThreeSchemas = """
        CREATE TABLE accounts.account (id int PRIMARY KEY);
        CREATE TABLE policies.policy (id int PRIMARY KEY, account_id int NOT NULL REFERENCES accounts.account (id));
        CREATE TABLE policies.policy_version (id int PRIMARY KEY, policy_id int NOT NULL REFERENCES policies.policy (id));
        CREATE TABLE audit.entry (id int PRIMARY KEY, policy_id int REFERENCES policies.policy (id));
        """;

    [TestMethod]
    public void TheSchemasFilterDrawsOnlyThoseTablesAndDropsTheSchemaNameWhenOneIsLeft()
    {
        var diagram = ErdDiagram.Build(Model(ThreeSchemas), new ErdFilter(["policies"], Exclude: false));

        StringAssert.Contains(diagram, "    policy {");
        StringAssert.Contains(diagram, "    policy_version {");
        Assert.IsFalse(diagram.Contains("entry {", StringComparison.Ordinal), "audit.entry is not drawn.");
        Assert.IsFalse(diagram.Contains("account {", StringComparison.Ordinal), "accounts.account is not drawn as a table.");
        StringAssert.Contains(diagram, "    \"accounts.account (external)\"");
        StringAssert.Contains(diagram, "\"accounts.account (external)\" ||--o{ policy : ");
        StringAssert.Contains(diagram, "    policy ||--o{ policy_version : ");
        Assert.IsFalse(diagram.Contains("audit", StringComparison.Ordinal), "A foreign key out of a table that is not drawn is not drawn.");
    }

    [TestMethod]
    public void TheExcludeFilterDrawsEveryOtherTableAndANameIsComparedWithoutRegardToCase()
    {
        var diagram = ErdDiagram.Build(Model(ThreeSchemas), new ErdFilter(["AUDIT"], Exclude: true));

        StringAssert.Contains(diagram, "\"accounts.account\" {");
        StringAssert.Contains(diagram, "\"policies.policy\" {");
        Assert.IsFalse(diagram.Contains("entry", StringComparison.Ordinal), diagram);
        Assert.IsFalse(diagram.Contains("(external)", StringComparison.Ordinal));
        Assert.AreEqual(ErdDiagram.Build(Model(ThreeSchemas)), ErdDiagram.Build(Model(ThreeSchemas), ErdFilter.None));
    }

    [TestMethod]
    public void TypesLoseTheirPublicPrefixAndKeepAnotherSchemasName()
    {
        var diagram = ErdDiagram.Build(Model("CREATE TYPE app.mood AS ENUM ('a'); CREATE TABLE app.t (a public.citext, b app.mood, c pg_catalog.int4);"));

        StringAssert.Contains(diagram, "        citext a");
        StringAssert.Contains(diagram, "        app_mood b");
        StringAssert.Contains(diagram, "        integer c");
    }

    [TestMethod]
    public void ASchemaTheProjectHasNoTableInIsAnErrorThatNamesIt()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => ErdFiles.CheckSchemas(new ErdFilter(["policies", "nope"], false), ["accounts", "policies"]));

        StringAssert.Contains(exception.Message, "--erd-schemas names a schema the project has no table in: nope.");
        ErdFiles.CheckSchemas(ErdFilter.None, []);
        ErdFiles.CheckSchemas(new ErdFilter(["POLICIES"], true), ["policies"]);
    }

    [TestMethod]
    public async Task BuildFiltersTheDiagramAndRefusesTheOptionsWithoutErdOrTogetherOrWithAnUnknownSchema()
    {
        using var project = TempProject.Create(ThreeSchemas);

        var filtered = await RunAsync("build", "--project", project.File, "--erd", "f", "--erd-schemas", "policies");
        Assert.AreEqual(0, filtered.ExitCode, filtered.Output);
        var diagram = File.ReadAllText(Path.Combine(project.Directory, "f.mmd"));
        StringAssert.Contains(diagram, "policy_version {");
        Assert.IsFalse(diagram.Contains("entry", StringComparison.Ordinal));

        var excluded = await RunAsync("build", "--project", project.File, "--erd", "e", "--erd-exclude-schemas", "audit,accounts");
        Assert.AreEqual(0, excluded.ExitCode, excluded.Output);
        Assert.IsFalse(File.ReadAllText(Path.Combine(project.Directory, "e.mmd")).Contains("accounts.account {", StringComparison.Ordinal));

        var withoutErd = await RunAsync("build", "--project", project.File, "--erd-schemas", "policies");
        Assert.AreNotEqual(0, withoutErd.ExitCode);
        StringAssert.Contains(withoutErd.Output, "only apply with --erd");

        var both = await RunAsync("build", "--project", project.File, "--erd", "--erd-schemas", "a", "--erd-exclude-schemas", "b");
        Assert.AreNotEqual(0, both.ExitCode);
        StringAssert.Contains(both.Output, "--erd-schemas and --erd-exclude-schemas cannot be used together.");

        var unknown = await RunAsync("build", "--project", project.File, "--erd", "u", "--erd-schemas", "nope");
        Assert.AreEqual(1, unknown.ExitCode, unknown.Output);
        StringAssert.Contains(unknown.Output, "names a schema the project has no table in: nope.");
        Assert.IsFalse(File.Exists(Path.Combine(project.Directory, "u.mmd")));
    }

    private static string[] Files(TempProject project, string pattern) => Directory.GetFiles(Path.Combine(project.Directory, "out"), pattern).Order(StringComparer.Ordinal).ToArray();

    [TestMethod]
    public async Task ByDefaultAFileAnEarlierRunWroteIsBackedUpAndTheNewOneTakesItsPlace()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY);");
        await RunAsync("build", "--project", project.File, "--erd", "out/m");
        var first = File.ReadAllText(Path.Combine(project.Directory, "out", "m.mmd"));
        File.AppendAllText(Path.Combine(project.Directory, "out", "m.mmd"), "    %% first run" + Environment.NewLine);

        var second = await RunAsync("build", "--project", project.File, "--erd", "out/m");

        Assert.AreEqual(0, second.ExitCode, second.Output);
        StringAssert.Contains(second.Output, "Moved existing ERD file to:");
        var diagramBackups = Files(project, "m.*.mmd");
        Assert.AreEqual(1, diagramBackups.Length);
        Assert.AreEqual(1, Files(project, "m.*.md").Length);
        StringAssert.Contains(File.ReadAllText(diagramBackups[0]), "%% first run");
        var current = File.ReadAllText(Path.Combine(project.Directory, "out", "m.mmd"));
        StringAssert.Contains(current, ErdFiles.Marker);
        Assert.IsFalse(current.Contains("first run", StringComparison.Ordinal));
        StringAssert.Contains(File.ReadAllText(Path.Combine(project.Directory, "out", "m.md")), "[m.mmd](m.mmd)");
        Assert.IsTrue(first.Contains("erDiagram", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ByDefaultAFileAPersonWroteIsKeptAndTheNewDiagramAndItsPageGoToStampedNames()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY);");
        Directory.CreateDirectory(Path.Combine(project.Directory, "out"));
        var handWritten = Path.Combine(project.Directory, "out", "m.md");
        File.WriteAllText(handWritten, "# My notes" + Environment.NewLine);

        var run = await RunAsync("build", "--project", project.File, "--erd", "out/m");

        Assert.AreEqual(0, run.ExitCode, run.Output);
        StringAssert.Contains(run.Output, "not written by --erd");
        Assert.AreEqual("# My notes" + Environment.NewLine, File.ReadAllText(handWritten));
        Assert.IsFalse(File.Exists(Path.Combine(project.Directory, "out", "m.mmd")));
        var diagram = Files(project, "m.*.mmd").Single();
        var page = Files(project, "m.*.md").Single();
        StringAssert.Contains(File.ReadAllText(diagram), ErdFiles.Marker);
        StringAssert.Contains(File.ReadAllText(page), $"[{Path.GetFileName(diagram)}]");
    }

    [TestMethod]
    public async Task KeepingAFileAPersonWroteIsSaidEvenWithQuiet()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY);");
        Directory.CreateDirectory(Path.Combine(project.Directory, "out"));
        File.WriteAllText(Path.Combine(project.Directory, "out", "m.mmd"), "mine");

        var quiet = await RunAsync("build", "--project", project.File, "--erd", "out/m", "--quiet");

        Assert.AreEqual(0, quiet.ExitCode, quiet.Output);
        StringAssert.Contains(quiet.Output, "not written by --erd");
        StringAssert.Contains(quiet.Output, "The diagram was written to");
        Assert.IsFalse(quiet.Output.Contains("Wrote ERD", StringComparison.Ordinal), "The progress lines stay quiet.");

        var explicitKeep = await RunAsync("build", "--project", project.File, "--erd", "out/m", "--erd-existing", "keep", "--quiet");
        Assert.AreEqual(string.Empty, explicitKeep.Output, "A mode the user chose needs no notice.");
    }

    [TestMethod]
    public async Task ADiagramOverTheViewerLimitIsWarnedAboutAndWrittenAnyway()
    {
        var tables = string.Join(" ", Enumerable.Range(0, 700).Select(static n => $"CREATE TABLE public.table_with_a_long_name_{n} (id int PRIMARY KEY, name text, created timestamp with time zone);"));
        using var project = TempProject.Create(tables);

        var run = await RunAsync("build", "--project", project.File, "--erd", "big");
        var small = await RunAsync("build", "--project", project.File, "--erd", "small", "--erd-schemas", "public");

        Assert.AreEqual(0, run.ExitCode, run.Output);
        StringAssert.Contains(run.Output, "characters; some viewers");
        StringAssert.Contains(run.Output, "may not render it. It was written anyway");
        Assert.IsTrue(File.ReadAllText(Path.Combine(project.Directory, "big.mmd")).Length > ErdFiles.ViewerLimit);
        Assert.AreEqual(0, small.ExitCode, small.Output);

        using var tiny = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY);");
        var none = await RunAsync("build", "--project", tiny.File, "--erd", "t");
        Assert.IsFalse(none.Output.Contains("some viewers", StringComparison.Ordinal), none.Output);
    }

    [TestMethod]
    public async Task AFailureWhilePlacingTheFilesLeavesNoTemporaryFile()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY);");
        var output = new ErdOutput(Path.Combine(project.Directory, "m.mmd"), Path.Combine(project.Directory, "m.md"));
        Directory.CreateDirectory(output.CompanionPath!);
        var log = new CliLog(Verbosity.Quiet, TextWriter.Null, TextWriter.Null);

        // Moving a file onto a directory is refused as UnauthorizedAccessException on Windows and as IOException on Linux.
        var failure = await Assert.ThrowsAsync<Exception>(() => ErdFiles.WriteAsync(Model("CREATE TABLE public.t (id int PRIMARY KEY);"), "p", output, DateTime.UtcNow, log, ErdFilter.None, ErdExisting.Default, CancellationToken.None));
        Assert.IsTrue(failure is UnauthorizedAccessException or IOException, failure.ToString());

        Assert.AreEqual(0, Directory.GetFiles(project.Directory, "*.tmp", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task TheFilesAreWrittenWithoutLeavingTemporaryNamesBehind()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY);");

        await RunAsync("build", "--project", project.File, "--erd", "out/m");

        Assert.AreEqual(0, Directory.GetFiles(project.Directory, "*.tmp", SearchOption.AllDirectories).Length);
        Assert.IsTrue(File.Exists(Path.Combine(project.Directory, "out", "m.mmd")) && File.Exists(Path.Combine(project.Directory, "out", "m.md")));
    }

    [TestMethod]
    public async Task ExplicitModesOverwriteBackUpOrKeepWhateverIsThere()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY);");
        Directory.CreateDirectory(Path.Combine(project.Directory, "out"));
        var diagramPath = Path.Combine(project.Directory, "out", "m.mmd");

        File.WriteAllText(diagramPath, "mine");
        var overwrite = await RunAsync("build", "--project", project.File, "--erd", "out/m", "--erd-existing", "overwrite");
        Assert.AreEqual(0, overwrite.ExitCode, overwrite.Output);
        StringAssert.Contains(File.ReadAllText(diagramPath), ErdFiles.Marker);
        Assert.AreEqual(0, Files(project, "m.*.mmd").Length);

        File.WriteAllText(diagramPath, "mine");
        var backup = await RunAsync("build", "--project", project.File, "--erd", "out/m", "--erd-existing", "BACKUP");
        Assert.AreEqual(0, backup.ExitCode, backup.Output);
        StringAssert.Contains(File.ReadAllText(diagramPath), ErdFiles.Marker);
        Assert.AreEqual("mine", File.ReadAllText(Files(project, "m.*.mmd").Single()));

        var before = File.ReadAllText(diagramPath);
        var keep = await RunAsync("build", "--project", project.File, "--erd", "out/m", "--erd-existing", "keep");
        Assert.AreEqual(0, keep.ExitCode, keep.Output);
        Assert.AreEqual(before, File.ReadAllText(diagramPath));
        Assert.AreEqual(2, Files(project, "m.*.mmd").Length);
    }

    [TestMethod]
    public void AStampThatIsTakenGetsANumberSoNothingIsReplaced()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY);");
        var path = Path.Combine(project.Directory, "m.mmd");
        File.WriteAllText(path, "mine");
        var at = new DateTime(2026, 10, 2, 14, 3, 11, DateTimeKind.Utc);
        File.WriteAllText(Path.Combine(project.Directory, "m.2026-10-02-14-03-11.mmd"), "older");
        var log = new CliLog(Verbosity.Normal, TextWriter.Null, TextWriter.Null);

        var room = ErdFiles.MakeRoom(new ErdOutput(path, null), ErdExisting.Keep, at, log);

        Assert.AreEqual(Path.Combine(project.Directory, "m.2026-10-02-14-03-11-2.mmd"), room.DiagramPath);
        Assert.AreEqual("older", File.ReadAllText(Path.Combine(project.Directory, "m.2026-10-02-14-03-11.mmd")));
    }

    [TestMethod]
    public void ThePlacementSaysWhatWasDoneAndWhy()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY);");
        var path = Path.Combine(project.Directory, "m.mmd");
        var at = new DateTime(2026, 10, 2, 14, 3, 11, DateTimeKind.Utc);
        var log = new CliLog(Verbosity.Quiet, TextWriter.Null, TextWriter.Null);
        var output = new ErdOutput(path, null);

        var none = ErdFiles.MakeRoom(output, ErdExisting.Default, at, log).Placement!;
        Assert.AreEqual("Written", none.Action);
        Assert.AreEqual("No file was there.", none.Reason);

        File.WriteAllText(path, "mine");
        var kept = ErdFiles.MakeRoom(output, ErdExisting.Default, at, log);
        StringAssert.Contains(kept.Placement!.Action, "Kept the existing file and wrote the diagram under a new name, m.2026-10-02-14-03-11.mmd");
        StringAssert.Contains(kept.Placement.Reason, "Default");
        StringAssert.Contains(kept.Placement.Reason, "not written by --erd");

        var overwritten = ErdFiles.MakeRoom(output, ErdExisting.Overwrite, at, log).Placement!;
        Assert.AreEqual("Overwrote the existing file.", overwritten.Action);
        Assert.AreEqual("Specified: --erd-existing overwrite.", overwritten.Reason);

        File.WriteAllText(path, "%% " + ErdFiles.Marker);
        var backedUp = ErdFiles.MakeRoom(output, ErdExisting.Default, at, log).Placement!;
        StringAssert.Contains(backedUp.Action, "Backed up the existing file to m.2026-10-02-14-03-11.mmd");
        StringAssert.Contains(backedUp.Reason, "written by --erd, so it is backed up");
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task ErdHtmlAlsoWritesAWebPageThatDrawsTheDiagramAndIsHandledWithTheOtherFiles()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY); CREATE TABLE public.u (id int PRIMARY KEY, t_id int REFERENCES public.t (id));");

        var without = await RunAsync("build", "--project", project.File, "--erd", "out/m");
        Assert.AreEqual(0, without.ExitCode, without.Output);
        Assert.IsFalse(File.Exists(Path.Combine(project.Directory, "out", "m.html")));

        var with = await RunAsync("build", "--project", project.File, "--erd", "out/m", "--erd-html");
        Assert.AreEqual(0, with.ExitCode, with.Output);
        StringAssert.Contains(with.Output, "Wrote ERD web page:");
        var html = File.ReadAllText(Path.Combine(project.Directory, "out", "m.html"));
        StringAssert.Contains(html, "<pre class=\"mermaid\">");
        StringAssert.Contains(html, "erDiagram");
        StringAssert.Contains(html, "t |o--o{ u");
        StringAssert.Contains(html, ErdFiles.MermaidScript);
        StringAssert.Contains(html, ErdFiles.Marker);
        StringAssert.Contains(html, "href=\"m.mmd\"");
        Assert.AreEqual(0, Files(project, "m.*.html").Length, "The first run wrote no page, so there is none to back up.");
        Assert.AreEqual(1, Files(project, "m.*.mmd").Length);
        Assert.AreEqual(1, Files(project, "m.*.md").Length);

        var again = await RunAsync("build", "--project", project.File, "--erd", "out/m", "--erd-html");
        Assert.AreEqual(0, again.ExitCode, again.Output);
        Assert.AreEqual(1, Files(project, "m.*.html").Length, "The earlier page is backed up with the other files.");
        Assert.IsTrue(File.Exists(Path.Combine(project.Directory, "out", "m.html")));
    }

    [TestMethod]
    public void TheWebPageEncodesTheDiagramSoItCannotBreakOutOfThePage()
    {
        var page = ErdFiles.HtmlPage("a<b", "erd one.mmd", "erDiagram\n    x {\n        text a<script>\n    }\n", new DateTime(2026, 10, 2, 14, 3, 11, DateTimeKind.Utc));

        StringAssert.Contains(page, "Entity relationship diagram: a&lt;b");
        StringAssert.Contains(page, "text a&lt;script&gt;");
        Assert.IsFalse(page.Contains("a<script>", StringComparison.Ordinal));
        StringAssert.Contains(page, "href=\"erd%20one.mmd\"");
        Assert.IsTrue(ErdFiles.IsGenerated(WriteTemp(page)));
    }

    private static string WriteTemp(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".html");
        File.WriteAllText(path, text);
        return path;
    }

    [TestMethod]
    public async Task ErdHtmlNeedsErd()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY);");

        var run = await RunAsync("build", "--project", project.File, "--erd-html");

        Assert.AreNotEqual(0, run.ExitCode);
        StringAssert.Contains(run.Output, "--erd-html only applies with --erd.");
    }

    [TestMethod]
    public async Task ErdExistingNeedsErdAndOneOfTheThreeModes()
    {
        using var project = TempProject.Create("CREATE TABLE public.t (id int PRIMARY KEY);");

        var withoutErd = await RunAsync("build", "--project", project.File, "--erd-existing", "keep");
        Assert.AreNotEqual(0, withoutErd.ExitCode);
        StringAssert.Contains(withoutErd.Output, "--erd-existing only applies with --erd.");

        var unknown = await RunAsync("build", "--project", project.File, "--erd", "--erd-existing", "replace");
        Assert.AreNotEqual(0, unknown.ExitCode);
        StringAssert.Contains(unknown.Output, "--erd-existing takes overwrite, backup, or keep.");
    }

    [TestMethod]
    public void EveryCommandTakesErdAndHelpDescribesIt()
    {
        var context = new CliContext();
        foreach (var command in new[] { InitCommand.Create(context), BuildCommand.Create(context), DiffCommand.Create(context), PublishCommand.Create(context) })
        {
            Assert.IsTrue(command.Options.Any(static o => o.Name == "--erd"), $"{command.Name} has no --erd");
            Assert.IsTrue(command.Options.Any(static o => o.Name == "--erd-schemas") && command.Options.Any(static o => o.Name == "--erd-exclude-schemas"), $"{command.Name} has no ERD schema filters");
            Assert.IsTrue(command.Options.Any(static o => o.Name == "--erd-existing"), $"{command.Name} has no --erd-existing");
            Assert.IsTrue(command.Options.Any(static o => o.Name == "--erd-html"), $"{command.Name} has no --erd-html");
            StringAssert.Contains(command.Description!, "--erd");
            StringAssert.Contains(command.Description!, "--erd-html");
            StringAssert.Contains(command.Description!, "--erd-existing");
        }

        // build and diff both write the engine's JSON document with --format json (G-309b, G-309f).
        foreach (var command in new[] { BuildCommand.Create(context), DiffCommand.Create(context) })
        {
            var format = command.Options.Single(static o => o.Name == "--format");
            Assert.AreEqual("text|json", format.HelpName, $"{command.Name}'s --format");
            StringAssert.Contains(command.Description!, "--format json writes", $"{command.Name}'s description");
            Assert.IsTrue(command.Description!.Split('\n').Any(static line => line.TrimStart().StartsWith("pgproj ", StringComparison.Ordinal) && line.TrimEnd().EndsWith("--format json", StringComparison.Ordinal)), $"{command.Name} has no --format json example");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var context = new CliContext();
        var root = new RootCommand("pgproj") { BuildCommand.Create(context), DiffCommand.Create(context), PublishCommand.Create(context) };
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
            var directory = Path.Combine(Path.GetTempPath(), "pgproj-erd-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(Path.Combine(directory, "pgproj.json"), """{ "name": "erd_test", "version": "1.0", "source": "./sql" }""");
            System.IO.Directory.CreateDirectory(Path.Combine(directory, "sql"));
            System.IO.File.WriteAllText(Path.Combine(directory, "sql", "t.sql"), sql);
            return new TempProject(directory);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
