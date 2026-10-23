using System.Text.Json;
using PgProj.Core;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-311: with a <see cref="ParseCache"/> on the request, a build after an edit parses only the files whose text changed, and builds what a build without
/// the cache builds: the same model, diagnostics, and documents, also for what the steps after parsing do across files (an <c>ALTER TABLE</c> in another
/// file, unnamed constraints numbered across files, a tenant template). A changed <c>searchPath</c> parses every file again, and a file that is gone is
/// not held.
/// </summary>
[TestClass]
public sealed class ParseCacheTests
{
    [TestMethod]
    public async Task ASecondBuildParsesOnlyTheFileThatChanged()
    {
        // Arrange
        var fixture = new ProjectFixture();
        var files = fixture.InMemory();
        var cache = new ParseCache();
        await BuildAsync(files, fixture.ProjectFile, cache);
        var first = cache.Parses;

        // Act: nothing changed, then one file changed.
        await BuildAsync(files, fixture.ProjectFile, cache);
        var unchanged = cache.Parses - first;
        files.Set(Path.Combine(fixture.Root, "sql", "Schema", "orders.sql"), "CREATE TABLE app.orders (id bigint PRIMARY KEY, note text);\n");
        var result = await BuildAsync(files, fixture.ProjectFile, cache);

        // Assert: the two schema files and the two pre-deploy files were parsed once; then nothing; then the changed file alone.
        Assert.AreEqual(4, first);
        Assert.AreEqual(0, unchanged);
        Assert.AreEqual(first + 1, cache.Parses);
        Assert.AreEqual(Json(await BuildAsync(files, fixture.ProjectFile, null)), Json(result));
    }

    [TestMethod]
    public async Task ACachedBuildIsAColdBuild_AcrossFiles()
    {
        // Arrange: an ALTER TABLE in another file than its table, unnamed constraints that PostgreSQL numbers across files, a view on the altered table.
        var fixture = new ProjectFixture()
            .With("sql/Schema/a.sql", "CREATE TABLE app.a (id int PRIMARY KEY, x int CHECK (x > 0), UNIQUE (x));\n")
            .With("sql/Schema/b.sql", "ALTER TABLE app.a ADD COLUMN y int CHECK (y > 0);\nCREATE TABLE app.b (a_id int REFERENCES app.a (id), x int CHECK (x > 0));\n")
            .With("sql/Schema/v.sql", "CREATE VIEW app.v AS SELECT id, y FROM app.a;\n");
        var files = fixture.InMemory();
        var cache = new ParseCache();
        await BuildAsync(files, fixture.ProjectFile, cache);

        // Act: each file edited in turn, built with the cache and cold.
        foreach (var (path, text) in new[]
        {
            ("sql/Schema/b.sql", "ALTER TABLE app.a ADD COLUMN y int CHECK (y > 1);\nCREATE TABLE app.b (a_id int REFERENCES app.a (id), x int CHECK (x > 0), z int UNIQUE);\n"),
            ("sql/Schema/a.sql", "CREATE TABLE app.a (id int PRIMARY KEY, x int CHECK (x > 0), w text UNIQUE, UNIQUE (x));\n"),
            ("sql/Schema/v.sql", "CREATE VIEW app.v AS SELECT id, y, w FROM app.a;\n"),
        })
        {
            files.Set(Path.Combine(fixture.Root, path), text);
            var cached = await BuildAsync(files, fixture.ProjectFile, cache);
            var cold = await BuildAsync(files, fixture.ProjectFile, null);

            // Assert
            Assert.AreEqual(BuildStatus.Succeeded, cold.Status, string.Join("; ", cold.Diagnostics.Select(static d => d.Message)));
            Assert.AreEqual(Json(cold), Json(cached), path);
        }
    }

    [TestMethod]
    public async Task TheSamplesBuildTheSameTwiceFromTheCache()
    {
        // The two samples, one with tenants (its template parsed in its stand-in schema), built cold and then twice through one cache.
        foreach (var sample in new[] { "MultiSchema", "SchemaPerTenant" })
        {
            var projectFile = Path.Combine(RepositoryRoot(), "samples", sample, "pgproj.json");
            var cache = new ParseCache();
            var cold = Json(await BuildAsync(DiskFileSource.Instance, projectFile, null));

            Assert.AreEqual(cold, Json(await BuildAsync(DiskFileSource.Instance, projectFile, cache)), sample);
            var parses = cache.Parses;
            Assert.AreEqual(cold, Json(await BuildAsync(DiskFileSource.Instance, projectFile, cache)), sample);
            Assert.AreEqual(parses, cache.Parses, $"{sample}: an unchanged project is not parsed again");
        }
    }

    [TestMethod]
    public async Task AChangedSearchPathParsesEveryFileAgain()
    {
        // Arrange: the first schema of searchPath is where an unqualified name is created, so every tree depends on it.
        var fixture = new ProjectFixture().With("sql/Schema/plain.sql", "CREATE TABLE plain (id int);\n");
        var files = fixture.InMemory();
        var cache = new ParseCache();
        await BuildAsync(files, fixture.ProjectFile, cache);
        var before = cache.Parses;

        // Act
        files.Set(fixture.ProjectFile, """{ "name": "fixture", "version": "1", "source": "./sql", "predeploy": "./scripts/pre.sql", "excludeGrants": true, "searchPath": ["app", "public"] }""");
        var result = await BuildAsync(files, fixture.ProjectFile, cache);

        // Assert: every file parsed again, the old parses dropped, and the table created in app.
        Assert.AreEqual(before * 2, cache.Parses);
        Assert.AreEqual(before, cache.Count);
        Assert.IsTrue(result.Model!.Tables.ContainsKey("app.plain"));
        Assert.AreEqual(Json(await BuildAsync(files, fixture.ProjectFile, null)), Json(result));
    }

    [TestMethod]
    public async Task AFileThatIsGoneIsNotHeld()
    {
        var fixture = new ProjectFixture().With("sql/Schema/extra.sql", "CREATE TABLE app.extra (id int);\n");
        var files = fixture.InMemory();
        var cache = new ParseCache();
        await BuildAsync(files, fixture.ProjectFile, cache);
        var held = cache.Count;

        files.Remove(Path.Combine(fixture.Root, "sql", "Schema", "extra.sql"));
        await BuildAsync(files, fixture.ProjectFile, cache);

        Assert.AreEqual(held - 1, cache.Count);
        cache.Clear();
        Assert.AreEqual(0, cache.Count);
    }

    private static Task<BuildResult> BuildAsync(IProjectFileSource files, string projectFile, ParseCache? cache)
        => new PgProjEngine().BuildAsync(new BuildRequest { Files = files, ProjectFilePath = projectFile, ParseCache = cache });

    /// <summary>What a build gives: its model (as <c>--dump-model</c> writes it), its diagnostics, and its documents.</summary>
    private static string Json(BuildResult result)
        => JsonSerializer.Serialize(result.Model) + result.ToJson(static path => path);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pgproj.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (pgproj.slnx) was not found above " + AppContext.BaseDirectory);
    }
}
