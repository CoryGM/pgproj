namespace PgProj.Core.Tests;

/// <summary>
/// G-290e: each file of a deploy script may say when it runs, with <c>-- pgproj: run when=...</c> among the comment lines at its top. The expanded script
/// keeps its files as a tree; rendered with every file running it is the script exactly, and a skipped file (with what it includes) is a comment saying
/// so. A tenant's scripts are <c>tenants.predeploy</c> and <c>tenants.postdeploy</c>; which markers each script takes is checked when the project loads.
/// </summary>
[TestClass]
public sealed class ScriptRunWhenTests
{
    [TestMethod]
    public void EveryFileRunningRendersTheScriptExactly()
    {
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", "SELECT 1;\r\n:r seed.sql\r\nSELECT 3;\r\n");
        directory.Write("seed.sql", "-- pgproj: run when=tenant-created\nSELECT 2;\n:r deeper.sql\n");
        directory.Write("deeper.sql", "SELECT 'deep';\n");

        var expanded = ScriptIncludeExpander.Expand(script);

        Assert.AreEqual(expanded.Text, expanded.Root!.Render(static _ => true));
        CollectionAssert.AreEqual(new string?[] { null, "tenant-created", null }, expanded.Parts.Select(static part => part.RunWhen).ToArray());
        Assert.AreEqual(1, expanded.Parts.ElementAt(1).RunWhenLine);
    }

    [TestMethod]
    public void ASkippedFileIsACommentAndTakesWhatItIncludesWithIt()
    {
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", "SELECT 1;\n:r seed.sql\nSELECT 3;\n");
        directory.Write("seed.sql", "-- Seeds a new tenant.\n\n-- pgproj: run when=tenant-created\nSELECT 2;\n:r deeper.sql\n");
        directory.Write("deeper.sql", "SELECT 'deep';\n");

        var rendered = ScriptIncludeExpander.Expand(script).Root!.Render(static part => part.RunWhen is null);

        Assert.AreEqual("SELECT 1;\n-- :r seed.sql\n-- pgproj: skipped seed.sql (it runs when=tenant-created)\nSELECT 3;\n", rendered);
    }

    [TestMethod]
    [DataRow("SELECT 1;\n-- pgproj: run when=tenant-created\n")]
    [DataRow("-- pgproj: run when tenant-created\nSELECT 1;\n")]
    public void AMarkerOnlyCountsAtTheTopAndInItsOwnForm(string content)
    {
        using var directory = TempDirectory.Create();

        Assert.IsNull(ScriptIncludeExpander.Expand(directory.Write("post.sql", content)).Root!.RunWhen);
    }

    [TestMethod]
    public void AMarkerIsReadWhateverItsCaseAndSpacing()
    {
        using var directory = TempDirectory.Create();

        Assert.AreEqual("Tenant-Changed", ScriptIncludeExpander.Expand(directory.Write("post.sql", "--   PGPROJ:  run  when = Tenant-Changed  \nSELECT 1;\n")).Root!.RunWhen);
    }

    [TestMethod]
    public void TwoMarkersInAFileAreAnError()
    {
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", "-- pgproj: run when=tenant-created\n-- pgproj: run when=tenant-changed\nSELECT 1;\n");

        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => ScriptIncludeExpander.Expand(script));

        StringAssert.Contains(exception.Message, "post.sql:2: a second '-- pgproj: run when=' marker (the first is on line 1)");
    }

    [TestMethod]
    public void ATenantsScriptsAreLoadedAndAreNotSchema()
    {
        using var directory = TempDirectory.Create();
        directory.Write("sql/shared.sql", "CREATE SCHEMA app;");
        directory.Write("sql/Tenant/t.sql", "CREATE TABLE t (id int);");
        directory.Write("sql/Tenant/seed.sql", "-- pgproj: run when=tenant-created\nINSERT INTO t VALUES (1);");
        directory.Write("sql/tenant-post.sql", ":r Tenant/seed.sql\nSELECT 1;");
        var project = Load(directory, """, "tenants": { "template": "sql/Tenant", "prefix": "tenant", "postdeploy": "sql/tenant-post.sql" }""");

        Assert.IsNull(project.Tenants!.PreDeploy);
        Assert.AreEqual(2, project.Tenants.PostDeploy!.Files.Count);
        CollectionAssert.AreEqual(new[] { System.IO.Path.Combine(directory.Path, "sql", "shared.sql") }, project.SqlFiles.ToArray(), "a tenant's scripts are not shared schema");
        CollectionAssert.AreEqual(new[] { System.IO.Path.Combine(directory.Path, "sql", "Tenant", "t.sql") }, project.Tenants.TemplateFiles.ToArray(), "nor template");
    }

    [TestMethod]
    [DataRow(""", "postdeploy": "post.sql" """, "-- pgproj: run when=tenant-created", "is not a marker the project's postdeploy script takes; it takes when=changed")]
    [DataRow(""", "tenants": { "template": "sql/Tenant", "prefix": "tenant", "predeploy": "post.sql" }""", "-- pgproj: run when=changed", "is not a marker a tenant's predeploy script takes; it takes when=tenant-changed or when=tenant-created")]
    [DataRow(""", "tenants": { "template": "sql/Tenant", "prefix": "tenant", "postdeploy": "post.sql" }""", "-- pgproj: run when=sometimes", "post.sql:1: '-- pgproj: run when=sometimes' is not a marker")]
    public void AMarkerAScriptDoesNotTakeIsRefused(string setting, string marker, string expected)
    {
        using var directory = TempDirectory.Create();
        directory.Write("sql/Tenant/t.sql", "CREATE TABLE t (id int);");
        directory.Write("post.sql", marker + "\nSELECT 1;\n");

        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => Load(directory, setting));

        StringAssert.Contains(exception.Message, expected);
    }

    [TestMethod]
    public void AProjectsScriptMayRunOnlyWhenChanged()
    {
        using var directory = TempDirectory.Create();
        directory.Write("sql/t.sql", "CREATE TABLE t (id int);");
        directory.Write("post.sql", "-- pgproj: run when=changed\nSELECT 1;\n");

        Assert.AreEqual("changed", Load(directory, """, "postdeploy": "post.sql" """).PostDeployScriptParts!.RunWhen);
    }

    private static PgProject Load(TempDirectory directory, string extra)
    {
        var projectFile = directory.Write("pgproj.json", $$"""{ "name": "test", "version": "1.0", "source": "sql"{{extra}} }""");
        return new PgProjectLoader().Load(projectFile);
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var directory = new TempDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pgproj-g290e-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(directory.Path);
            return directory;
        }

        public string Write(string relativePath, string content)
        {
            var filePath = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, content);
            return filePath;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
