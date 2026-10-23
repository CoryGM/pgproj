using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-255 and G-256: <c>ALTER TABLE ... CLUSTER ON</c> and <c>REPLICA IDENTITY</c> were reported as not modeled and ignored. They are deployed after the index or key they name, changed in place,
/// and compared only when the project states them.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ClusterAndReplicaIdentityTests
{
    private const string Setup = """
        CREATE TABLE public.t (id integer NOT NULL, code text NOT NULL);
        CREATE UNIQUE INDEX t_code ON public.t (code);
        CREATE INDEX t_id ON public.t (id);
        """;

    [TestMethod]
    public void ClusterOnAnIndexIsDeployedAfterTheIndex()
    {
        var script = Script(DatabaseModel.Empty, Build(Setup + "ALTER TABLE public.t CLUSTER ON t_id;"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" CLUSTER ON \"t_id\";");
        Assert.IsTrue(script.IndexOf("CREATE INDEX", StringComparison.Ordinal) < script.IndexOf("CLUSTER ON", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ClusterOnAPrimaryKeyIsDeployedAfterTheKey()
    {
        var script = Script(DatabaseModel.Empty, Build("CREATE TABLE public.t (id integer PRIMARY KEY); ALTER TABLE public.t CLUSTER ON t_pkey;"));

        StringAssert.Contains(script, "CLUSTER ON \"t_pkey\";");
    }

    [TestMethod]
    public void ReplicaIdentityUsingAUniqueIndexIsDeployedAfterTheIndex()
    {
        var script = Script(DatabaseModel.Empty, Build(Setup + "ALTER TABLE public.t REPLICA IDENTITY USING INDEX t_code;"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" REPLICA IDENTITY USING INDEX \"t_code\";");
        Assert.IsTrue(script.IndexOf("CREATE UNIQUE INDEX", StringComparison.Ordinal) < script.IndexOf("REPLICA IDENTITY", StringComparison.Ordinal), script);
    }

    [TestMethod]
    [DataRow("FULL")]
    [DataRow("NOTHING")]
    public void ReplicaIdentityFullOrNothingIsDeployedWithTheTable(string mode)
    {
        var script = Script(DatabaseModel.Empty, Build(Setup + $"ALTER TABLE public.t REPLICA IDENTITY {mode};"));

        StringAssert.Contains(script, $"ALTER TABLE \"public\".\"t\" REPLICA IDENTITY {mode};");
    }

    [TestMethod]
    public void ChangedSettingsAreMadeInPlace()
    {
        var script = Script(
            Build(Setup),
            Build(Setup + "ALTER TABLE public.t CLUSTER ON t_id; ALTER TABLE public.t REPLICA IDENTITY USING INDEX t_code;"));

        StringAssert.Contains(script, "CLUSTER ON \"t_id\";");
        StringAssert.Contains(script, "REPLICA IDENTITY USING INDEX \"t_code\";");
        Assert.IsFalse(script.Contains("DROP", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("CREATE", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ATableReplicaIdentityChangesToFullInPlace()
    {
        Assert.AreEqual(
            "ALTER TABLE \"public\".\"t\" REPLICA IDENTITY FULL;",
            Script(Build(Setup), Build(Setup + "ALTER TABLE public.t REPLICA IDENTITY FULL;")));
    }

    [TestMethod]
    public void SettingsTheProjectDoesNotStateAreLeftAlone()
    {
        Assert.AreEqual(string.Empty, Script(
            Build(Setup + "ALTER TABLE public.t CLUSTER ON t_id; ALTER TABLE public.t REPLICA IDENTITY FULL;"),
            Build(Setup)));
    }

    [TestMethod]
    public void TheSameSettingsOnBothSidesAreNoChange()
    {
        var model = Build(Setup + "ALTER TABLE public.t CLUSTER ON t_id; ALTER TABLE public.t REPLICA IDENTITY USING INDEX t_code;");

        Assert.AreEqual(string.Empty, Script(model, model));
    }

    [TestMethod]
    public void SetWithoutClusterUnclustersATableThatIsClustered()
    {
        Assert.AreEqual(
            "ALTER TABLE \"public\".\"t\" SET WITHOUT CLUSTER;",
            Script(Build(Setup + "ALTER TABLE public.t CLUSTER ON t_id;"), Build(Setup + "ALTER TABLE public.t SET WITHOUT CLUSTER;")));
    }

    [TestMethod]
    public void SetWithoutClusterUnclustersATableClusteredOnAPrimaryKey()
    {
        Assert.AreEqual(
            "ALTER TABLE \"public\".\"t\" SET WITHOUT CLUSTER;",
            Script(
                Build("CREATE TABLE public.t (id integer PRIMARY KEY); ALTER TABLE public.t CLUSTER ON t_pkey;"),
                Build("CREATE TABLE public.t (id integer PRIMARY KEY); ALTER TABLE public.t SET WITHOUT CLUSTER;")));
    }

    [TestMethod]
    public void SetWithoutClusterOnATableThatIsNotClusteredIsNoChange()
    {
        Assert.AreEqual(string.Empty, Script(Build(Setup), Build(Setup + "ALTER TABLE public.t SET WITHOUT CLUSTER;")));
    }

    [TestMethod]
    public void SetWithoutClusterIsNothingToCreateOnANewTable()
    {
        var script = Script(DatabaseModel.Empty, Build(Setup + "ALTER TABLE public.t SET WITHOUT CLUSTER;"));

        Assert.IsFalse(script.Contains("CLUSTER", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ClusteringOnAnotherIndexNeedsNoWithoutClusterStatement()
    {
        var script = Script(Build(Setup + "ALTER TABLE public.t CLUSTER ON t_id;"), Build(Setup + "ALTER TABLE public.t CLUSTER ON t_code;"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" CLUSTER ON \"t_code\";");
        Assert.IsFalse(script.Contains("WITHOUT CLUSTER", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ClusterOnAndSetWithoutClusterTogetherKeepClusterOnAndWarn()
    {
        foreach (var sql in new[] { "ALTER TABLE public.t CLUSTER ON t_id; ALTER TABLE public.t SET WITHOUT CLUSTER;", "ALTER TABLE public.t SET WITHOUT CLUSTER; ALTER TABLE public.t CLUSTER ON t_id;" })
        {
            var model = Build(Setup + sql);

            Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("CLUSTER ON wins", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
            StringAssert.Contains(Script(DatabaseModel.Empty, model), "CLUSTER ON \"t_id\";");
        }
    }

    [TestMethod]
    public void SetWithoutClusterOnATableTheProjectDoesNotDeclareIsAWarning()
    {
        var model = Build("ALTER TABLE public.nope SET WITHOUT CLUSTER;");

        Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("does not declare that table", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
