using PgProj.Core;

namespace PgProj.Engine.Tests;

/// <summary>G-309a: the three sources of a project's files, and the loader reading through them.</summary>
[TestClass]
public sealed class FileSourceTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "pgproj-source-tests");

    [TestMethod]
    public void AnInMemorySourceFindsFilesByPatternAndFolder()
    {
        var source = new InMemoryFileSource()
            .Set(Path.Combine(Root, "a.sql"), "A")
            .Set(Path.Combine(Root, "sub", "b.sql"), "B", version: 7)
            .Set(Path.Combine(Root, "sub", "deeper", "c.txt"), "C")
            .AddDirectory(Path.Combine(Root, "empty"));

        CollectionAssert.AreEquivalent(
            new[] { Path.Combine(Root, "a.sql"), Path.Combine(Root, "sub", "b.sql") },
            source.EnumerateFiles(Root, "*.sql", recursive: true).ToArray());
        CollectionAssert.AreEquivalent(new[] { Path.Combine(Root, "a.sql") }, source.EnumerateFiles(Root, "*.sql", recursive: false).ToArray());
        Assert.IsTrue(source.DirectoryExists(Path.Combine(Root, "sub", "deeper")));
        Assert.IsTrue(source.DirectoryExists(Path.Combine(Root, "empty")));
        Assert.IsFalse(source.DirectoryExists(Path.Combine(Root, "missing")));
        Assert.AreEqual("B", source.ReadAllText(Path.Combine(Root, "sub", "b.sql")));
        Assert.AreEqual(7, source.Version(Path.Combine(Root, "sub", "b.sql")));
        Assert.IsNull(source.Version(Path.Combine(Root, "a.sql")));
        Assert.Throws<FileNotFoundException>(() => source.ReadAllText(Path.Combine(Root, "nothing.sql")));
        Assert.Throws<ArgumentException>(() => source.Set("relative.sql", "x"));
    }

    [TestMethod]
    public void ACaseInsensitiveSourceTreatsPathsThatDifferOnlyInCaseAsOneFile()
    {
        var insensitive = new InMemoryFileSource(caseSensitive: false).Set(Path.Combine(Root, "Sales.sql"), "S");
        var sensitive = new InMemoryFileSource(caseSensitive: true).Set(Path.Combine(Root, "Sales.sql"), "S");

        Assert.IsTrue(insensitive.FileExists(Path.Combine(Root, "SALES.SQL")));
        Assert.IsFalse(sensitive.FileExists(Path.Combine(Root, "SALES.SQL")));
        Assert.IsFalse(insensitive.IsCaseSensitive(Root));
        Assert.IsTrue(sensitive.IsCaseSensitive(Root));
        Assert.HasCount(1, insensitive.EnumerateFiles(Root, "*.SQL", recursive: true).ToArray());
        Assert.IsEmpty(sensitive.EnumerateFiles(Root, "*.SQL", recursive: true).ToArray());
    }

    [TestMethod]
    public void AnOverlayDocumentReplacesTheFileUnderItAndANewOneIsFound()
    {
        var disk = new InMemoryFileSource()
            .Set(Path.Combine(Root, "a.sql"), "on disk", version: 1)
            .Set(Path.Combine(Root, "b.sql"), "also on disk");
        var overlay = new OverlayFileSource(disk)
            .Set(Path.Combine(Root, "a.sql"), "in the editor", version: 9)
            .Set(Path.Combine(Root, "new.sql"), "not saved yet", version: 2);

        Assert.AreEqual("in the editor", overlay.ReadAllText(Path.Combine(Root, "a.sql")));
        Assert.AreEqual(9, overlay.Version(Path.Combine(Root, "a.sql")));
        Assert.AreEqual("also on disk", overlay.ReadAllText(Path.Combine(Root, "b.sql")));
        Assert.IsTrue(overlay.FileExists(Path.Combine(Root, "new.sql")));
        Assert.IsFalse(disk.FileExists(Path.Combine(Root, "new.sql")));
        CollectionAssert.AreEquivalent(
            new[] { Path.Combine(Root, "a.sql"), Path.Combine(Root, "b.sql"), Path.Combine(Root, "new.sql") },
            overlay.EnumerateFiles(Root, "*.sql", recursive: false).ToArray());

        Assert.IsTrue(overlay.Remove(Path.Combine(Root, "a.sql")));
        Assert.AreEqual("on disk", overlay.ReadAllText(Path.Combine(Root, "a.sql")));
        Assert.AreEqual(1, overlay.Version(Path.Combine(Root, "a.sql")));
    }

    [TestMethod]
    public void TheLoaderReadsAProjectFromMemoryAsItReadsItFromDisk()
    {
        var fixture = new ProjectFixture();
        try
        {
            fixture.WriteToDisk();

            var fromDisk = new PgProjectLoader().Load(fixture.ProjectFile);
            var fromMemory = new PgProjectLoader().Load(fixture.InMemory(), fixture.ProjectFile);

            Assert.AreEqual(fromDisk.Name, fromMemory.Name);
            Assert.AreEqual(fromDisk.SourcePath, fromMemory.SourcePath);
            CollectionAssert.AreEqual(fromDisk.SqlFiles.ToArray(), fromMemory.SqlFiles.ToArray());
            Assert.AreEqual(fromDisk.ExcludeGrants, fromMemory.ExcludeGrants);
            Assert.AreEqual(fromDisk.PreDeployScriptContent, fromMemory.PreDeployScriptContent);
            CollectionAssert.AreEqual(
                fromDisk.PreDeployScriptFiles.Select(static file => file.FilePath).ToArray(),
                fromMemory.PreDeployScriptFiles.Select(static file => file.FilePath).ToArray());
            StringAssert.Contains(fromMemory.PreDeployScriptContent!, "SELECT 2;");
            Assert.DoesNotContain("ignored", string.Join(';', fromMemory.SqlFiles), "the Scripts folder is not schema");
        }
        finally
        {
            fixture.Delete();
        }
    }

    [TestMethod]
    public void IncludesAndATenantFileAreReadFromMemory()
    {
        var fixture = new ProjectFixture()
            .With("pgproj.json", """
                {
                  "name": "t", "version": "1", "source": "./sql",
                  "tenants": { "template": "./sql/Tenant", "prefix": "tenant", "file": "./tenants.csv" }
                }
                """)
            .With(Path.Combine("sql", "Tenant", "t.sql"), "CREATE TABLE widget (id int);\n")
            .With("tenants.csv", "tenant\nacme\nglobex\n");

        var project = new PgProjectLoader().Load(fixture.InMemory(), fixture.ProjectFile);

        Assert.IsNotNull(project.Tenants);
        CollectionAssert.AreEqual(new[] { "acme", "globex" }, project.Tenants.FileTenants.Select(static tenant => tenant.Identifier).ToArray());
        Assert.HasCount(1, project.Tenants.TemplateFiles);
        Assert.DoesNotContain("Tenant", string.Join(';', project.SqlFiles), "the template is not part of the shared project");
    }

    [TestMethod]
    public void TheLoaderRefusesWhatItRefusesOnDiskWithTheSameMessageFromMemory()
    {
        foreach (var (name, configure) in InvalidProjects())
        {
            var fixture = configure(new ProjectFixture());
            try
            {
                fixture.WriteToDisk();
                var onDisk = Assert.Throws<PgProjectValidationException>(() => new PgProjectLoader().Load(fixture.ProjectFile), name);
                var inMemory = Assert.Throws<PgProjectValidationException>(() => new PgProjectLoader().Load(fixture.InMemory(), fixture.ProjectFile), name);

                Assert.AreEqual(onDisk.Message, inMemory.Message, name);
            }
            finally
            {
                fixture.Delete();
            }
        }
    }

    [TestMethod]
    public void ASourceWithoutTheProjectFileIsAFileNotFound()
    {
        var path = Path.Combine(Root, "pgproj.json");

        var exception = Assert.Throws<FileNotFoundException>(() => new PgProjectLoader().Load(new InMemoryFileSource(), path));

        StringAssert.Contains(exception.Message, "was not found");
    }

    [TestMethod]
    public void SettingsWithoutAFileAreValidatedAsTheFileIs()
    {
        var fixture = new ProjectFixture();
        var settings = new ProjectSettings { Name = "s", Version = "1", Source = "./sql", ExcludeOwners = true };

        var project = new PgProjectLoader().Load(fixture.InMemory(), settings, fixture.Root);

        Assert.AreEqual("s", project.Name);
        Assert.IsTrue(project.ExcludeOwners);
        Assert.HasCount(2, project.SqlFiles);

        var invalid = Assert.Throws<PgProjectValidationException>(() => new PgProjectLoader().Load(fixture.InMemory(), settings with { Name = " " }, fixture.Root));
        StringAssert.Contains(invalid.Message, "is missing required property 'name'");
    }

    private static IEnumerable<(string Name, Func<ProjectFixture, ProjectFixture> Configure)> InvalidProjects()
    {
        yield return ("invalid json", f => f.With("pgproj.json", "{ not json"));
        yield return ("missing name", f => f.With("pgproj.json", """{ "version": "1", "source": "./sql" }"""));
        yield return ("missing source folder", f => f.With("pgproj.json", """{ "name": "n", "version": "1", "source": "./nowhere" }"""));
        yield return ("missing predeploy", f => f.With("pgproj.json", """{ "name": "n", "version": "1", "source": "./sql", "predeploy": "./scripts/none.sql" }"""));
        yield return ("include of a missing file", f => f.With(Path.Combine("scripts", "pre.sql"), ":r ./gone.sql\n"));
        yield return ("removed connectionString", f => f.With("pgproj.json", """{ "name": "n", "version": "1", "source": "./sql", "connectionString": "Host=x" }"""));
        yield return ("tenant file without a tenant column", f => f
            .With("pgproj.json", """{ "name": "n", "version": "1", "source": "./sql", "tenants": { "template": "./sql/Tenant", "prefix": "tenant", "file": "./t.csv" } }""")
            .With(Path.Combine("sql", "Tenant", "t.sql"), "SELECT 1;")
            .With("t.csv", "name\nx\n"));
    }
}
