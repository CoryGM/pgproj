using PgProj.Core;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-313: <see cref="PgProjEngine.CheckDocumentAsync"/> checks one document on its own: a schema or template file gets the parser's diagnostics a full build
/// gives for it, a deploy script the build's check of it, each parsed as the build parses it; it fills the build's <see cref="ParseCache"/>, carries the
/// version through, reads an unsaved buffer, and says when a file is not the project's.
/// </summary>
[TestClass]
public sealed class CheckDocumentAsyncTests
{
    private const string Root = "/check";

    [TestMethod]
    public async Task EachFileGetsWhatAFullBuildGivesForIt()
    {
        // Arrange: a project with tenants whose files each have something to say.
        var files = Project();
        var build = await new PgProjEngine().BuildAsync(new BuildRequest { Files = files, ProjectFilePath = ProjectFile });

        foreach (var (path, kind) in new[]
        {
            ("sql/Schema/app.sql", DocumentCheckKind.Schema),
            ("sql/Schema/broken.sql", DocumentCheckKind.Schema),
            ("sql/Tenant/orders.sql", DocumentCheckKind.TenantTemplate),
            ("sql/Tenant/broken.sql", DocumentCheckKind.TenantTemplate),
            ("scripts/pre.sql", DocumentCheckKind.DeployScript),
            ("scripts/seed.sql", DocumentCheckKind.DeployScript),
            ("scripts/vars.sql", DocumentCheckKind.DeployScript),
            ("scripts/tenant.sql", DocumentCheckKind.DeployScript),
        })
        {
            var fullPath = Path.GetFullPath(Path.Combine(Root, path));

            // Act
            var check = await new PgProjEngine().CheckDocumentAsync(new DocumentCheckRequest { Files = files, ProjectFilePath = ProjectFile, DocumentPath = fullPath });

            // Assert
            Assert.AreEqual(DocumentCheckStatus.Succeeded, check.Status, path);
            Assert.AreEqual(kind, check.Kind, path);
            var fromBuild = build.Diagnostics.Where(d => string.Equals(d.File, fullPath, StringComparison.Ordinal)).ToArray();
            Assert.IsNotEmpty(fromBuild, $"{path}: the project gives each file something to say");
            CollectionAssert.AreEqual(fromBuild, check.Diagnostics.ToArray(), $"{path}:\n{string.Join("\n", check.Diagnostics)}\n---\n{string.Join("\n", fromBuild)}");
        }
    }

    [TestMethod]
    public async Task AnUnsavedBufferIsCheckedAsTheBuildWouldParseIt_AndFillsTheCache()
    {
        // Arrange: the buffer differs from the file; the project's searchPath makes app the schema an unqualified name is created in.
        var files = Project();
        var cache = new ParseCache();
        var path = Path.GetFullPath(Path.Combine(Root, "sql", "Schema", "app.sql"));
        const string Buffer = "CREATE TABLE app.customer (id bigint PRIMARY KEY);\nCREATE TABLE plain (a int,, b int);\n";

        // Act
        var check = await new PgProjEngine().CheckDocumentAsync(new DocumentCheckRequest { Files = files, ProjectFilePath = ProjectFile, DocumentPath = path, Text = Buffer, Version = 12, ParseCache = cache });

        // Assert: the buffer's error, its version, and the parse held under the project's default schema, so the build of the same text does not parse it.
        Assert.AreEqual(12, check.Version);
        Assert.AreEqual(2, check.Diagnostics.Single().Line);
        Assert.AreEqual(1, cache.Parses);
        files.Set(path, Buffer);
        await new PgProjEngine().BuildAsync(new BuildRequest { Files = files, ProjectFilePath = ProjectFile, ParseCache = cache });
        Assert.AreEqual(FileCount, cache.Parses, "every other file parsed once, the checked one not again");
    }

    [TestMethod]
    public async Task TheVersionComesFromTheFileSourceWhenNoTextIsGiven()
    {
        var files = Project();
        var path = Path.GetFullPath(Path.Combine(Root, "sql", "Schema", "app.sql"));
        files.Set(path, "CREATE TABLE app.customer (id bigint PRIMARY KEY);\n", version: 4);

        var check = await new PgProjEngine().CheckDocumentAsync(new DocumentCheckRequest { Files = files, ProjectFilePath = ProjectFile, DocumentPath = path });

        Assert.AreEqual(4, check.Version);
        Assert.IsEmpty(check.Diagnostics);
    }

    [TestMethod]
    public async Task AFileTheProjectDoesNotReadIsNotChecked()
    {
        var files = Project().Set(Path.Combine(Root, "notes", "scratch.sql"), "SELEC 1;");

        var check = await new PgProjEngine().CheckDocumentAsync(new DocumentCheckRequest { Files = files, ProjectFilePath = ProjectFile, DocumentPath = Path.Combine(Root, "notes", "scratch.sql") });

        Assert.AreEqual(DocumentCheckStatus.NotInProject, check.Status);
        Assert.AreEqual(DocumentCheckKind.None, check.Kind);
        Assert.IsEmpty(check.Diagnostics);
    }

    [TestMethod]
    public async Task AMissingOrInvalidProjectIsAStatus()
    {
        var missing = await new PgProjEngine().CheckDocumentAsync(new DocumentCheckRequest { Files = new InMemoryFileSource(), ProjectFilePath = ProjectFile, DocumentPath = "/check/a.sql" });
        var invalid = await new PgProjEngine().CheckDocumentAsync(new DocumentCheckRequest
        {
            Files = new InMemoryFileSource()
                .Set(ProjectFile, """{ "name": "x", "version": "1", "source": "./sql", "searchPath": [] }""")
                .Set(Path.GetFullPath(Path.Combine(Root, "sql", "a.sql")), "CREATE TABLE a (x int);"),
            ProjectFilePath = ProjectFile,
            DocumentPath = "/check/a.sql",
        });

        Assert.AreEqual(DocumentCheckStatus.ProjectNotFound, missing.Status);
        Assert.AreEqual(DocumentCheckStatus.ProjectInvalid, invalid.Status);
        StringAssert.Contains(invalid.Message, "searchPath");
    }

    [TestMethod]
    public async Task APasswordInAMessageIsMasked()
    {
        var files = Project();
        var path = Path.GetFullPath(Path.Combine(Root, "sql", "Schema", "app.sql"));

        var check = await new PgProjEngine().CheckDocumentAsync(new DocumentCheckRequest
        {
            Files = files,
            ProjectFilePath = ProjectFile,
            DocumentPath = path,
            Text = "CREATE USER MAPPING FOR app SERVER remote OPTIONS (user 'u', password 'hunter2');\n",
        });

        Assert.IsNotEmpty(check.Diagnostics);
        Assert.IsFalse(check.Diagnostics.Any(static d => d.Message.Contains("hunter2", StringComparison.Ordinal)), string.Join("\n", check.Diagnostics));
    }

    private static string ProjectFile => Path.GetFullPath(Path.Combine(Root, "pgproj.json"));

    /// <summary>The project's SQL files a build parses through its cache: four schema and template files and the project's three script files (a tenant's
    /// script is parsed with its variables substituted, not cached).</summary>
    private const int FileCount = 7;

    private static InMemoryFileSource Project()
    {
        var files = new InMemoryFileSource();
        void Put(string path, string text) => files.Set(Path.GetFullPath(Path.Combine(Root, path)), text);
        Put("pgproj.json", """
            {
              "name": "check", "version": "1", "source": "./sql", "searchPath": ["app", "public"],
              "predeploy": "./scripts/pre.sql", "postdeploy": "./scripts/vars.sql",
              "tenants": { "template": "./sql/Tenant", "prefix": "tenant", "postdeploy": "./scripts/tenant.sql" }
            }
            """);
        Put("sql/Schema/app.sql", "CREATE SCHEMA app;\nCREATE TABLE app.customer (id bigint PRIMARY KEY);\nCREATE TEXT SEARCH PARSER app.p (START = prsd_start, GETTOKEN = prsd_nexttoken, END = prsd_end, LEXTYPES = prsd_lextype);\n");
        Put("sql/Schema/broken.sql", "-- a syntax error\nCREATE TABLE app.broken (a int,, b int);\n");
        Put("sql/Tenant/orders.sql", "CREATE TABLE orders (id bigint PRIMARY KEY);\nCREATE TEXT SEARCH PARSER p (START = prsd_start, GETTOKEN = prsd_nexttoken, END = prsd_end, LEXTYPES = prsd_lextype);\n");
        Put("sql/Tenant/broken.sql", "CREATE TABLE lines (a int,, b int);\n");
        Put("scripts/pre.sql", "SELECT 1;\n:r ./seed.sql\nSELEC 2;\n");
        Put("scripts/seed.sql", "INSERT INTO app.customer VALUES (1,, 2);\n");
        Put("scripts/vars.sql", "SELECT :'tenant_id';\n");
        Put("scripts/tenant.sql", "INSERT INTO :\"tenant_schema\".orders VALUES (:'unknown');\nSELEC 3;\n");
        return files;
    }
}
