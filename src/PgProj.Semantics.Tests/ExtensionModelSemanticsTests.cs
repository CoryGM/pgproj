using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-213: an extension is keyed by its name alone; it carries the text written, its version, whether its schema is stated, the extensions declared
/// before it (the ones it is created after), and a comment only <c>COMMENT ON EXTENSION</c> states.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ExtensionModelSemanticsTests
{
    [TestMethod]
    public void AnExtensionCarriesWhatWasWritten()
    {
        var model = Build("CREATE EXTENSION cube; CREATE EXTENSION earthdistance SCHEMA geo VERSION '1.1' CASCADE; COMMENT ON EXTENSION cube IS 'cubes';");

        var cube = model.Types["extension:cube"];
        Assert.IsNull(cube.SchemaName);
        Assert.IsFalse(cube.ExtensionSchemaStated);
        Assert.IsNull(cube.ExtensionVersion);
        Assert.IsEmpty(cube.ExtensionRequires);
        Assert.AreEqual("cubes", cube.Comment);
        Assert.IsTrue(cube.CommentStated);

        var earthdistance = model.Types["extension:earthdistance"];
        Assert.AreEqual("geo", earthdistance.SchemaName);
        Assert.IsTrue(earthdistance.ExtensionSchemaStated);
        Assert.AreEqual("1.1", earthdistance.ExtensionVersion);
        Assert.AreEqual("CREATE EXTENSION earthdistance SCHEMA geo VERSION '1.1' CASCADE", earthdistance.ExtensionWrittenText);
        CollectionAssert.AreEqual(new[] { "extension:cube" }, earthdistance.ExtensionRequires.ToArray());
        Assert.IsFalse(earthdistance.CommentStated);
        Assert.IsFalse(model.HasErrors, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void CommentIsNullStatesNoComment()
    {
        var citext = Build("CREATE EXTENSION citext; COMMENT ON EXTENSION citext IS NULL;").Types["extension:citext"];

        Assert.IsNull(citext.Comment);
        Assert.IsTrue(citext.CommentStated);
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
