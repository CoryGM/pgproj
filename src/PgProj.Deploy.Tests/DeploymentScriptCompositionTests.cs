using PgProj.Diff;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class DeploymentScriptCompositionTests
{
    [TestMethod]
    public void WriteOrdersPreDeployOperationsAndPostDeploy()
    {
        var batch = new OperationBatch
        {
            Operations =
            [
                new TestOperation("CREATE TABLE public.t (id integer);")
                {
                    NodeId = "test",
                    NodeKind = ChangeNodeKind.Table,
                }
            ],
            PreDeploymentScripts = ["SELECT 'pre';"],
            PostDeploymentScripts = ["SELECT 'post';"],
        };

        var script = new ScriptWriter().Write(batch);

        Assert.AreEqual(
            "SELECT 'pre';" + Environment.NewLine + Environment.NewLine
            + "CREATE TABLE public.t (id integer);" + Environment.NewLine + Environment.NewLine
            + "SELECT 'post';",
            script);
    }

    [TestMethod]
    public void WriteEmitsScriptsWhenThereAreNoGeneratedOperations()
    {
        var batch = new OperationBatch
        {
            Operations = [],
            PreDeploymentScripts = ["SELECT 'pre';"],
            PostDeploymentScripts = ["SELECT 'post';"],
        };

        var script = new ScriptWriter().Write(batch);

        Assert.AreEqual(
            "SELECT 'pre';" + Environment.NewLine + Environment.NewLine + "SELECT 'post';",
            script);
    }

    [TestMethod]
    public void GenerateOmitsWhitespaceOnlyScripts()
    {
        var changes = new ChangeSet
        {
            Added = [],
            Removed = [],
            Modified = [],
        };
        var graph = new Dependencies.DependencyGraph();

        var batch = new ScriptGenerator().Generate(changes, graph, " \r\n", "\t");

        Assert.HasCount(0, batch.PreDeploymentScripts);
        Assert.HasCount(0, batch.PostDeploymentScripts);
        Assert.AreEqual(string.Empty, new ScriptWriter().Write(batch));
    }

    private sealed record TestOperation : SqlOperation
    {
        private readonly string _sql;

        public TestOperation(string sql)
        {
            _sql = sql;
        }

        public override string GenerateSql() => _sql;
    }
}
