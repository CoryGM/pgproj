using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-213: a live extension is written after those it requires, with its version and a comment only when it is not the one it comes with; the model takes
/// the database's own requirements and comment; and the scaffold writes no version and declares a requiring extension after its requirements.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveExtensionTests
{
    private static readonly LiveTypeSnapshot Cube = new("geo", "cube", "extension", "CREATE EXTENSION cube SCHEMA geo", "data type for multidimensional cubes")
    {
        ExtensionVersion = "1.5",
        DefaultComment = "data type for multidimensional cubes",
    };

    private static readonly LiveTypeSnapshot Earthdistance = new("geo", "earthdistance", "extension", "CREATE EXTENSION earthdistance SCHEMA geo", "ours")
    {
        ExtensionVersion = "1.2",
        DefaultComment = "calculate great-circle distances on the surface of the Earth",
        Requires = ["cube"],
    };

    // A made-up extension that sorts before cube and requires it.
    private static readonly LiveTypeSnapshot Aardvark = new("public", "aardvark", "extension", "CREATE EXTENSION aardvark SCHEMA public", null)
    {
        ExtensionVersion = "1.0",
        DefaultComment = "its own",
        Requires = ["cube"],
    };

    [TestMethod]
    public void ExtensionsAreOrderedAfterThoseTheyRequire()
    {
        var ordered = LiveTypeSnapshot.OrderExtensions([Earthdistance, Aardvark, Cube]).Select(static extension => extension.TypeName).ToArray();

        CollectionAssert.AreEqual(new[] { "cube", "aardvark", "earthdistance" }, ordered);
    }

    [TestMethod]
    public void TheSnapshotStatesTheVersionAndOnlyAChangedComment()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("geo")], [], [], [], types: [Earthdistance, Aardvark, Cube]);

        StringAssert.Contains(sql, "CREATE EXTENSION cube SCHEMA geo VERSION '1.5';");
        StringAssert.Contains(sql, "CREATE EXTENSION earthdistance SCHEMA geo VERSION '1.2';");
        StringAssert.Contains(sql, "COMMENT ON EXTENSION earthdistance IS 'ours';");
        StringAssert.Contains(sql, "COMMENT ON EXTENSION aardvark IS NULL;");
        Assert.IsFalse(sql.Contains("COMMENT ON EXTENSION cube", StringComparison.Ordinal), sql);
        Assert.IsTrue(sql.IndexOf("CREATE EXTENSION cube", StringComparison.Ordinal) < sql.IndexOf("CREATE EXTENSION aardvark", StringComparison.Ordinal), sql);
    }

    [TestMethod]
    public void TheModelTakesTheDatabasesRequirementsAndComment()
    {
        IReadOnlyList<LiveTypeSnapshot> snapshots = [Earthdistance, Aardvark, Cube];
        var model = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder())
            .BuildModelFromSnapshotSql(LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("geo")], [], [], [], types: snapshots));
        var types = LiveDatabaseModelLoader.WithLiveExtensionFacts(model.Types, snapshots);

        // Read in the order cube, aardvark, earthdistance, but earthdistance requires only cube.
        CollectionAssert.AreEqual(new[] { "extension:cube" }, types["extension:earthdistance"].ExtensionRequires.ToArray());
        Assert.AreEqual("1.2", types["extension:earthdistance"].ExtensionVersion);
        Assert.AreEqual("data type for multidimensional cubes", types["extension:cube"].Comment);
        Assert.IsFalse(types["extension:cube"].CommentStated);
        Assert.AreEqual("ours", types["extension:earthdistance"].Comment);
        Assert.IsTrue(types["extension:earthdistance"].CommentStated);
        Assert.IsNull(types["extension:aardvark"].Comment);
        Assert.IsTrue(types["extension:aardvark"].CommentStated);
    }

    [TestMethod]
    public void TheScaffoldDeclaresARequiringExtensionAfterItsRequirements()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("geo")], [], [], [], [], [], [Earthdistance, Aardvark, Cube], [], [], [], [])
            .ToDictionary(static file => file.RelativePath, static file => file.Content);

        // aardvark sorts before cube, so in a file of its own it would be declared before what it requires.
        Assert.IsFalse(files.ContainsKey("Extension/aardvark.sql"), string.Join(", ", files.Keys));
        Assert.IsFalse(files.ContainsKey("Extension/earthdistance.sql"), string.Join(", ", files.Keys));
        var cube = files["Extension/cube.sql"];
        Assert.IsTrue(cube.IndexOf("CREATE EXTENSION cube SCHEMA geo;", StringComparison.Ordinal) < cube.IndexOf("CREATE EXTENSION aardvark SCHEMA public;", StringComparison.Ordinal), cube);
        StringAssert.Contains(cube, "COMMENT ON EXTENSION earthdistance IS 'ours';");
        StringAssert.Contains(cube, "COMMENT ON EXTENSION aardvark IS NULL;");
        Assert.IsFalse(cube.Contains("VERSION", StringComparison.Ordinal), cube);
        Assert.IsFalse(cube.Contains("COMMENT ON EXTENSION cube", StringComparison.Ordinal), cube);
    }
}
