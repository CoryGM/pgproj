using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-46: extensions deploy with <c>CREATE EXTENSION</c>/<c>DROP EXTENSION</c> (not the type statements), are created
/// before and dropped after everything else, and are never redeployed as drop-and-recreate.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ExtensionOperationTests
{
    [TestMethod]
    // Deployed as written (G-213).
    [DataRow("CREATE EXTENSION citext SCHEMA public;", "CREATE EXTENSION citext SCHEMA public;")]
    [DataRow("CREATE EXTENSION \"uuid-ossp\" SCHEMA public;", "CREATE EXTENSION \"uuid-ossp\" SCHEMA public;")]
    [DataRow("CREATE EXTENSION hstore SCHEMA app;", "CREATE EXTENSION hstore SCHEMA app;")]
    public void CreateEmitsCreateExtension(string sql, string expected)
    {
        var script = ScriptFor(DatabaseModel.Empty, BuildModel(sql));

        StringAssert.Contains(script, expected, script);
        Assert.IsFalse(script.Contains("CREATE TYPE", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("-- TODO", StringComparison.Ordinal), script);
    }

    [TestMethod]
    [DataRow("CREATE EXTENSION citext SCHEMA public;", "DROP EXTENSION \"citext\";")]
    [DataRow("CREATE EXTENSION \"uuid-ossp\" SCHEMA public;", "DROP EXTENSION \"uuid-ossp\";")]
    public void RemovalEmitsDropExtensionNotDropType(string sql, string expected)
    {
        var script = ScriptFor(BuildModel(sql), DatabaseModel.Empty);

        StringAssert.Contains(script, expected, script);
        Assert.IsFalse(script.Contains("DROP TYPE", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ProjectDeclaringPlpgsqlEmitsNothingAgainstAnEmptyModel()
    {
        var batch = Generate(DatabaseModel.Empty, BuildModel("CREATE EXTENSION plpgsql SCHEMA pg_catalog;"));

        Assert.IsEmpty(batch.Operations);
    }

    [TestMethod]
    public void ExtensionsAreCreatedBeforeEverythingElseEvenWhenTheirNameSortsLast()
    {
        var batch = Generate(DatabaseModel.Empty, BuildModel("""
            CREATE TABLE public.a_table (id bigint PRIMARY KEY, code text);
            CREATE FUNCTION public.a_fn() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
            CREATE EXTENSION "zzz-ext" SCHEMA public;
            """));

        var ids = batch.Operations.Select(static operation => operation.NodeId).ToList();
        var extensionIndex = ids.IndexOf("extension:zzz-ext");

        Assert.AreEqual(0, extensionIndex, string.Join(", ", ids));
    }

    [TestMethod]
    public void ExtensionsAreDroppedAfterEverythingElse()
    {
        var oldModel = BuildModel("""
            CREATE EXTENSION "zzz-ext" SCHEMA public;
            CREATE TABLE public.a_table (id bigint PRIMARY KEY);
            """);

        var batch = Generate(oldModel, DatabaseModel.Empty);
        var drops = batch.Operations.Where(static operation => operation is DropOperation).Select(static operation => operation.NodeId).ToList();

        Assert.AreEqual("extension:zzz-ext", drops[^1], string.Join(", ", drops));
    }

    [TestMethod]
    public void ModifiedExtensionProducesNoOperation()
    {
        var oldExtension = new TypeDefinition("public.citext", "citext", "public", "extension", [], [], [], [], [], [], [], []);
        var newExtension = oldExtension with { Comment = "changed" };
        var change = new ModifiedChange
        {
            NodeId = oldExtension.Key,
            NodeKind = ChangeNodeKind.Type,
            OldNode = oldExtension,
            NewNode = newExtension,
            Details = [new ModificationDetail { PropertyName = "Comment", OldValue = null, NewValue = "changed" }],
        };
        var graph = new DependencyGraph();
        graph.AddNode(DependencyNode.FromSemanticNode(newExtension, []));

        var batch = new ScriptGenerator().Generate(new ChangeSet { Added = [], Removed = [], Modified = [change] }, graph);

        Assert.IsEmpty(batch.Operations, "A modified extension must never be redeployed as drop-and-recreate.");
    }

    [TestMethod]
    public void ExtensionOperationsAreNotReportedAsUnsupported()
    {
        var batch = Generate(DatabaseModel.Empty, BuildModel("CREATE EXTENSION citext SCHEMA public;"));

        Assert.IsEmpty(batch.GetUnsupportedOperations());
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(
            new DiffEngine().Diff(oldModel, newModel),
            new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static string ScriptFor(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().GenerateSqlScript(Generate(oldModel, newModel));

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
