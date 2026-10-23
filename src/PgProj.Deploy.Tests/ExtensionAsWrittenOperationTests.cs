using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-213: an extension is created as written, in declaration order, with its stated comment; a stated schema, version, or comment that differs is altered
/// in place; a required extension is dropped after the one that requires it; and no system schema is ever created.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ExtensionAsWrittenOperationTests
{
    private const string Live = """
        CREATE EXTENSION citext SCHEMA public VERSION '1.6';
        COMMENT ON EXTENSION citext IS 'data type for case-insensitive character strings';
        """;

    [TestMethod]
    public void CascadeVersionAndIfNotExistsAreDeployedAsWritten()
    {
        var script = ScriptFor(DatabaseModel.Empty, Build("create extension if not exists earthdistance schema geo version '1.1' cascade;"));

        StringAssert.Contains(script, "create extension if not exists earthdistance schema geo version '1.1' cascade;", script);
    }

    [TestMethod]
    public void ExtensionsAreCreatedInDeclarationOrder()
    {
        var script = ScriptFor(DatabaseModel.Empty, Build("CREATE EXTENSION plperl; CREATE EXTENSION hstore; CREATE EXTENSION hstore_plperl;"));

        var plperl = script.IndexOf("CREATE EXTENSION plperl;", StringComparison.Ordinal);
        var hstore = script.IndexOf("CREATE EXTENSION hstore;", StringComparison.Ordinal);
        var both = script.IndexOf("CREATE EXTENSION hstore_plperl;", StringComparison.Ordinal);
        Assert.IsTrue(plperl >= 0 && plperl < hstore && hstore < both, script);
    }

    [TestMethod]
    [DataRow("CREATE EXTENSION citext; COMMENT ON EXTENSION citext IS 'ours';", "COMMENT ON EXTENSION \"citext\" IS 'ours';")]
    [DataRow("CREATE EXTENSION citext; COMMENT ON EXTENSION citext IS NULL;", "COMMENT ON EXTENSION \"citext\" IS NULL;")]
    public void AStatedCommentIsSetWhenTheExtensionIsCreated(string project, string expected)
    {
        var script = ScriptFor(DatabaseModel.Empty, Build(project));

        Assert.IsTrue(script.IndexOf(expected, StringComparison.Ordinal) > script.IndexOf("CREATE EXTENSION citext;", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AnUnstatedCommentIsLeftAlone()
    {
        var script = ScriptFor(DatabaseModel.Empty, Build("CREATE EXTENSION citext;"));

        Assert.IsFalse(script.Contains("COMMENT ON EXTENSION", StringComparison.Ordinal), script);
    }

    [TestMethod]
    [DataRow("CREATE EXTENSION citext SCHEMA app;", "ALTER EXTENSION \"citext\" SET SCHEMA \"app\";")]
    [DataRow("CREATE EXTENSION citext VERSION '1.8';", "ALTER EXTENSION \"citext\" UPDATE TO '1.8';")]
    [DataRow("CREATE EXTENSION citext; COMMENT ON EXTENSION citext IS 'ours';", "COMMENT ON EXTENSION \"citext\" IS 'ours';")]
    [DataRow("CREATE EXTENSION citext; COMMENT ON EXTENSION citext IS NULL;", "COMMENT ON EXTENSION \"citext\" IS NULL;")]
    public void AStatedDifferenceIsAlteredInPlace(string project, string expected)
    {
        var script = ScriptFor(Build(Live), Build(project));

        StringAssert.Contains(script, expected, script);
        Assert.IsFalse(script.Contains("DROP EXTENSION", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("CREATE EXTENSION", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void TheSchemaAnExtensionMovesToIsCreatedFirst()
    {
        var script = ScriptFor(Build(Live), Build("CREATE SCHEMA app; CREATE EXTENSION citext SCHEMA app;"));

        var create = script.IndexOf("CREATE SCHEMA IF NOT EXISTS \"app\";", StringComparison.Ordinal);
        Assert.IsTrue(create >= 0 && create < script.IndexOf("SET SCHEMA", StringComparison.Ordinal), script);
    }

    [TestMethod]
    [DataRow("pg_catalog")]
    [DataRow("information_schema")]
    [DataRow("pg_temp")]
    public void ASystemSchemaIsNeverCreated(string schema)
    {
        var script = ScriptFor(DatabaseModel.Empty, Build($"CREATE EXTENSION cube SCHEMA {schema};"));

        Assert.IsFalse(script.Contains("CREATE SCHEMA", StringComparison.Ordinal), script);
        StringAssert.Contains(script, $"CREATE EXTENSION cube SCHEMA {schema};", script);
    }

    [TestMethod]
    public void AnExtensionIsDroppedBeforeTheOneItRequires()
    {
        // The database's earthdistance requires cube; alphabetically, cube would be dropped first, which PostgreSQL refuses.
        var live = Build("CREATE EXTENSION cube; CREATE EXTENSION earthdistance;");
        var types = live.Types.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        types["extension:cube"] = types["extension:cube"] with { ExtensionRequires = [] };
        types["extension:earthdistance"] = types["extension:earthdistance"] with { ExtensionRequires = ["extension:cube"] };
        live.Types = types;

        var script = ScriptFor(live, Build("CREATE TABLE public.t (a integer);"));

        var earthdistance = script.IndexOf("DROP EXTENSION \"earthdistance\";", StringComparison.Ordinal);
        Assert.IsTrue(earthdistance >= 0 && earthdistance < script.IndexOf("DROP EXTENSION \"cube\";", StringComparison.Ordinal), script);
    }

    private static string ScriptFor(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().GenerateSqlScript(new ScriptGenerator().Generate(
            new DiffEngine().Diff(oldModel, newModel),
            new DependencyAnalyzer().Analyze(oldModel, newModel)));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
