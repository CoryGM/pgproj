namespace PgProj.Core.Tests;

/// <summary>G-93: <c>searchPath</c> is an optional <c>pgproj.json</c> list; without it the path is <c>public</c> and <c>publish</c> does not pin one.</summary>
[TestClass]
public sealed class PgProjectLoaderSearchPathTests
{
    [TestMethod]
    public void WithoutSearchPathThePathIsPublicAndNotConfigured()
    {
        var project = Load(string.Empty);

        CollectionAssert.AreEqual(new[] { "public" }, project.SearchPath.ToArray());
        Assert.IsFalse(project.HasConfiguredSearchPath);
        Assert.AreEqual("public", project.DefaultSchema);
    }

    [TestMethod]
    public void SearchPathIsReadInOrderAndItsFirstSchemaIsTheDefault()
    {
        var project = Load(""", "searchPath": ["shop", "public"]""");

        CollectionAssert.AreEqual(new[] { "shop", "public" }, project.SearchPath.ToArray());
        Assert.IsTrue(project.HasConfiguredSearchPath);
        Assert.AreEqual("shop", project.DefaultSchema);
    }

    [TestMethod]
    [DataRow(""", "searchPath": []""", "empty 'searchPath'")]
    [DataRow(""", "searchPath": ["shop", ""]""", "empty entry")]
    [DataRow(""", "searchPath": ["$user", "public"]""", "'$user'")]
    [DataRow(""", "searchPath": ["shop", "shop"]""", "twice")]
    public void AnInvalidSearchPathIsRefused(string property, string expected)
    {
        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => Load(property));

        StringAssert.Contains(exception.Message, expected);
    }

    private static PgProject Load(string extraProperty)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pgproj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "sql"));
        try
        {
            var projectPath = Path.Combine(directory, "pgproj.json");
            File.WriteAllText(projectPath, $$"""{ "name": "test", "version": "1.0.0", "source": "sql"{{extraProperty}} }""");
            return new PgProjectLoader().Load(projectPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
