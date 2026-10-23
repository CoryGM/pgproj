namespace PgProj.Core.Tests;

/// <summary>
/// G-124: SSDT-style <c>:r &lt;path&gt;</c> includes in a pre- or post-deployment script. A line starting with <c>:r</c>, outside strings,
/// comments, and dollar-quoted bodies, is replaced by a marker comment and the named file's contents, relative to the file that
/// contains it.
/// </summary>
[TestClass]
public sealed class ScriptIncludeExpanderTests
{
    [TestMethod]
    public void AnIncludeIsReplacedByAMarkerAndTheFilesContents()
    {
        // Arrange
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", "SELECT 1;\n:r ./seed/one.sql\nSELECT 3;\n");
        directory.Write("seed/one.sql", "SELECT 2;\n");

        // Act
        var expanded = ScriptIncludeExpander.Expand(script);

        // Assert
        Assert.AreEqual("SELECT 1;\n-- :r ./seed/one.sql\nSELECT 2;\nSELECT 3;\n", expanded.Text);
    }

    [TestMethod]
    public void APathIsRelativeToTheFileThatContainsTheInclude()
    {
        // Arrange: seed/one.sql includes "./two.sql", which is seed/two.sql, not a file next to the script.
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", ":r seed/one.sql\n");
        directory.Write("seed/one.sql", "SELECT 1;\n:r ./two.sql\n");
        directory.Write("seed/two.sql", "SELECT 2;\n");
        directory.Write("two.sql", "SELECT 'wrong';\n");

        // Act
        var expanded = ScriptIncludeExpander.Expand(script);

        // Assert
        StringAssert.Contains(expanded.Text, "SELECT 2;");
        Assert.IsFalse(expanded.Text.Contains("wrong", StringComparison.Ordinal));
        Assert.AreEqual(3, expanded.Files.Count);
    }

    [TestMethod]
    public void EachFilesOwnTextKeepsItsLinesWithTheIncludeAsAComment()
    {
        // Arrange
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", "SELECT 1;\n  :r one.sql\nSELECT 3;\n");
        directory.Write("one.sql", "SELECT 2;\n");

        // Act
        var files = ScriptIncludeExpander.Expand(script).Files;

        // Assert: the script is first, its :r line is one comment line, and the included file is its own text.
        Assert.AreEqual(Path.GetFullPath(script), files[0].FilePath);
        Assert.AreEqual("SELECT 1;\n-- :r one.sql\nSELECT 3;\n", files[0].Text);
        Assert.AreEqual("SELECT 2;\n", files[1].Text);
    }

    [TestMethod]
    public void AQuotedPathAndCrLfLinesAreRead()
    {
        // Arrange
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", "SELECT 1;\r\n:r \"my seed.sql\"\r\nSELECT 3;\r\n");
        directory.Write("my seed.sql", "SELECT 2;\r\n");

        // Act
        var expanded = ScriptIncludeExpander.Expand(script);

        // Assert
        Assert.AreEqual("SELECT 1;\r\n-- :r my seed.sql\nSELECT 2;\r\nSELECT 3;\r\n", expanded.Text);
    }

    [TestMethod]
    public void AFileIncludedTwiceIsExpandedTwiceAndListedOnce()
    {
        // Arrange
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", ":r a.sql\n:r a.sql\n");
        directory.Write("a.sql", "SELECT 1;\n");

        // Act
        var expanded = ScriptIncludeExpander.Expand(script);

        // Assert
        Assert.AreEqual(2, expanded.Text.Split("SELECT 1;").Length - 1);
        Assert.AreEqual(2, expanded.Files.Count);
    }

    [TestMethod]
    [DataRow("SELECT 'a\n:r nothere.sql\nb';\n", DisplayName = "in a string")]
    [DataRow("SELECT E'a\\'\n:r nothere.sql\n';\n", DisplayName = "in an escape string after an escaped quote")]
    [DataRow("SELECT \"a\n:r nothere.sql\nb\";\n", DisplayName = "in a quoted identifier")]
    [DataRow("-- note\n-- :r nothere.sql\nSELECT 1;\n", DisplayName = "in a line comment")]
    [DataRow("/* a\n:r nothere.sql\n/* nested\n:r nothere.sql\n*/\n:r nothere.sql\n*/\nSELECT 1;\n", DisplayName = "in a nested block comment")]
    [DataRow("DO $$\nBEGIN\n:r nothere.sql\nEND $$;\n", DisplayName = "in a dollar-quoted body")]
    [DataRow("DO $body$\n$$\n:r nothere.sql\n$$\n$body$;\n", DisplayName = "in a tagged dollar-quoted body holding another")]
    [DataRow("SELECT 1 :r nothere.sql\n", DisplayName = "not at the start of a line")]
    [DataRow("SELECT 1;\n:read nothere.sql\n:rx\n", DisplayName = "another command that starts with r")]
    public void AnIncludeThatIsNotAtTheStartOfALineOutsideQuotingIsLeftAlone(string sql)
    {
        // Arrange
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", sql);

        // Act
        var expanded = ScriptIncludeExpander.Expand(script);

        // Assert: nothing is looked up, and the text is as written.
        Assert.AreEqual(sql, expanded.Text);
        Assert.AreEqual(1, expanded.Files.Count);
    }

    [TestMethod]
    public void AnIncludeAfterAStringThatEndedOnTheLineBeforeIsFound()
    {
        // Arrange
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", "SELECT 'a\nb';\n:r one.sql\n$$ x $$;\n:r one.sql\n");
        directory.Write("one.sql", "SELECT 2;\n");

        // Act
        var expanded = ScriptIncludeExpander.Expand(script);

        // Assert
        Assert.AreEqual(2, expanded.Text.Split("SELECT 2;").Length - 1);
    }

    [TestMethod]
    public void AMissingFileNamesTheScriptTheLineAndTheFile()
    {
        // Arrange
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", "SELECT 1;\n\n:r missing.sql\n");

        // Act
        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => ScriptIncludeExpander.Expand(script));

        // Assert
        StringAssert.Contains(exception.Message, $"{Path.GetFullPath(script)}:3");
        StringAssert.Contains(exception.Message, ":r missing.sql");
        StringAssert.Contains(exception.Message, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(script))!, "missing.sql"));
    }

    [TestMethod]
    public void AnIncludeCycleNamesTheChain()
    {
        // Arrange
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", ":r a.sql\n");
        directory.Write("a.sql", "SELECT 1;\n:r b.sql\n");
        directory.Write("b.sql", ":r a.sql\n");

        // Act
        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => ScriptIncludeExpander.Expand(script));

        // Assert
        var root = Path.GetDirectoryName(Path.GetFullPath(script))!;
        StringAssert.Contains(exception.Message, $"{Path.Combine(root, "post.sql")} -> {Path.Combine(root, "a.sql")} -> {Path.Combine(root, "b.sql")} -> {Path.Combine(root, "a.sql")}");
        StringAssert.Contains(exception.Message, $"{Path.Combine(root, "b.sql")}:1");
    }

    [TestMethod]
    public void AScriptThatIncludesItselfIsACycle()
    {
        // Arrange
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", ":r post.sql\n");

        // Act / Assert
        Assert.ThrowsExactly<PgProjectValidationException>(() => ScriptIncludeExpander.Expand(script));
    }

    [TestMethod]
    public void AnIncludeWithoutAPathIsRefused()
    {
        // Arrange
        using var directory = TempDirectory.Create();
        var script = directory.Write("post.sql", ":r \n");

        // Act
        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => ScriptIncludeExpander.Expand(script));

        // Assert
        StringAssert.Contains(exception.Message, "needs the path");
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var directory = new TempDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pgproj-g124-{Guid.NewGuid():N}"));
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
