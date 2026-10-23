namespace PgProj.Core.Tests;

/// <summary>
/// G-317: a <c>:r</c> path takes <c>\</c> or <c>/</c> between folders on every operating system, so a project written on Windows (<c>:r .\Seed\file.sql</c>, as SQL Server
/// Data Tools users write it) builds on Linux and macOS too. The files are held in a case-sensitive source, as on Linux, where <c>\</c> is otherwise an ordinary character.
/// </summary>
[TestClass]
public sealed class ScriptIncludeSeparatorTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "pgproj-g317");

    private static string At(params string[] parts) => Path.Combine([Root, .. parts]);

    [TestMethod]
    public void BackslashesForwardSlashesAndBothInOnePathAllFindTheFile()
    {
        // Arrange
        var source = new InMemoryFileSource(caseSensitive: true)
            .Set(At("Scripts", "post.sql"), ":r .\\Seed\\one.sql\n:r ./Seed/two.sql\n:r Seed\\Deep/three.sql\n:r \".\\Seed\\Deep\\four.sql\"\n")
            .Set(At("Scripts", "Seed", "one.sql"), "SELECT 1;\n")
            .Set(At("Scripts", "Seed", "two.sql"), "SELECT 2;\n")
            .Set(At("Scripts", "Seed", "Deep", "three.sql"), "SELECT 3;\n:r ..\\two.sql\n")
            .Set(At("Scripts", "Seed", "Deep", "four.sql"), "SELECT 4;\n");

        // Act
        var expanded = ScriptIncludeExpander.Expand(At("Scripts", "post.sql"), source);

        // Assert: each include is found, and the marker keeps the path as it was written.
        Assert.AreEqual(
            "-- :r .\\Seed\\one.sql\nSELECT 1;\n-- :r ./Seed/two.sql\nSELECT 2;\n-- :r Seed\\Deep/three.sql\nSELECT 3;\n-- :r ..\\two.sql\nSELECT 2;\n-- :r .\\Seed\\Deep\\four.sql\nSELECT 4;\n",
            expanded.Text);
        CollectionAssert.AreEqual(
            new[] { At("Scripts", "post.sql"), At("Scripts", "Seed", "Deep", "four.sql"), At("Scripts", "Seed", "Deep", "three.sql"), At("Scripts", "Seed", "one.sql"), At("Scripts", "Seed", "two.sql") }.Order(StringComparer.Ordinal).ToArray(),
            expanded.Files.Select(static file => file.FilePath).Distinct().Order(StringComparer.Ordinal).ToArray());
    }

    [TestMethod]
    public void EitherSeparatorResolvesToTheSameFullPath()
    {
        var expected = At("Scripts", "Seed", "a.sql");

        Assert.AreEqual(expected, ScriptIncludeExpander.ResolveInclude(At("Scripts"), "Seed\\a.sql"));
        Assert.AreEqual(expected, ScriptIncludeExpander.ResolveInclude(At("Scripts"), "Seed/a.sql"));
        Assert.AreEqual(expected, ScriptIncludeExpander.ResolveInclude(At("Scripts"), ".\\Other\\..\\Seed/a.sql"));
    }

    [TestMethod]
    public void AMissingIncludeIsNamedAsWrittenAndAsResolved()
    {
        // Arrange
        var source = new InMemoryFileSource(caseSensitive: true).Set(At("Scripts", "post.sql"), ":r .\\Seed\\none.sql\n");

        // Act
        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => ScriptIncludeExpander.Expand(At("Scripts", "post.sql"), source));

        // Assert
        StringAssert.Contains(exception.Message, "':r .\\Seed\\none.sql' names a file that does not exist: '" + At("Scripts", "Seed", "none.sql") + "'.");
    }

    [TestMethod]
    public void AProjectWhosePostDeployScriptIncludesWithBackslashesLoads()
    {
        // Arrange: the layout of samples/MultiSchema before its includes were written with /, kept here as the backslash fixture.
        var source = new InMemoryFileSource(caseSensitive: true)
            .Set(At("pgproj.json"), """{ "name": "p", "version": "1", "source": "./Schema", "postdeploy": "./Scripts/postdeploy.sql" }""")
            .Set(At("Schema", "t.sql"), "CREATE TABLE t (id int);\n")
            .Set(At("Scripts", "postdeploy.sql"), ":r .\\Seed\\PrincipalSeed.sql\n")
            .Set(At("Scripts", "Seed", "PrincipalSeed.sql"), "INSERT INTO t VALUES (1);\n");

        // Act
        var project = new PgProjectLoader().Load(source, At("pgproj.json"));

        // Assert
        StringAssert.Contains(project.PostDeployScriptContent, "INSERT INTO t VALUES (1);");
    }

    [TestMethod]
    public void CaseStillMattersOnACaseSensitiveSource()
    {
        // Arrange: the folder is seed, the include says Seed.
        var source = new InMemoryFileSource(caseSensitive: true)
            .Set(At("Scripts", "post.sql"), ":r .\\Seed\\one.sql\n")
            .Set(At("Scripts", "seed", "one.sql"), "SELECT 1;\n");

        // Act and assert
        Assert.ThrowsExactly<PgProjectValidationException>(() => ScriptIncludeExpander.Expand(At("Scripts", "post.sql"), source));
    }
}
